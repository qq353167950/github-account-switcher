using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GitHubSwitcher.Services;

namespace GitHubSwitcher;

public partial class App : Application
{
    public static AccountStore Store { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.EnsureRoot();
        Store.Load();

        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            ShowError(args.Exception.GetBaseException().Message);
        };
    }

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowError(e.Exception.GetBaseException().Message);
    }

    private static void ShowError(string message)
    {
        MessageBox.Show(message, "出错了", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
