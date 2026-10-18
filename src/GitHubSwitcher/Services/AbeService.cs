using System;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace GitHubSwitcher.Services;

public enum AbeState
{
    /// <summary>未启用 App-Bound 加密，cookie 可正常读写。</summary>
    Disabled,

    /// <summary>已启用，方案 B 无法工作。</summary>
    Enabled,

    /// <summary>已通过策略显式关闭（本工具写入）。</summary>
    DisabledByPolicy,

    /// <summary>无法判断（读不到 Local State 等）。</summary>
    Unknown
}

public sealed class AbeStatus
{
    public AbeState State { get; set; }
    public bool PolicyApplied { get; set; }
    public bool PolicyValueIsOff { get; set; }
    public string UserDataDir { get; set; } = "";
    public string? Problem { get; set; }
    public bool HasAppBoundKey { get; set; }
    public bool CookiePrefixV10Only { get; set; }

    public bool CanInject => State == AbeState.Disabled || State == AbeState.DisabledByPolicy;
}

/// <summary>
/// App-Bound 加密（ABE）检测与开关。
///
/// 已在本机 Chrome 149 二进制中确认：存在企业策略 "ApplicationBoundEncryptionEnabled"。
/// 把它设为 0 可显式禁止 Chrome 启用 ABE，从而保证 cookie 仍是可解密的 v10 格式。
///
/// 重要事实：ABE 一旦真正启用，Chrome 会把已有 cookie 重新加密成 v20 且密钥绑定到
/// Chrome 自身进程（通过系统服务校验调用方路径）。这不是"改个设置就能回退"的东西——
/// 关闭策略只能阻止它将来启用，不能让已经 v20 的数据变回 v10。
/// 所以本工具的价值是「提前把闸门关上」，而不是「事后救人」。
/// </summary>
public static class AbeService
{
    private const string PolicyPath = @"SOFTWARE\Policies\Google\Chrome";
    private const string PolicyName = "ApplicationBoundEncryptionEnabled";

    /// <summary>检查当前 ABE 状态。</summary>
    public static AbeStatus Inspect(string? userDataDir = null)
    {
        var ud = string.IsNullOrWhiteSpace(userDataDir)
            ? BrowserProfileService.DefaultUserDataDir
            : userDataDir;

        var status = new AbeStatus { UserDataDir = ud };

        // --- 策略层 ---
        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(PolicyPath);
            using var hklm = Registry.LocalMachine.OpenSubKey(PolicyPath);

            var hkcuVal = hkcu?.GetValue(PolicyName) as int?;
            var hklmVal = hklm?.GetValue(PolicyName) as int?;

            status.PolicyApplied = hkcuVal != null || hklmVal != null;
            // 机器策略优先于用户策略
            var effective = hklmVal ?? hkcuVal;
            status.PolicyValueIsOff = effective == 0;
        }
        catch { }

        // --- 数据层：Local State 是否已有 app_bound_encrypted_key ---
        try
        {
            var ls = Path.Combine(ud, "Local State");
            if (File.Exists(ls))
            {
                var json = JsonNode.Parse(File.ReadAllText(ls));
                var oc = json?["os_crypt"];
                status.HasAppBoundKey = oc?["app_bound_encrypted_key"] != null;
            }
        }
        catch
        {
            status.Problem = "无法读取 Chrome 的 Local State。";
        }

        // --- 数据层：抽查 cookie 前缀 ---
        try
        {
            var db = ChromeCookieStore.FindCookiesDb(ud);
            if (db != null)
            {
                using var conn = SqliteDb.Open(db, readOnly: true);
                var rows = conn.Query(
                "SELECT COUNT(*) FROM cookies WHERE hex(substr(encrypted_value,1,3))='763230'");
                var v20 = rows.Count > 0 && rows[0].Length > 0
                    ? long.TryParse(rows[0][0]?.ToString(), out var n) ? n : 0
                    : 0;
                status.CookiePrefixV10Only = v20 == 0;
                if (v20 > 0) status.HasAppBoundKey = true;
            }
        }
        catch { }

        // --- 判定 ---
        if (status.HasAppBoundKey)
        {
            status.State = AbeState.Enabled;
        }
        else if (status.PolicyValueIsOff)
        {
            status.State = AbeState.DisabledByPolicy;
        }
        else if (status.Problem != null)
        {
            status.State = AbeState.Unknown;
        }
        else
        {
            status.State = AbeState.Disabled;
        }

