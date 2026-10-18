using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubSwitcher.Services;

public sealed class BrowserSwitchResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public bool NeedCloseChrome { get; set; }
    public int CookieCount { get; set; }
}

/// <summary>
/// 方案 B：把某个账号的 GitHub 登录态注入到你日常使用的 Chrome 里。
///
/// 关键前提（已实测）：
///  - 各 Chrome 用户目录的 master key 不同，需要解出明文再用目标密钥重加密
///  - Chrome 运行时会把 cookie 缓存在内存，可能回写覆盖，因此切换前建议关闭 Chrome
///  - 只动 host_key LIKE '%github%' 的行，绝不碰书签/历史/扩展/密码
/// </summary>
public sealed class BrowserProfileService
{
    /// <summary>你日常使用的 Chrome 用户数据目录。</summary>
    public static string DefaultUserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Google", "Chrome", "User Data");

    /// <summary>默认配置目录名（Chrome 的 "用户1"）。</summary>
    public const string DefaultProfileDir = "Default";

    private static string SnapshotDir => Path.Combine(AppPaths.Root, "browser-snapshots");

    // ---------------------------------------------------------------- 状态检测

    /// <summary>当前是否有 Chrome 进程在运行。</summary>
    public static bool IsChromeRunning()
    {
        try { return Process.GetProcessesByName("chrome").Length > 0; }
        catch { return false; }
    }

    /// <summary>Chrome 是否用了 App-Bound 加密（若是，方案 B 无法工作）。</summary>
    public static bool HasAppBoundEncryption(string userDataDir)
    {
        try
        {
            var ls = Path.Combine(userDataDir, "Local State");
            if (!File.Exists(ls)) return false;
            var json = JsonNode.Parse(File.ReadAllText(ls));
            var oc = json?["os_crypt"];
            return oc?["app_bound_encrypted_key"] != null;
        }
        catch { return false; }
    }

