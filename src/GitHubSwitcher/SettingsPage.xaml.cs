using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GitHubSwitcher.Services;

namespace GitHubSwitcher;

public partial class SettingsPage : UserControl, IRefreshable
{
    private readonly MainWindow _window;

    public SettingsPage(MainWindow window)
    {
        InitializeComponent();
        _window = window;
    }

    public void Refresh()
    {
        UpdateAbe();
        UpdateDiagnostics();
    }

    // ---------------------------------------------------------------- ABE 状态

    private void UpdateAbe()
    {
        var s = AbeService.Inspect();
        var v20 = AbeService.CountV20Cookies();

        switch (s.State)
        {
            case AbeState.Disabled:
                SetBadge("未启用（正常）", "#E7F4EA", "#1A7F37");
                AbeExplain.Text =
                    "当前 Chrome 没有启用 App-Bound 加密，cookie 是可解密的 v10 格式，" +
                    "「切换后常用 Chrome 变成对应账号」功能可以正常工作。";
                AbeWarn.Visibility = Visibility.Collapsed;
                AbeDanger.Visibility = Visibility.Collapsed;
                AbeFixBtn.IsEnabled = true;
                AbeRepairBtn.IsEnabled = v20 > 0;
                AbeRestoreBtn.IsEnabled = false;
                break;

            case AbeState.DisabledByPolicy:
                SetBadge("已通过策略关闭", "#E7F4EA", "#1A7F37");
                AbeExplain.Text =
                    "已写入 Chrome 企业策略 ApplicationBoundEncryptionEnabled=0，" +
                    "Chrome 不会再启用 App-Bound 加密。功能可正常工作。" +
                    (v20 > 0 ? $"（另有 {v20} 条历史 v20 cookie 待清理）" : "");
                AbeWarn.Visibility = Visibility.Visible;
                AbeWarnText.Text =
                    "该策略会让 Chrome 显示「由贵单位管理」的提示。这是预期现象，" +
                    "点「恢复 Chrome 默认」可随时移除。";
                AbeDanger.Visibility = Visibility.Collapsed;
                AbeFixBtn.IsEnabled = false;
                AbeRepairBtn.IsEnabled = v20 > 0;
                AbeRestoreBtn.IsEnabled = true;
                break;

            case AbeState.Enabled:
                SetBadge("已启用（受影响）", "#FDECEA", "#C0392B");
                AbeExplain.Text =
                    $"Chrome 已启用 App-Bound 加密，现有 {v20} 条 cookie 是 v20 格式。" +
                    "密钥由 Chrome 的 elevation service 保管并校验调用方身份，" +
                    "外部程序无法解密，也无法把它降级成 v10。";
                AbeDanger.Visibility = Visibility.Visible;
                AbeDangerText.Text =
                    "可行的恢复路径：点「从 v20 修复并重置登录」—— 它会写入策略阻止今后产生 v20，" +
                    "并清掉这些读不了的 cookie，然后你重新登录一次即可（新 cookie 就是 v10）。" +
                    "代价是这些网站要重新登录。";
                AbeWarn.Visibility = Visibility.Collapsed;
                AbeFixBtn.IsEnabled = false;
                AbeRepairBtn.IsEnabled = true;
                AbeRestoreBtn.IsEnabled = true;
                break;

            default:
                SetBadge("无法判断", "#FFF8E6", "#B26A00");
                AbeExplain.Text = s.Problem ?? "未能读取 Chrome 的加密状态。";
                AbeWarn.Visibility = Visibility.Collapsed;
                AbeDanger.Visibility = Visibility.Collapsed;
                AbeFixBtn.IsEnabled = true;
                AbeRepairBtn.IsEnabled = v20 > 0;
                AbeRestoreBtn.IsEnabled = true;
                break;
        }
    }

    private void SetBadge(string text, string bg, string fg)
    {
        AbeBadgeText.Text = text;
        AbeBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        AbeBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
    }

    private static string DescribeAbe(AbeStatus s) => s.State switch
    {
        AbeState.Disabled => "未启用（正常）",
        AbeState.DisabledByPolicy => "已通过策略关闭",
        AbeState.Enabled => "已启用（受影响）",
        _ => "无法判断"
    };

