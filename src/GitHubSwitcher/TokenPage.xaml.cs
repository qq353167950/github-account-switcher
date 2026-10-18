using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitHubSwitcher.Models;
using GitHubSwitcher.Services;

namespace GitHubSwitcher;

public partial class TokenPage : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private readonly GitHubApi _api = new();
    private readonly ObservableCollection<ScopeRow> _scopes = new();

    public TokenPage(MainWindow window)
    {
        InitializeComponent();
        _window = window;

        foreach (var s in ScopeCatalog.All)
        {
            _scopes.Add(new ScopeRow
            {
                Name = s.Name,
                Description = s.Description,
                IsDangerous = s.IsDangerous,
                IsSelected = s.IsDefault
            });
        }
        ScopeList.ItemsSource = _scopes;
        foreach (var r in _scopes) r.PropertyChanged += (_, _) => UpdateHint();

        UpdateHint();
    }

    /// <summary>选中指定账号（供「导入账号」后直接跳转过来使用）。</summary>
    public void SelectAccount(string login)
    {
        var target = App.Store.Accounts.FirstOrDefault(a =>
            string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
        if (target != null) AccountCombo.SelectedItem = target;
        ShowExisting();
    }

    public void Refresh()
    {
        var keep = (AccountCombo.SelectedItem as Account)?.Login;
        AccountCombo.ItemsSource = null;
        AccountCombo.ItemsSource = App.Store.Accounts.ToList();

        if (keep != null)
        {
            var again = App.Store.Accounts.FirstOrDefault(a =>
                string.Equals(a.Login, keep, StringComparison.OrdinalIgnoreCase));
            if (again != null) AccountCombo.SelectedItem = again;
        }
        if (AccountCombo.SelectedItem == null && App.Store.Accounts.Count > 0)
        {
            var active = App.Store.Active ?? App.Store.Accounts[0];
            AccountCombo.SelectedItem = active;
        }

        ShowExisting();
    }

    private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowExisting();

    private void ShowExisting()
    {
        var acc = AccountCombo.SelectedItem as Account;
        if (acc == null)
        {
            ExistingCard.Visibility = Visibility.Collapsed;
            return;
        }

        var token = SecureStore.GetToken(acc.Login);
        if (string.IsNullOrWhiteSpace(token))
        {
            ExistingCard.Visibility = Visibility.Collapsed;
            return;
        }

        ExistingCard.Visibility = Visibility.Visible;
        ExistingToken.Text = token;
        ExistingScopes.Text = "点「校验并保存」可刷新它的权限列表。";
    }

    // ---------------------------------------------------------------- 权限与链接

    private string[] SelectedScopes() =>
        _scopes.Where(s => s.IsSelected).Select(s => s.Name).ToArray();

    private void UpdateHint()
    {
        var sel = SelectedScopes();
        ScopeHint.Text = sel.Length == 0
            ? "未选择任何权限 —— 这样的 token 只能读公开信息。"
            : $"已选 {sel.Length} 项：{string.Join(", ", sel)}";
    }

    private string BuildCreateUrl(Account acc)
    {
        var scopes = string.Join(",", SelectedScopes());
        var desc = Uri.EscapeDataString($"GitHubSwitcher-{acc.Login}");
        var url = "https://github.com/settings/tokens/new";
        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(scopes)) q.Add($"scopes={scopes}");
        q.Add($"description={desc}");
        return q.Count > 0 ? $"{url}?{string.Join("&", q)}" : url;
    }

    private void BuildLink_Click(object sender, RoutedEventArgs e)
    {
        var acc = AccountCombo.SelectedItem as Account;
        if (acc == null)
        {
            MessageBox.Show("请先添加并选择一个账号。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        LinkPreview.Text = BuildCreateUrl(acc);
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var acc = AccountCombo.SelectedItem as Account;
        if (acc == null)
        {
            MessageBox.Show("请先添加并选择一个账号。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var url = BuildCreateUrl(acc);
        LinkPreview.Text = url;

        // 关键：用该账号专属的 Chrome 窗口打开，保证是新账号登录态下创建 token
        try
        {
            ChromeLauncher.Open(acc.ChromeUserDataDir, url);
            TokenStatus.Text = $"已在 {acc.Login} 的浏览器窗口打开创建页面，选择好权限点 Generate token，然后把 token 粘到下面。";
        }
        catch (Exception ex)
        {
            ChromeLauncher.OpenUrlInDefaultBrowser(url);
            TokenStatus.Text = $"无法用专属窗口打开（{ex.Message}），已用默认浏览器打开。";
        }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        var acc = AccountCombo.SelectedItem as Account;
        var text = string.IsNullOrWhiteSpace(LinkPreview.Text)
            ? (acc == null ? "" : BuildCreateUrl(acc))
            : LinkPreview.Text;

        if (string.IsNullOrWhiteSpace(text)) return;
        LinkPreview.Text = text;
        try
        {
            Clipboard.SetText(text);
            TokenStatus.Text = "创建链接已复制。";
        }
        catch (Exception ex) { TokenStatus.Text = $"复制失败：{ex.Message}"; }
    }

    // ---------------------------------------------------------------- 保存

    private async void SaveToken_Click(object sender, RoutedEventArgs e)
    {
        var acc = AccountCombo.SelectedItem as Account;
        if (acc == null)
        {
            MessageBox.Show("请先添加并选择一个账号。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var token = TokenInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            TokenStatus.Text = "请先粘贴 token。";
            return;
        }

        if (!token.StartsWith("ghp_", StringComparison.Ordinal) &&
            !token.StartsWith("github_pat_", StringComparison.Ordinal) &&
            !token.StartsWith("gho_", StringComparison.Ordinal))
        {
            TokenStatus.Text = "这看起来不是 GitHub token（应以 ghp_ 或 github_pat_ 开头）。";
            return;
        }

        TokenStatus.Text = "正在校验…";
        try
        {
            var user = await _api.GetUserAsync(token, CancellationToken.None).ConfigureAwait(true);
            if (user == null)
            {
                TokenStatus.Text = "校验失败：token 无效或已撤销。";
                return;
            }

            if (!string.Equals(user.Login, acc.Login, StringComparison.OrdinalIgnoreCase))
            {
                var dr = MessageBox.Show(
                    $"这个 token 属于 {user.Login}，不是当前选择的 {acc.Login}。\n\n仍要保存吗？",
                    "账号不匹配", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (dr != MessageBoxResult.Yes) return;
            }

            var scopes = await _api.GetScopesAsync(token, CancellationToken.None).ConfigureAwait(true);

            SecureStore.SetToken(acc.Login, token);
            TokenInput.Text = "";

            TokenStatus.Text = scopes.Length == 0
                ? $"已保存。该 token 权限：未返回（fine-grained token 常见）。"
                : $"已保存。实际权限（{scopes.Length} 项）：{string.Join(", ", scopes)}";

            ShowExisting();
            ExistingScopes.Text = scopes.Length == 0
                ? "fine-grained token（权限由 GitHub 页面上按仓库授予）"
                : $"实际权限：{string.Join(", ", scopes)}";

            // 顺便注册到 gh，命令行立刻可用
            var (ok, msg) = await GhCli.RegisterTokenAsync(token).ConfigureAwait(true);
            if (!ok) TokenStatus.Text += $"\n命令行注册未完成：{msg}";

            _window.NotifyChanged();
        }
        catch (Exception ex)
        {
            TokenStatus.Text = $"保存失败：{ex.Message}";
        }
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(TokenInput.Text))
            {
                Clipboard.SetText(TokenInput.Text.Trim());
                TokenStatus.Text = "已复制的输入框内容。";
            }
        }
        catch (Exception ex) { TokenStatus.Text = $"复制失败：{ex.Message}"; }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        TokenInput.Text = "";
        TokenStatus.Text = "";
    }

    private void CopyExisting_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(ExistingToken.Text))
            {
                Clipboard.SetText(ExistingToken.Text);
                TokenStatus.Text = "已保存的 token 已复制。";
            }
        }
        catch (Exception ex) { TokenStatus.Text = $"复制失败：{ex.Message}"; }
    }

    private void DeleteExisting_Click(object sender, RoutedEventArgs e)
    {
        var acc = AccountCombo.SelectedItem as Account;
        if (acc == null) return;

        if (MessageBox.Show($"确定删除 {acc.Login} 的本地 token？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        SecureStore.RemoveToken(acc.Login);
        ExistingCard.Visibility = Visibility.Collapsed;
        TokenStatus.Text = "已删除本地 token。";
        _window.NotifyChanged();
    }
}

/// <summary>权限列表的一行，支持勾选。</summary>
public sealed class ScopeRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsDangerous { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
        }
    }

    public Visibility DangerVisibility => IsDangerous ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
