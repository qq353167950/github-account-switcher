using System;
using System.IO;
using System.Text.Json;

namespace GitHubSwitcher.Services;

/// <summary>集中管理应用使用的目录与文件路径。</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GitHubSwitcher");

    public static string AccountsFile => Path.Combine(Root, "accounts.json");

    /// <summary>每个账号一个独立的 Chrome 数据目录，彻底隔离 Cookie。</summary>
    public static string ChromeRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GitHubSwitcher", "chrome");

    public static string ChromeDirFor(string login) =>
        Path.Combine(ChromeRoot, Sanitize(login));

    public static void EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChromeRoot);
    }

    /// <summary>应用图标绝对路径（托盘图标用）。</summary>
    public static string IconPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "GitHubSwitcher.exe");

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "default" : s;
    }

    /// <summary>
    /// 添加账号时用的一次性隔离目录。
    /// 因为此刻还不知道登录名，先给临时目录；登录成功后改名为正式目录，
    /// 保证登录产生的 Cookie 从第一步起就落在隔离环境里。
    /// </summary>
    public static string NewPendingChromeDir()
    {
        Directory.CreateDirectory(ChromeRoot);
        return Path.Combine(ChromeRoot, "_pending_" + Guid.NewGuid().ToString("N")[..8]);
    }
}
