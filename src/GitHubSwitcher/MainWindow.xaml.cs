using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GitHubSwitcher.Services;
using WinForms = System.Windows.Forms;

namespace GitHubSwitcher;

public partial class MainWindow : Window
{
    private SwitchPage? _switchPage;
    private AddAccountPage? _addPage;
    private TokenPage? _tokenPage;
    private SettingsPage? _settingsPage;
    private WinForms.NotifyIcon? _tray;
    private bool _realExit;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowPage(NavSwitch);
        StateChanged += (_, _) =>
        {
            // 最小化时收进托盘，还原时隐藏气泡菜单残留
            if (_tray != null) _tray.Visible = (WindowState == WindowState.Minimized) || _tray.Visible;
        };
        Closing += MainWindow_Closing;
    }

    // ---------------------------------------------------------------- 托盘

    private WinForms.NotifyIcon Tray
    {
        get
        {
            if (_tray != null) return _tray;
            _tray = new WinForms.NotifyIcon
            {
                Text = "GitHub 账号切换器",
                Icon = System.Drawing.Icon.FromHandle(
                             new System.Drawing.Icon(AppPaths.IconPath, 32, 32).Handle),
                Visible = false
            };

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("打开主窗口", null, (_, _) => RestoreFromTray());
            menu.Items.Add(new WinForms.ToolStripSeparator());
            var activeItem = new WinForms.ToolStripMenuItem("当前账号：—") { Enabled = false };
            menu.Items.Add(activeItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _tray.ContextMenuStrip = menu;

            // 双击托盘图标还原
            _tray.DoubleClick += (_, _) => RestoreFromTray();
            // 悬停时显示当前账号
            _tray.MouseMove += (_, _) =>
            {
                var login = App.Store.Active?.Login ?? "未选择";
                if (activeItem.Text != $"当前账号：{login}")
                {
                    activeItem.Text = $"当前账号：{login}";
                    _tray.Text = $"GitHub 账号切换器 - {login}";
                }
            };
            return _tray;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_realExit) return;

        try
        {
            // 点 X：第一次询问用户偏好；之后按记住的选择执行
            if (!Prefs.HasCloseChoice)
            {
                var dr = MessageBox.Show(
                    "点击关闭按钮时希望怎么处理？\n\n" +
                    "「是」＝ 最小化到托盘（程序继续在后台运行，双击托盘图标还原）\n" +
                    "「否」＝ 退出程序\n\n" +
                    "（本设置会记住）",
                    "关闭方式", MessageBoxButton.YesNo, MessageBoxImage.Question);
                Prefs.CloseToTray = dr == MessageBoxResult.Yes;
            }
            if (Prefs.CloseToTray)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
        }
        catch (Exception ex)
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppPaths.Root, "tray_error.log"),
                $"{DateTime.Now}: {ex}");
        }
        ExitApp();
    }

    private void MinimizeToTray()
    {
        Tray.Visible = true;
        Hide();
        _tray?.ShowBalloonTip(1500, "GitHub 账号切换器",
            $"已最小化到托盘，当前账号：{App.Store.Active?.Login ?? "未选择"}", WinForms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Tray.Visible = false;
    }

    private void ExitApp()
    {
        _realExit = true;
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is RadioButton rb) ShowPage(rb);
    }

    private void ShowPage(RadioButton nav)
    {
        if (_switchPage == null) _switchPage = new SwitchPage(this);
        if (_addPage == null) _addPage = new AddAccountPage(this);
        if (_tokenPage == null) _tokenPage = new TokenPage(this);
        if (_settingsPage == null) _settingsPage = new SettingsPage(this);

        if (nav == NavAdd) Host.Content = _addPage;
        else if (nav == NavToken) Host.Content = _tokenPage;
        else if (nav == NavSettings) Host.Content = _settingsPage;
        else Host.Content = _switchPage;

        (Host.Content as IRefreshable)?.Refresh();
        UpdateSidebar();
    }

    /// <summary>供各页在数据变化后调用：刷新侧栏与当前页。</summary>
    public void NotifyChanged()
    {
        UpdateSidebar();
        (Host.Content as IRefreshable)?.Refresh();
    }

    public void GoToSwitch()
    {
        NavSwitch.IsChecked = true;
        ShowPage(NavSwitch);
    }

    public void GoToAdd()
    {
        NavAdd.IsChecked = true;
        ShowPage(NavAdd);
    }

    public void GoToSettings()
    {
        NavSettings.IsChecked = true;
        ShowPage(NavSettings);
    }

    /// <summary>跳到「创建 Token」页并预选指定账号。</summary>
    public void GoToToken(string login)
    {
        NavToken.IsChecked = true;
        ShowPage(NavToken);
        _tokenPage?.SelectAccount(login);
    }

    private void UpdateSidebar()
    {
        var active = App.Store.Active;
        SidebarActive.Text = active == null ? "未选择" : active.Login;
    }
}

/// <summary>页面刷新契约。</summary>
public interface IRefreshable
{
    void Refresh();
}
