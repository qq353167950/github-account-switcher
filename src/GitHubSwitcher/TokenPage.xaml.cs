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
    private readonly ObservableCollection<ScopeGroup> _groups = new();
    private bool _syncing;

    public TokenPage(MainWindow window)
    {
        InitializeComponent();
        _window = window;

        foreach (var cat in ScopeCatalog.CategoryOrder)
        {
            var group = new ScopeGroup { Title = cat };
            foreach (var s in ScopeCatalog.All.Where(s => s.Category == cat))
            {
                group.Rows.Add(new ScopeRow
                {
                    Name = s.Name,
                    Description = s.Description,
                    IsDangerous = s.IsDangerous,
                    IsSelected = s.IsDefault
                });
            }
            _groups.Add(group);
        }

        // 摘要跟随勾选变化
        foreach (var g in _groups)
            foreach (var r in g.Rows)
                r.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ScopeRow.IsSelected))
                    {
                        ApplyImplication(r);
                        g.RefreshSummary();
                    }
                    UpdateHint();
                };

        GroupList.ItemsSource = _groups;
        foreach (var g in _groups) g.RefreshSummary();

        // 有效期选项（Days：null = 无期限；-1 = 自定义日期）
        ExpirationCombo.ItemsSource = new List<ExpirationOption>
        {
            new() { Label = "无期限（不过期）", Days = null },
            new() { Label = "7 天",   Days = 7 },
            new() { Label = "30 天",  Days = 30 },
            new() { Label = "60 天",  Days = 60 },
            new() { Label = "90 天",  Days = 90 },
            new() { Label = "自定义日期…", Days = -1 },
        };
        ExpirationCombo.SelectedIndex = 0;

        UpdateHint();
    }

    // ---------------------------------------------------------------- 权限联动

    private IEnumerable<ScopeRow> AllRows => _groups.SelectMany(g => g.Rows);

    /// <summary>勾选 admin:* 时自动勾选被蕴含的 read:/write:* 并标注；取消时随之取消。</summary>
    private void ApplyImplication(ScopeRow changed)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            if (changed.IsSelected &&
                ScopeCatalog.Implies.TryGetValue(changed.Name, out var children))
            {
                foreach (var c in children)
                {
                    var row = AllRows.FirstOrDefault(s =>
                        string.Equals(s.Name, c, StringComparison.OrdinalIgnoreCase));
                    if (row == null) continue; // repo 的子项不在 UI 列表里
                    row.IsSelected = true;
                    row.IncludedBy = changed.Name;
                }
            }
            else if (!changed.IsSelected)
            {
                foreach (var row in AllRows.Where(s => s.IncludedBy == changed.Name))
                {
                    row.IncludedBy = "";
                    row.IsSelected = false;
                }
            }

            if (!changed.IsSelected && !string.IsNullOrEmpty(changed.IncludedBy))
                changed.IncludedBy = "";
        }
        finally { _syncing = false; }
    }

    // ---------------------------------------------------------------- 全选/清空/默认

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        try
        {
            foreach (var r in AllRows)
            {
                r.IsSelected = true;
                if (ScopeCatalog.Implies.TryGetValue(r.Name, out var children))
                    foreach (var c in children)
                    {
                        var row = AllRows.FirstOrDefault(s =>
                            string.Equals(s.Name, c, StringComparison.OrdinalIgnoreCase));
                        if (row != null) { row.IsSelected = true; row.IncludedBy = r.Name; }
                    }
            }
        }
        finally { _syncing = false; }
        foreach (var g in _groups) g.RefreshSummary();
        UpdateHint();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        try
        {
            foreach (var r in AllRows) { r.IsSelected = false; r.IncludedBy = ""; }
        }
        finally { _syncing = false; }
        foreach (var g in _groups) g.RefreshSummary();
        UpdateHint();
    }

    private void SelectDefault_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        try
        {
            foreach (var r in AllRows)
            {
                r.IncludedBy = "";
                r.IsSelected = ScopeCatalog.Find(r.Name)?.IsDefault == true;
            }
        }
        finally { _syncing = false; }
        foreach (var g in _groups) g.RefreshSummary();
        UpdateHint();
    }

    // ---------------------------------------------------------------- 有效期

    private void ExpirationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExpirationDate == null) return;
        var opt = ExpirationCombo.SelectedItem as ExpirationOption;
        var custom = opt?.Days == -1;
        ExpirationDate.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        ExpirationHint.Text = custom
            ? "输入具体日期，格式 YYYY-MM-DD"
            : (opt?.Days == null ? "token 永不过期" : $"token 将在 {opt!.Days} 天后过期");
    }

    /// <summary>返回用户选择的到期时间；null = 无期限；自定义日期非法时返回 null 并提示。</summary>
    private DateTimeOffset? GetExpiration()
    {
        var opt = ExpirationCombo.SelectedItem as ExpirationOption;
        if (opt == null || opt.Days == null) return null;
        if (opt.Days == -1)
        {
            var d = ExpirationDate.Text.Trim();
            if (DateOnly.TryParseExact(d, "yyyy-MM-dd", out var parsed))
                return new DateTimeOffset(parsed.ToDateTime(TimeOnly.MaxValue), TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
            TokenStatus.Text = "自定义日期格式应为 YYYY-MM-DD，将按无期限生成链接。";
            return null;
        }
        return DateTimeOffset.Now.AddDays(opt.Days.Value);
    }

    private sealed class ExpirationOption
    {
        public string Label { get; init; } = "";
        public int? Days { get; init; }
    }

    // ---------------------------------------------------------------- 账号/显示

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

        var exp = SecureStore.GetExpiration(acc.Login);
        ExpirationInfo.Text = DescribeExpiration(exp);
        ExpirationInfo.Foreground = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                exp is { } e && e <= DateTimeOffset.Now ? "#C0392B" : "#1A7F37"));

        ExistingScopes.Text = "点「校验并保存」可刷新它的权限列表。";
    }

    /// <summary>生成到期时间的展示文案。</summary>
    public static string DescribeExpiration(DateTimeOffset? exp)
    {
        if (exp == null) return "有效期：无期限（不过期）";
        var remain = exp.Value - DateTimeOffset.Now;
        if (remain <= TimeSpan.Zero) return $"已过期（{exp.Value:yyyy-MM-dd}）—— 请重新创建 token";
        if (remain.TotalDays >= 1)
            return $"有效期至 {exp.Value:yyyy-MM-dd}（剩余 {Math.Floor(remain.TotalDays)} 天）";
        return $"有效期至 {exp.Value:yyyy-MM-dd HH:mm}（剩余 {Math.Ceiling(remain.TotalHours)} 小时）";
    }

    // ---------------------------------------------------------------- 权限与链接

    private string[] SelectedScopes() =>
        AllRows.Where(s => s.IsSelected).Select(s => s.Name).ToArray();

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
        var opt = ExpirationCombo.SelectedItem as ExpirationOption;
        if (opt is { Days: not null and not -1 })
            q.Add($"expiration={DateOnly.FromDateTime(DateTime.Today.AddDays(opt.Days.Value)):yyyy-MM-dd}");
        else if (opt?.Days == -1 && DateOnly.TryParseExact(ExpirationDate.Text.Trim(), "yyyy-MM-dd", out var d))
            q.Add($"expiration={d:yyyy-MM-dd}");
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

            // 记录到期时间：只有本页选择过有效期的 classic token 才有；gco_/gho_ 设备流 token 无期限
            DateTimeOffset? expiresAt = token.StartsWith("ghp_", StringComparison.Ordinal)
                ? GetExpiration() : null;

            SecureStore.SetToken(acc.Login, token, expiresAt);
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

