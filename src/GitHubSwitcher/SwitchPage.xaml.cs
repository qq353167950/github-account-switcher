using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using GitHubSwitcher.Services;

namespace GitHubSwitcher;

public partial class SwitchPage : UserControl, IRefreshable
{
    private readonly MainWindow _window;
    private readonly ObservableCollection<AccountRow> _rows = new();
    private CancellationTokenSource? _cts;

    public SwitchPage(MainWindow window)
    {
        InitializeComponent();
        _window = window;
        AccountList.ItemsSource = _rows;
    }

    public void Refresh()
    {
        _rows.Clear();
        foreach (var a in App.Store.Accounts)
        {
            _rows.Add(new AccountRow
            {
                Login = a.Login,
                Email = a.Email,
                IsActive = string.Equals(App.Store.Data.ActiveLogin, a.Login, StringComparison.OrdinalIgnoreCase),
                StatusText = SecureStore.HasToken(a.Login)
                    ? "浏览器窗口 + 命令行认证均可切换"
                    : "未保存 token（仅切换浏览器窗口）"
            });
        }

        var empty = _rows.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AccountList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        StatusLine.Text = empty
            ? "先在「添加账号」里登录一个 GitHub 账号。"
            : $"共 {_rows.Count} 个账号。";

        // 备份存在时显示「还原」入口
        UpdateRestoreCard();
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void GoAdd_Click(object sender, RoutedEventArgs e) => _window.GoToAdd();

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string login) return;

        var account = App.Store.Data.Find(login);
        if (account == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        btn.IsEnabled = false;
        StatusLine.Text = $"正在切换到 {login}…";

        try
        {
            // --- 1) 命令行认证：有 token 就用 gh 注册/切换 ---
            var token = SecureStore.GetToken(login);
            if (!string.IsNullOrWhiteSpace(token))
            {
                var (regOk, regMsg) = await GhCli.RegisterTokenAsync(token, ct).ConfigureAwait(true);
                if (!regOk)
                {
                    StatusLine.Text = $"命令行部分未完成：{regMsg}";
                }
                else
                {
                    var (swOk, swMsg) = await GhCli.SwitchAsync(login, ct).ConfigureAwait(true);
                    if (!swOk) StatusLine.Text = swMsg;
                }
            }

            // --- 2) 提交署名 ---
            var (gOk, gMsg) = await GitConfig.ApplyIdentityAsync(account, ct).ConfigureAwait(true);
            if (!gOk)
            {
                StatusLine.Text = $"切换取消：{gMsg}";
                return;
            }

            // --- 3) 浏览器：按模式处理 ---
            if (string.Equals(account.BrowserMode, Models.BrowserModes.Inject, StringComparison.OrdinalIgnoreCase))
            {
                // 方案 B：把登录态注入到你日常的 Chrome
                var br = BrowserProfileService.ApplyToDefaultChrome(account.ChromeUserDataDir, "Default", "Default", ct);
                if (!br.Ok)
                {
                    if (br.NeedCloseChrome)
                    {
                        var dr = MessageBox.Show(
                            br.Message + "\n\n要我现在帮你关闭 Chrome 吗？（会先保存你的标签页）",
                            "需要先关闭 Chrome", MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (dr == MessageBoxResult.Yes)
                        {
                            CloseChrome(account.ChromeUserDataDir);
                            br = BrowserProfileService.ApplyToDefaultChrome(account.ChromeUserDataDir, "Default", "Default", ct);
                        }
                    }
                    if (!br.Ok)
                    {
                        StatusLine.Text = $"浏览器部分未完成：{br.Message}";
                        return;
                    }
                }

                // 注入完成，然后用你的日常 Chrome 打开 github
                ChromeLauncher.OpenDefaultProfile("https://github.com");
                App.Store.SetActive(login);
                StatusLine.Text = $"已切换到 {login}：你常用的 Chrome 已是该账号，提交署名 {account.Email}。";
                _window.NotifyChanged();
                return;
            }

            // 方案 A：打开该账号专属浏览器窗口
            ChromeLauncher.Open(account.ChromeUserDataDir, "https://github.com");

            // --- 4) 记录 ---
            App.Store.SetActive(login);
            StatusLine.Text = $"已切换到 {login}：独立浏览器窗口已打开，提交署名为 {account.Email}。";
            _window.NotifyChanged();
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = "已取消。";
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"切换失败：{ex.Message}";
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    /// <summary>优雅关闭 Chrome：先正常关闭，再清理残留后台进程。</summary>
    private static void CloseChrome(string accountUserDataDir)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("chrome"))
            {
                try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
            }

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(150);
                if (System.Diagnostics.Process.GetProcessesByName("chrome").Length == 0) break;
            }

            foreach (var p in System.Diagnostics.Process.GetProcessesByName("chrome"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            System.Threading.Thread.Sleep(400);
        }
        catch { }
    }

    /// <summary>把日常 Chrome 恢复成切换前的登录态。</summary>
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserProfileService.IsChromeRunning())
        {
            var dr = MessageBox.Show(
                "Chrome 正在运行，无法安全还原。要我先关闭 Chrome 吗？\n（会先保存你的标签页）",
                "需要先关闭 Chrome", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (dr != MessageBoxResult.Yes) return;
            CloseChrome("");
        }

        var r = BrowserProfileService.Restore();
        StatusLine.Text = r.Ok ? $"{r.Message} 重开 Chrome 即可。" : $"还原失败：{r.Message}";
        if (r.Ok)
        {
            MessageBox.Show(
                "已还原。重开 Chrome 后，GitHub 会回到你切换前的那个账号。",
                "还原完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        UpdateRestoreCard();
    }

    private void UpdateRestoreCard()
    {
        var time = BrowserProfileService.BackupTime();
        RestoreCard.Visibility = time == null ? Visibility.Collapsed : Visibility.Visible;
        BackupInfo.Text = time == null ? "" : $"备份时间：{time:yyyy-MM-dd HH:mm}";
    }
}

public sealed class AccountRow
{
    public string Login { get; set; } = "";
    public string Email { get; set; } = "";
    public string StatusText { get; set; } = "";
    public bool IsActive { get; set; }
}