    /// <summary>能不能用方案 B。返回 null 表示可用，否则返回原因。</summary>
    public static string? CheckCapability(string userDataDir = "")
    {
        var ud = string.IsNullOrWhiteSpace(userDataDir) ? DefaultUserDataDir : userDataDir;
        if (!Directory.Exists(ud)) return $"找不到 Chrome 用户目录：{ud}";

        // 统一走 AbeService 判定，避免两处逻辑不一致
        var abe = AbeService.Inspect(ud);
        if (abe.State == AbeState.Enabled)
            return "Chrome 已启用 App-Bound 加密，无法直接改写 cookie。" +
                   "请到「设置」页查看说明，或把该账号改为独立窗口模式。";

        if (ChromeCookieStore.TryGetMasterKey(ud) == null)
            return "无法读取 Chrome 的加密密钥（可能权限不足）。";
        if (ChromeCookieStore.FindCookiesDb(ud) == null)
            return "找不到 Chrome 的 Cookies 数据库。";
        return null;
    }
    /// <summary>读出你日常 Chrome 当前登录的 GitHub 账号名（取不到返回 null）。</summary>
    public static string? DetectCurrentLogin(string userDataDir = "")
    {
        var ud = string.IsNullOrWhiteSpace(userDataDir) ? DefaultUserDataDir : userDataDir;
        var key = ChromeCookieStore.TryGetMasterKey(ud);
        var dbPath = ChromeCookieStore.FindCookiesDb(ud);
        if (key == null || dbPath == null) return null;

        try
        {
            var cookies = ChromeCookieStore.ReadGitHubCookies(dbPath);
            var dotcomUser = cookies.FirstOrDefault(c =>
                c.HostKey.Contains("github") && c.Name == "dotcom_user");
            if (dotcomUser == null) return null;

            var plain = ChromeCookieStore.Decrypt(key, dotcomUser.EncryptedValue);
            var value = ChromeCookieStore.ExtractValue(plain, dotcomUser.HostKey);
            var s = System.Text.Encoding.UTF8.GetString(value).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 备份 / 还原

    private static string SnapshotPath(string profileDir) =>
        Path.Combine(SnapshotDir, $"{AppPaths.Sanitize(profileDir)}.json");

    /// <summary>备份当前 GitHub cookie（以明文形式保存，方便还原）。</summary>
    public static bool Backup(string profileDir = DefaultProfileDir)
    {
        try
        {
            var dbPath = ChromeCookieStore.FindCookiesDb(DefaultUserDataDir, profileDir);
            var key = ChromeCookieStore.TryGetMasterKey(DefaultUserDataDir);
            if (dbPath == null || key == null) return false;

            Directory.CreateDirectory(SnapshotDir);
            var cookies = ChromeCookieStore.ReadGitHubCookies(dbPath);

            var payload = new JsonObject
            {
                ["savedAt"] = DateTimeOffset.Now.ToString("O"),
                ["profileDir"] = profileDir,
                ["items"] = new JsonArray(cookies.Select(c =>
                {
                    // 存明文，便于跨密钥还原
                    string? plainB64 = null;
                    try { plainB64 = Convert.ToBase64String(ChromeCookieStore.Decrypt(key, c.EncryptedValue)); }
                    catch { }

                    return (JsonNode)new JsonObject
                    {
                        ["hostKey"] = c.HostKey,
                        ["name"] = c.Name,
                        ["path"] = c.Path,
                        ["plain"] = plainB64,
                        ["expiresUtc"] = c.ExpiresUtc,
                        ["creationUtc"] = c.CreationUtc,
                        ["lastAccessUtc"] = c.LastAccessUtc,
                        ["lastUpdateUtc"] = c.LastUpdateUtc,
                        ["isSecure"] = c.IsSecure,
                        ["isHttpOnly"] = c.IsHttpOnly,
                        ["hasExpires"] = c.HasExpires,
                        ["isPersistent"] = c.IsPersistent,
                        ["priority"] = c.Priority,
                        ["sameSite"] = c.SameSite,
                        ["sourceScheme"] = c.SourceScheme,
                        ["sourcePort"] = c.SourcePort,
                        ["sourceType"] = c.SourceType,
                        ["hasCrossSiteAncestor"] = c.HasCrossSiteAncestor,
                        ["topFrameSiteKey"] = c.TopFrameSiteKey
                    };
                }).ToArray())
            };

            File.WriteAllText(SnapshotPath(profileDir),
                payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            return true;
        }
        catch { return false; }
    }

    public static bool HasBackup(string profileDir = DefaultProfileDir) =>
        File.Exists(SnapshotPath(profileDir));

    public static DateTimeOffset? BackupTime(string profileDir = DefaultProfileDir)
    {
        try
        {
            var p = SnapshotPath(profileDir);
            if (!File.Exists(p)) return null;
            var json = JsonNode.Parse(File.ReadAllText(p));
            return DateTimeOffset.TryParse(json?["savedAt"]?.GetValue<string>(), out var t) ? t : null;
        }
        catch { return null; }
    }

    /// <summary>从备份还原（明文 → 用当前密钥重新加密）。</summary>
    public static BrowserSwitchResult Restore(string profileDir = DefaultProfileDir)
    {
        var path = SnapshotPath(profileDir);
        if (!File.Exists(path))
            return new BrowserSwitchResult { Ok = false, Message = "没有找到备份文件。" };

        var key = ChromeCookieStore.TryGetMasterKey(DefaultUserDataDir);
        var dbPath = ChromeCookieStore.FindCookiesDb(DefaultUserDataDir, profileDir);
        if (key == null || dbPath == null)
            return new BrowserSwitchResult { Ok = false, Message = "无法访问 Chrome 数据。" };

        try
        {
            var json = JsonNode.Parse(File.ReadAllText(path));
            var items = json?["items"]?.AsArray() ?? new JsonArray();
            var cookies = new List<ChromeCookie>();

            foreach (var it in items)
            {
                if (it == null) continue;
                var plainB64 = it["plain"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(plainB64)) continue;

                cookies.Add(new ChromeCookie
                {
                    HostKey = it["hostKey"]?.GetValue<string>() ?? "",
                    Name = it["name"]?.GetValue<string>() ?? "",
                    Path = it["path"]?.GetValue<string>() ?? "/",
                    EncryptedValue = ChromeCookieStore.Encrypt(key, Convert.FromBase64String(plainB64)),
                    ExpiresUtc = it["expiresUtc"]?.GetValue<long>() ?? 0,
                    CreationUtc = it["creationUtc"]?.GetValue<long>() ?? 0,
                    LastAccessUtc = it["lastAccessUtc"]?.GetValue<long>() ?? 0,
                    LastUpdateUtc = it["lastUpdateUtc"]?.GetValue<long>() ?? 0,
                    IsSecure = it["isSecure"]?.GetValue<int>() ?? 0,
                    IsHttpOnly = it["isHttpOnly"]?.GetValue<int>() ?? 0,
                    HasExpires = it["hasExpires"]?.GetValue<int>() ?? 0,
                    IsPersistent = it["isPersistent"]?.GetValue<int>() ?? 0,
                    Priority = it["priority"]?.GetValue<int>() ?? 0,
                    SameSite = it["sameSite"]?.GetValue<int>() ?? 0,
                    SourceScheme = it["sourceScheme"]?.GetValue<int>() ?? 0,
                    SourcePort = it["sourcePort"]?.GetValue<int>() ?? 0,
                    SourceType = it["sourceType"]?.GetValue<int>() ?? 0,
                    HasCrossSiteAncestor = it["hasCrossSiteAncestor"]?.GetValue<int>() ?? 0,
                    TopFrameSiteKey = it["topFrameSiteKey"]?.GetValue<string>() ?? ""
                });
            }

            if (cookies.Count == 0)
                return new BrowserSwitchResult { Ok = false, Message = "备份里没有可还原的 cookie。" };

            ChromeCookieStore.ReplaceGitHubCookies(dbPath, key, key, cookies);
            return new BrowserSwitchResult { Ok = true, CookieCount = cookies.Count, Message = $"已还原 {cookies.Count} 条 cookie。" };
        }
        catch (Exception ex)
        {
            return new BrowserSwitchResult { Ok = false, Message = $"还原失败：{ex.Message}" };
        }
    }

    // ---------------------------------------------------------------- 切换

    /// <summary>
    /// 把某个账号的 GitHub 登录态写入你日常 Chrome 的 Default 配置。
    /// 源数据来自该账号的隔离目录。
    /// </summary>
    public static BrowserSwitchResult ApplyToDefaultChrome(
        string sourceUserDataDir, string sourceProfileDir = DefaultProfileDir,
        string targetProfileDir = DefaultProfileDir,
        CancellationToken ct = default)
    {
        var cap = CheckCapability();
        if (cap != null) return new BrowserSwitchResult { Ok = false, Message = cap };

        if (IsChromeRunning())
        {
            return new BrowserSwitchResult
            {
                Ok = false,
                NeedCloseChrome = true,
                Message = "Chrome 正在运行。请先完全退出 Chrome（含后台进程），否则写入的登录态会被覆盖。"
            };
        }

        var srcDb = ChromeCookieStore.FindCookiesDb(sourceUserDataDir, sourceProfileDir);
        if (srcDb == null)
            return new BrowserSwitchResult { Ok = false, Message = "该账号还没有浏览器登录记录，请先添加/登录一次。" };

        var srcKey = ChromeCookieStore.TryGetMasterKey(sourceUserDataDir);
        if (srcKey == null)
            return new BrowserSwitchResult { Ok = false, Message = "无法读取该账号目录的加密密钥。" };

        var dstDb = ChromeCookieStore.FindCookiesDb(DefaultUserDataDir, targetProfileDir);
        var dstKey = ChromeCookieStore.TryGetMasterKey(DefaultUserDataDir);
        if (dstDb == null || dstKey == null)
            return new BrowserSwitchResult { Ok = false, Message = "无法访问你日常 Chrome 的 cookie 数据库。" };

        try
        {
            var cookies = ChromeCookieStore.ReadGitHubCookies(srcDb);
            if (cookies.Count == 0)
                return new BrowserSwitchResult { Ok = false, Message = "该账号目录里没有 GitHub cookie。" };

            var (ok, total) = ChromeCookieStore.VerifyDecryptable(srcKey, cookies);
            if (ok != total)
                return new BrowserSwitchResult
                {
                    Ok = false,
                    Message = $"该账号的 {total - ok} 条 cookie 无法解密（可能已过期或加密方式变化），请重新登录一次。"
                };

            // 写入前先备份你原来的登录态，确保可一键还原
            Backup(targetProfileDir);

            ChromeCookieStore.ReplaceGitHubCookies(dstDb, srcKey, dstKey, cookies);
            return new BrowserSwitchResult
            {
                Ok = true,
                CookieCount = cookies.Count,
                Message = $"已把登录态写入你日常的 Chrome（{cookies.Count} 条 cookie）。"
            };
        }
        catch (Exception ex)
        {
            return new BrowserSwitchResult { Ok = false, Message = $"写入失败：{ex.Message}" };
        }
    }
}
