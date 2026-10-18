using System;
using System.Windows;
using System.Windows.Controls;

namespace GitHubSwitcher;

public partial class MainWindow : Window
{
    private SwitchPage? _switchPage;
    private AddAccountPage? _addPage;
    private TokenPage? _tokenPage;
    private SettingsPage? _settingsPage;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowPage(NavSwitch);
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