/// <summary>权限分类分组（可折叠）。</summary>
public sealed class ScopeGroup : INotifyPropertyChanged
{
    private string _summary = "";

    public string Title { get; set; } = "";
    public ObservableCollection<ScopeRow> Rows { get; } = new();
    public bool IsExpanded { get; set; }

    /// <summary>收起时在标题旁显示已勾选的 scope 名，如「repo, workflow」。</summary>
    public string Summary
    {
        get => _summary;
        private set { _summary = value; Raise(); }
    }

    public void RefreshSummary()
    {
        var picked = Rows.Where(r => r.IsSelected).Select(r => r.Name).ToList();
        Summary = picked.Count == 0 ? "未选择" : string.Join(", ", picked);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>权限列表的一行，支持勾选。</summary>
public sealed class ScopeRow : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _includedBy = "";

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

    /// <summary>非空表示本行是被某个父 scope（如 admin:org）自动包含勾选的。</summary>
    public string IncludedBy
    {
        get => _includedBy;
        set
        {
            if (_includedBy == value) return;
            _includedBy = value;
            Raise();
            Raise(nameof(IncludedHint));
        }
    }

    public string IncludedHint =>
        string.IsNullOrEmpty(IncludedBy) ? "" : $"已包含于 {IncludedBy}";

    public Visibility DangerVisibility => IsDangerous ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