        return status;
    }

    /// <summary>
    /// 写入策略，禁止 Chrome 启用 App-Bound 加密。
    /// 写入当前用户作用域（HKCU），不需要管理员权限。
    /// </summary>
    public static (bool ok, string message) DisableAbe()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyPath, writable: true);
            if (key == null)
                return (false, "无法创建策略注册表键。");

            key.SetValue(PolicyName, 0, RegistryValueKind.DWord);

            // 回读确认
            using var check = Registry.CurrentUser.OpenSubKey(PolicyPath);
            var v = check?.GetValue(PolicyName) as int?;
            if (v != 0)
                return (false, "策略写入后校验失败。");

            return (true,
                "已写入策略，Chrome 将不再启用 App-Bound 加密。\n" +
                "需要完全退出 Chrome 再重新打开才生效。");
        }
        catch (Exception ex)
        {
            return (false, $"写入策略失败：{ex.Message}");
        }
    }

    /// <summary>移除本工具写入的策略（恢复 Chrome 默认行为）。</summary>
    public static (bool ok, string message) RestoreDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PolicyPath, writable: true);
            if (key == null)
                return (true, "策略键不存在，已经是默认行为。");

            var existed = key.GetValue(PolicyName) != null;
            if (existed) key.DeleteValue(PolicyName, throwOnMissingValue: false);

            // 若键已空则一并删除，避免留下无意义的空键
            try
            {
                using var again = Registry.CurrentUser.OpenSubKey(PolicyPath, writable: true);
                if (again != null && again.SubKeyCount == 0 && again.ValueCount == 0)
                {
                    again.Close();
                    Registry.CurrentUser.DeleteSubKey(PolicyPath, throwOnMissingSubKey: false);
                }
            }
            catch { }

            return (true, existed
                ? "已移除策略，Chrome 将恢复默认行为（未来版本可能会自动启用 ABE）。"
                : "本来就没有写入过策略。");
        }
        catch (Exception ex)
        {
            return (false, $"移除策略失败：{ex.Message}");
        }
    }

    /// <summary>统计指定目录里 v20（App-Bound 加密）cookie 的数量。</summary>
    public static int CountV20Cookies(string? userDataDir = null)
    {
        var ud = string.IsNullOrWhiteSpace(userDataDir)
            ? BrowserProfileService.DefaultUserDataDir
            : userDataDir;
        try
        {
            var db = ChromeCookieStore.FindCookiesDb(ud);
            if (db == null) return 0;
            using var conn = SqliteDb.Open(db, readOnly: true);
            var rows = conn.Query(
                "SELECT COUNT(*) FROM cookies WHERE hex(substr(encrypted_value,1,3))='763230'");
            return rows.Count > 0 && rows[0].Length > 0
                ? int.TryParse(rows[0][0]?.ToString(), out var n) ? n : 0
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 从 v20 恢复可用状态。
    ///
    /// 原理（已核实）：v20 的密钥由 Chrome 的 elevation service 保管，
    /// 取密钥时会校验调用方必须是 Chrome 本体（二进制里有 WinVerifyTrust /
    /// QueryFullProcessImageName）。所以外部程序无法解密 v20，也无法"降级"成 v10。
    ///
    /// 唯一可行的恢复路径是：
    ///   1) 写入策略禁止 ABE（保证今后不再产生 v20）
    ///   2) 删除无法解密的 v20 cookie（它们本来也读不了了）
    ///   3) 重新登录一次，新产生的 cookie 就是可解密的 v10
    ///
    /// 代价：所有网站的登录状态会被清除，需要重新登录（GitHub 也在其中）。
    /// </summary>
    public static (bool ok, string message) RepairFromV20(bool deleteCookies)
    {
        if (IsChromeRunningSafe())
            return (false, "Chrome 正在运行，请先完全退出 Chrome 再操作。");

        var v20 = CountV20Cookies();
        if (v20 == 0)
            return (true, "没有发现 v20 加密的 cookie，无需修复。");

        // 1) 先关闸门
        var (policyOk, policyMsg) = DisableAbe();
        if (!policyOk)
            return (false, $"第一步（关闭 ABE 策略）失败：{policyMsg}");

        if (!deleteCookies)
            return (true,
                $"已写入策略阻止今后产生 v20（共发现 {v20} 条 v20 cookie）。" +
                "若要彻底恢复，需要同时清除这些无法解密的 cookie。");

        // 2) 删除无法解密的 v20 cookie
        try
        {
            var db = ChromeCookieStore.FindCookiesDb(BrowserProfileService.DefaultUserDataDir);
            if (db == null) return (false, "找不到 Cookies 数据库。");

            using var conn = SqliteDb.Open(db, readOnly: false);
            conn.Execute("DELETE FROM cookies WHERE hex(substr(encrypted_value,1,3))='763230'");

            var left = CountV20Cookies();
            return (true,
                $"已清除 {v20} 条 v20 cookie，并已写入策略阻止今后再次产生。" +
                $"当前剩余 v20：{left} 条。\n\n" +
                "请重新打开 Chrome 并登录 GitHub（其他网站也需要重新登录）。" +
                "重新登录后产生的就是可解密的 v10 cookie，本工具的切换功能即可正常使用。");
        }
        catch (Exception ex)
        {
            return (false, $"清除 v20 cookie 失败：{ex.Message}");
        }
    }

    private static bool IsChromeRunningSafe()
    {
        try { return System.Diagnostics.Process.GetProcessesByName("chrome").Length > 0; }
        catch { return false; }
    }
}
