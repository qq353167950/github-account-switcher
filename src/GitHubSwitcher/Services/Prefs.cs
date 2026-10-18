using System;
using System.IO;
using System.Text.Json;

namespace GitHubSwitcher.Services;

/// <summary>
/// 应用偏好设置。用自管的 JSON 文件持久化（%APPDATA%\GitHubSwitcher\prefs.json）。
/// </summary>
public static class Prefs
{
    private static readonly object Gate = new();
    private static bool? _closeToTray;
    private static bool _loaded;

    private static string FilePath => Path.Combine(AppPaths.Root, "prefs.json");

    private sealed class PrefsData
    {
        public bool CloseToTray { get; set; }
    }

    private static PrefsData Load()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<PrefsData>(
                System.IO.File.ReadAllText(FilePath)) ?? new();
        }
        catch { return new(); }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _closeToTray = Load().CloseToTray;
        _loaded = true;
    }

    /// <summary>点 X 时是否最小化到托盘。默认 false（直接退出）。</summary>
    public static bool CloseToTray
    {
        get { lock (Gate) { EnsureLoaded(); return _closeToTray!.Value; } }
        set
        {
            lock (Gate)
            {
                _closeToTray = value;
                _loaded = true;
                AppPaths.EnsureRoot();
                System.IO.File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(new PrefsData { CloseToTray = value }));
            }
        }
    }

    /// <summary>用户是否已做过选择（决定关闭时是否弹询问框）。</summary>
    public static bool HasCloseChoice
    {
        get
        {
            lock (Gate)
            {
                if (!_loaded)
                {
                    // 文件存在即视为已选择过（文件只在用户选择后写入）
                    _closeToTray = Load().CloseToTray;
                    _loaded = true;
                }
                return System.IO.File.Exists(FilePath);
            }
        }
    }
}