    private void SetResult(string text, bool ok)
    {
        AbeResult.Text = text;
        AbeResult.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(ok ? "#1A7F37" : "#C0392B"));
    }

    // ---------------------------------------------------------------- 按钮

    private void AbeFix_Click(object sender, RoutedEventArgs e)
    {
        var dr = MessageBox.Show(
            "即将在注册表写入一条 Chrome 策略，禁止 Chrome 启用 App-Bound 加密。\n\n" +
            "· 只影响当前 Windows 用户（HKCU），不需要管理员权限\n" +
            "· 副作用：Chrome 会显示「由贵单位管理」的提示\n" +
            "· 可随时用「恢复 Chrome 默认」移除\n\n" +
            "继续吗？",
            "确认写入策略", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (dr != MessageBoxResult.Yes) return;

        var (ok, msg) = AbeService.DisableAbe();
        SetResult(msg, ok);

        if (ok)
        {
            MessageBox.Show(msg, "已写入策略", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateAbe();
            UpdateDiagnostics();
        }
    }

    /// <summary>从 v20 修复：关策略 + 清掉无法解密的 cookie，让用户重登。</summary>
    private void AbeRepair_Click(object sender, RoutedEventArgs e)
    {
        var v20 = AbeService.CountV20Cookies();
        if (v20 == 0)
        {
            MessageBox.Show(
                "没有检测到 v20 加密的 cookie，当前无需修复。\n\n" +
                "如果只是想预防将来产生 v20，用「关闭 App-Bound 加密」即可。",
                "无需修复", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dr = MessageBox.Show(
            $"检测到 {v20} 条 v20 加密的 cookie，本工具无法解密它们。\n\n" +
            "修复会做两件事：\n" +
            "  1. 写入策略，阻止 Chrome 今后再产生 v20\n" +
            $"  2. 删除这 {v20} 条无法解密的 cookie\n\n" +
            "代价：这些网站的登录状态会丢失，需要重新登录一次\n" +
            "（包括 GitHub；重登后产生的是可解密的 v10）。\n\n" +
            "请先完全退出 Chrome。确定继续吗？",
            "确认从 v20 修复", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (dr != MessageBoxResult.Yes) return;

        var (ok, msg) = AbeService.RepairFromV20(deleteCookies: true);
        SetResult(msg, ok);

        MessageBox.Show(msg, ok ? "修复完成" : "修复失败",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);

        UpdateAbe();
        UpdateDiagnostics();
    }

    private void AbeRestore_Click(object sender, RoutedEventArgs e)
    {
        var dr = MessageBox.Show(
            "移除策略后，Chrome 将恢复默认行为。\n" +
            "未来的 Chrome 版本可能会自动启用 App-Bound 加密。\n\n确定移除吗？",
            "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (dr != MessageBoxResult.Yes) return;

        var (ok, msg) = AbeService.RestoreDefault();
        SetResult(msg, ok);
        UpdateAbe();
        UpdateDiagnostics();
    }

    /// <summary>
    /// 重新检测。
    /// 说明：如果检测结果与上次相同，界面上看不出变化，用户会以为按钮没反应。
    /// 所以这里必须给出可见反馈：时间戳 + 关键结论。
    /// </summary>
    private void Recheck_Click(object sender, RoutedEventArgs e)
    {
        var v20Before = AbeService.CountV20Cookies();
        Refresh();
        var after = AbeService.Inspect();
        var v20After = AbeService.CountV20Cookies();

        SetResult(
            $"已于 {DateTime.Now:HH:mm:ss} 重新检测\n" +
            $"App-Bound 加密：{DescribeAbe(after)}；v20 cookie：{v20After} 条" +
            (v20Before == v20After ? "（与上次一致）" : $"（上次 {v20Before} 条）"),
            after.CanInject);
    }

    // ---------------------------------------------------------------- 环境自检

    private void UpdateDiagnostics()
    {
        DiagList.Children.Clear();
        int fail = 0;

        var chrome = ChromeLauncher.FindChrome();
        AddItem(chrome != null, "Chrome 已安装", chrome ?? "未找到 chrome.exe");

        bool hasDb = false;
        try
        {
            hasDb = ChromeCookieStore.FindCookiesDb(BrowserProfileService.DefaultUserDataDir) != null;
        }
        catch { }
        AddItem(hasDb, "能找到常用 Chrome 的 cookie 数据库",
            hasDb ? BrowserProfileService.DefaultUserDataDir : "未找到 Cookies 文件");

        var key = ChromeCookieStore.TryGetMasterKey(BrowserProfileService.DefaultUserDataDir);
        var keyOk = key != null && key.Length == 32;
        AddItem(keyOk, "能解出 Chrome 的加密密钥",
            keyOk ? "已取得 32 字节主密钥" : "读取失败（可能权限不足）");
        if (!keyOk) fail++;

        string? login = null;
        try { login = BrowserProfileService.DetectCurrentLogin(); } catch { }
        AddItem(login != null, "能读出当前 Chrome 登录的 GitHub 账号",
            login ?? "读不到（可能未登录，或加密方式变化）");

        AddItem(GhCli.IsAvailable, "gh CLI 可用（命令行走它切换身份）",
            GhCli.IsAvailable ? "已检测到 gh" : "未检测到 gh，命令行部分将不可用");

        var abe = AbeService.Inspect();
        AddItem(abe.CanInject, "App-Bound 加密未阻止本功能",
            abe.CanInject ? "可以正常切换" : "受 App-Bound 加密影响");
        if (!abe.CanInject) fail++;

        var gitOk = ProcessRunner.Exists("git");
        AddItem(gitOk, "git 可用（用于写入提交署名）", gitOk ? "已检测到 git" : "未检测到 git");

        DiagFooter.Text = fail == 0
            ? "全部通过：切换后你常用 Chrome 会变成对应账号，且不影响书签。"
            : $"有 {fail} 项未通过，浏览器切换可能无法正常工作，请按上面提示处理。";
        DiagFooter.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(fail == 0 ? "#1A7F37" : "#C0392B"));
    }

    private void AddItem(bool ok, string title, string detail)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };

        row.Children.Add(new TextBlock
        {
            Text = ok ? "✓" : "✕",
            FontWeight = FontWeights.Bold,
            Width = 20,
            Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(ok ? "#1A7F37" : "#C0392B"))
        });

        row.Children.Add(new TextBlock { Text = title, FontSize = 13, Margin = new Thickness(0, 0, 10, 0) });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            row.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#616B76")),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        DiagList.Children.Add(row);
    }
}
