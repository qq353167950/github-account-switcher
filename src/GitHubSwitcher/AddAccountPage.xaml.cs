using System;
using System.IO;
using System.Threading;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitHubSwitcher.Models;
using GitHubSwitcher.Services;

namespace GitHubSwitcher;

public partial class AddAccountPage : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private readonly GitHubApi _api = new();
    private CancellationTokenSource? _cts;

    private string _pendingLogin = "";
    private long _pendingId;
    private string _pendingEmail = "";
    private string _pendingToken = "";

    public AddAccountPage(MainWindow window)
    {
        InitializeComponent();
        _window = window;
    }

    public void Refresh()
    {
        // 页面无需自动刷新；保留接口一致性。
    }

    private string _pendingChromeDir = "";

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        StartBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        DoneCard.Visibility = Visibility.Collapsed;
        BrowserNote.Visibility = Visibility.Collapsed;
        WaitText.Text = "正在申请授权码…";

        try
        {
            var clientId = App.Store.Data.OAuthClientId;
            var info = await _api.StartDeviceFlowAsync(clientId, "repo,read:org,gist,workflow", ct)
                                  .ConfigureAwait(true);

            CodeCard.Visibility = Visibility.Visible;
            LinkBox.Text = info.VerificationUri;
            CodeBox.Text = info.UserCode;
            WaitText.Text = "等待你在浏览器中完成授权…（窗口可留着不动）";

            // 关键修复：从一开始就用【专属隔离目录】打开浏览器，
            // 否则登录产生的 Cookie 会落进你日常的 Chrome，切换过去时仍是未登录。
            if (string.IsNullOrWhiteSpace(_pendingChromeDir))
                _pendingChromeDir = AppPaths.NewPendingChromeDir();

            try
            {
                var url = $"{info.VerificationUri}?user_code={Uri.EscapeDataString(info.UserCode)}";
                ChromeLauncher.Open(_pendingChromeDir, url);
                BrowserNote.Visibility = Visibility.Visible;
                BrowserNoteText.Text =
                    "已用该账号的独立窗口打开。请在这个窗口里登录要添加的账号（如需换号，点「Use a different account」）。";
            }
            catch (Exception ex)
            {
                BrowserNote.Visibility = Visibility.Visible;
                BrowserNoteText.Text = $"无法自动打开浏览器，请手动复制链接。({ex.Message})";
            }

            // 关键：持续轮询，直到用户在浏览器里完成登录
            var token = await _api.PollForTokenAsync(clientId, info, ct).ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(token))
            {
                WaitText.Text = "你取消了授权。";
                return;
            }

            WaitText.Text = "授权成功，正在读取账号信息…";

            var user = await _api.GetUserAsync(token, ct).ConfigureAwait(true);
            if (user == null || string.IsNullOrWhiteSpace(user.Login))
            {
                WaitText.Text = "授权成功但无法读取账号信息，请重试。";
                return;
            }

            _pendingLogin = user.Login;
            _pendingId = user.Id;
            _pendingEmail = !string.IsNullOrWhiteSpace(user.Email)
                ? user.Email!
                : $"{user.Id}+{user.Login}@users.noreply.github.com";
            _pendingToken = token;

            CodeCard.Visibility = Visibility.Collapsed;
            DoneCard.Visibility = Visibility.Visible;
            LabelBox.Text = user.Login;
            DoneText.Text =
                $"账号：{user.Login}\n" +
                $"显示名：{(string.IsNullOrWhiteSpace(user.Name) ? "（未设置）" : user.Name)}\n" +
                $"提交署名邮箱：{_pendingEmail}\n\n" +
                "点「保存账号」后，程序会为它建立独立的浏览器窗口，并注册到命令行凭据库。";
            WaitText.Text = "";
        }
        catch (OperationCanceledException)
        {
            WaitText.Text = "已取消等待。";
        }
        catch (Exception ex)
        {
            WaitText.Text = $"失败：{ex.Message}";
        }
        finally
        {
            StartBtn.IsEnabled = true;
            CancelBtn.IsEnabled = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        WaitText.Text = "已停止等待。";
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e) => CopyToClipboard(LinkBox.Text, "链接已复制");

    private void CopyCode_Click(object sender, RoutedEventArgs e) => CopyToClipboard(CodeBox.Text, "授权码已复制");

    private void CopyToClipboard(string text, string okMessage)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Clipboard.SetText(text);
                WaitText.Text = okMessage;
            }
        }
        catch (Exception ex)
        {
            WaitText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        var link = LinkBox.Text;
        if (string.IsNullOrWhiteSpace(link)) return;
        var url = string.IsNullOrWhiteSpace(CodeBox.Text)
            ? link
            : $"{link}?user_code={Uri.EscapeDataString(CodeBox.Text)}";

        // 始终用登录过程中那个隔离窗口打开，保证登录态落在同一环境
        try
        {
            if (string.IsNullOrWhiteSpace(_pendingChromeDir))
                _pendingChromeDir = AppPaths.NewPendingChromeDir();
            ChromeLauncher.Open(_pendingChromeDir, url);
        }
        catch
        {
            ChromeLauncher.OpenUrlInDefaultBrowser(url);
        }
        await Task.CompletedTask;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingLogin))
        {
            MessageBox.Show("没有待保存的账号，请先完成登录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 把登录期间使用的临时隔离目录改名成该账号的正式目录，
        // 这样登录态直接成为该账号的长期会话，不需要再登录一次。
        var finalDir = AppPaths.ChromeDirFor(_pendingLogin);
        var dirMoved = TryPromoteChromeDir(finalDir);

        var label = LabelBox.Text.Trim();
        var account = new Account
        {
            Login = _pendingLogin,
            Label = string.IsNullOrWhiteSpace(label) ? _pendingLogin : label,
            Email = _pendingEmail,
            GitHubId = _pendingId,
            ChromeUserDataDir = dirMoved ? finalDir : (_pendingChromeDir ?? finalDir)
        };

        App.Store.AddOrUpdate(account);
        SecureStore.SetToken(_pendingLogin, _pendingToken);

        // 注册到 gh，使命令行可用
        var (ok, msg) = await GhCli.RegisterTokenAsync(_pendingToken).ConfigureAwait(true);

        // 首个账号自动设为当前
        if (App.Store.Accounts.Count == 1 || string.IsNullOrWhiteSpace(App.Store.Data.ActiveLogin))
        {
            App.Store.SetActive(_pendingLogin);
            await GitConfig.ApplyIdentityAsync(account).ConfigureAwait(true);
        }

        DoneText.Text = ok
            ? $"已保存 {_pendingLogin}，命令行凭据也注册好了。"
            : $"已保存 {_pendingLogin}。命令行部分未完成：{msg}";

        MessageBox.Show($"账号 {_pendingLogin} 已添加。", "成功",
            MessageBoxButton.OK, MessageBoxImage.Information);

        _pendingLogin = "";
        _pendingToken = "";
        _pendingChromeDir = "";
        CodeCard.Visibility = Visibility.Collapsed;
        DoneCard.Visibility = Visibility.Collapsed;
        _window.NotifyChanged();
    }

    /// <summary>
    /// 把添加过程中使用的临时目录改名成正式目录。
    /// 若目标已存在（重复添加同一账号），则保留原目录，避免覆盖已有登录态。
    /// </summary>
    private bool TryPromoteChromeDir(string finalDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_pendingChromeDir)) return false;
            if (!Directory.Exists(_pendingChromeDir)) return false;

            if (Directory.Exists(finalDir))
            {
                // 已存在：说明该账号以前添加过，丢弃这次的临时目录
                TryDeleteDir(_pendingChromeDir);
                return false;
            }

            Directory.Move(_pendingChromeDir, finalDir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }

    private void GoSwitch_Click(object sender, RoutedEventArgs e) => _window.GoToSwitch();
}

// 便于 XAML 中使用
file static class WpfShim { }
