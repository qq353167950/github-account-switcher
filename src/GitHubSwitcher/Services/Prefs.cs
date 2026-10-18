using System;
using System.IO;
using System.Text.Json;

namespace GitHubSwitcher.Services;

/// <summary>
/// 应用偏好设置。用自管的 JSON 文件持久化（%APPDATA%\GitHubSwitcher\prefs.json），
/// 避免 ApplicationSettingsBase 在 .NET Core 下需要额外包的问题。
/// </summary>
public static class Prefs
{
    private static readonly object Gate = new();

    private static string File => Path.Combine(AppPaths.Root, "prefs.json");

    private static Dictionary<string, object?> Load()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(
                System.IO.File.ReadAllText(File)) ?? new();
        }
        catch { return new(); }
    }

    private static void Save(Dictionary<string, object?> map)
    {
        AppPaths.EnsureRoot();
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(map));
    }

    private static bool? _closeToTray;

    /// <summary>点 X 时是否最小化到托盘。默认 false（直接退出）。</summary>
    public static bool CloseToTray
    {
        get
        {
            lock (Gate)
            {
                if (_closeToTray == null)
                {
                    var m = Load();
                    _closeToTray = m.TryGetValue("closeToTray", out var v) && v is bool b && b;
                    _hasValue = m.ContainsKey("closeToTray");
                }
                return _closeToTray.Value;
            }
        }
        set
        {
            lock (Gate)
            {
                _closeToTray = value;
                _hasValue = true;
                var m = Load();
                m["closeToTray"] = value;
                Save(m);
            }
        }
    }

    private static bool _hasValue;

    /// <summary>用户是否已做过选择（决定关闭时是否弹询问框）。</summary>
    public static bool HasCloseChoice
    {
        get
        {
            lock (Gate)
            {
                if (_hasValue) return true;
                _ = CloseToTray; // 触发懒加载
                return _hasValue;
            }
        }
    }
}
