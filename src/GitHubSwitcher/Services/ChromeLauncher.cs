using System;
using System.Diagnostics;
using System.IO;

namespace GitHubSwitcher.Services;

/// <summary>Chrome 浏览器启动与账号隔离目录管理。</summary>
public static class ChromeLauncher
{
    /// <summary>定位 chrome.exe，找不到返回 null。</summary>
    public static string? FindChrome()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// 用指定账号专属的数据目录打开 Chrome（方案 A：独立窗口）。
    /// 不同账号用不同 --user-data-dir，Cookie/登录态天然隔离。
    /// </summary>
    public static void Open(string userDataDir, string? url = null)
    {
        var chrome = FindChrome()
            ?? throw new InvalidOperationException(
                "未找到 Chrome。请安装 Google Chrome，或把 chrome.exe 放到标准路径。");

        Directory.CreateDirectory(userDataDir);

        var args = $"--user-data-dir=\"{userDataDir}\" " +
                   "--no-first-run --no-default-browser-check " +
                   "--disable-features=Translate,ChromeWhatsNewUI";
        if (!string.IsNullOrWhiteSpace(url))
            args += $" \"{url}\"";

        Process.Start(new ProcessStartInfo(chrome, args) { UseShellExecute = false });
    }

    /// <summary>
    /// 用「默认」用户目录打开 Chrome —— 就是你双击桌面图标打开的那个。
    /// 方案 B 注入 cookie 后，用它把页面直接开到日常窗口里。
    /// </summary>
    public static void OpenDefaultProfile(string? url = null)
    {
        var chrome = FindChrome()
            ?? throw new InvalidOperationException("未找到 Chrome。");

        var args = "--no-first-run --no-default-browser-check";
        if (!string.IsNullOrWhiteSpace(url))
            args += $" \"{url}\"";

        Process.Start(new ProcessStartInfo(chrome, args) { UseShellExecute = false });
    }

    /// <summary>用系统默认浏览器打开链接（兜底）。</summary>
    public static void OpenUrlInDefaultBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打不开就让用户手动复制链接 */ }
    }

    /// <summary>判断该数据目录下是否已有登录痕迹。</summary>
    public static bool HasProfile(string userDataDir) =>
        Directory.Exists(Path.Combine(userDataDir, "Default"));
}
