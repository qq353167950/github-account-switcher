using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitHubSwitcher.Services;

/// <summary>一个 GitHub 相关 cookie 的完整快照。</summary>
public sealed class ChromeCookie
{
    public string HostKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "/";
    public byte[] EncryptedValue { get; set; } = Array.Empty<byte>();
    public long ExpiresUtc { get; set; }
    public long CreationUtc { get; set; }
    public long LastAccessUtc { get; set; }
    public long LastUpdateUtc { get; set; }
    public int IsSecure { get; set; }
    public int IsHttpOnly { get; set; }
    public int HasExpires { get; set; }
    public int IsPersistent { get; set; }
    public int Priority { get; set; }
    public int SameSite { get; set; }
    public int SourceScheme { get; set; }
    public int SourcePort { get; set; }
    public int SourceType { get; set; }
    public int HasCrossSiteAncestor { get; set; }
    public string TopFrameSiteKey { get; set; } = "";
}

/// <summary>
/// 直接读写 Chrome 的 Cookies 数据库。
///
/// 实测确认（本机 Chrome 149）：
///  - Local State 里 os_crypt.encrypted_key 是 DPAPI 保护的 32 字节主密钥
///  - cookie 的 encrypted_value 格式为 "v10" + 12字节nonce + 密文 + 16字节tag（AES-256-GCM）
///  - 明文 = SHA256(host_key) (32字节) + 实际 cookie 值
///  - 迁移到不同主密钥时，必须解出明文再用目标密钥重新加密
/// </summary>
public static class ChromeCookieStore
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Chrome 的 WebKit 时间：1601-01-01 起的微秒数。</summary>
    private const long WindowsEpochOffsetSeconds = 11644473600L;

    public static long NowUtc => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10
                                 + WindowsEpochOffsetSeconds * 1_000_000L;

    // ---------------------------------------------------------------- 主密钥

    /// <summary>从一个 Chrome 用户数据目录读取并解出 master key。失败返回 null。</summary>
    public static byte[]? TryGetMasterKey(string userDataDir)
    {
        try
        {
            var lsPath = Path.Combine(userDataDir, "Local State");
            if (!File.Exists(lsPath)) return null;

            var json = JsonNode.Parse(File.ReadAllText(lsPath));
            var b64 = json?["os_crypt"]?["encrypted_key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(b64)) return null;

            var raw = Convert.FromBase64String(b64);
            if (raw.Length < 6 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI") return null;

            return DpapiUnprotect(raw[5..]);
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? DpapiUnprotect(byte[] cipher)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var inPtr = Marshal.AllocHGlobal(cipher.Length);
        try
        {
            Marshal.Copy(cipher, 0, inPtr, cipher.Length);
            inBlob.cbData = cipher.Length;
            inBlob.pbData = inPtr;

            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                return null;

            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        catch { return null; }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }

    // ---------------------------------------------------------------- 加解密

    public static byte[] Decrypt(byte[] masterKey, byte[] encryptedValue)
    {
        if (encryptedValue.Length < 3) throw new CryptographicException("密文太短。");
        var prefix = Encoding.ASCII.GetString(encryptedValue, 0, 3);
        if (prefix != "v10" && prefix != "v11")
            throw new CryptographicException($"不支持的加密前缀：{prefix}（可能启用了 App-Bound 加密）");

        if (encryptedValue.Length < 3 + 12 + 16)
            throw new CryptographicException("密文格式异常。");

        var nonce = encryptedValue[3..15];
        var payload = encryptedValue[15..];
        var tag = payload[^16..];
        var cipher = payload[..^16];

        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(masterKey[..32], 16);
        gcm.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    public static byte[] Encrypt(byte[] masterKey, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(masterKey[..32], 16);
        gcm.Encrypt(nonce, plain, cipher, tag);

        var outBytes = new byte[3 + 12 + cipher.Length + 16];
        Encoding.ASCII.GetBytes("v10", 0, 3, outBytes, 0);
        Buffer.BlockCopy(nonce, 0, outBytes, 3, 12);
        Buffer.BlockCopy(cipher, 0, outBytes, 15, cipher.Length);
        Buffer.BlockCopy(tag, 0, outBytes, 15 + cipher.Length, 16);
        return outBytes;
    }

    /// <summary>明文结构：SHA256(host_key) + 值。</summary>
    public static byte[] BuildPlain(string hostKey, byte[] value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(hostKey)).Concat(value).ToArray();

    public static byte[] ExtractValue(byte[] plain, string hostKey)
    {
        var prefix = SHA256.HashData(Encoding.UTF8.GetBytes(hostKey));
        if (plain.Length < 32) return plain;
        return plain.AsSpan(32).ToArray();
    }

    // ---------------------------------------------------------------- 路径解析

    /// <summary>找到 Chrome 的 Cookies 数据库文件。</summary>
    public static string? FindCookiesDb(string userDataDir, string profileDir = "Default")
    {
        var a = Path.Combine(userDataDir, profileDir, "Network", "Cookies");
        if (File.Exists(a)) return a;
        var b = Path.Combine(userDataDir, profileDir, "Cookies");
        if (File.Exists(b)) return b;
        return null;
    }

    // ---------------------------------------------------------------- 读

    /// <summary>读取与 GitHub 相关的所有 cookie（原始密文，不解密）。</summary>
    public static List<ChromeCookie> ReadGitHubCookies(string cookiesDbPath)
    {
        var list = new List<ChromeCookie>();
        using var db = SqliteDb.Open(cookiesDbPath, readOnly: true);

        var rows = db.Query(
            "SELECT host_key,name,encrypted_value,path,expires_utc,is_secure,is_httponly," +
            "last_access_utc,has_expires,is_persistent,priority,samesite,source_scheme,source_port," +
            "creation_utc,top_frame_site_key,last_update_utc,source_type,has_cross_site_ancestor " +
            "FROM cookies WHERE host_key LIKE '%github%'");

        foreach (var r in rows)
        {
            list.Add(new ChromeCookie
            {
                HostKey = r[0] as string ?? "",
                Name = r[1] as string ?? "",
                EncryptedValue = r[2] as byte[] ?? Array.Empty<byte>(),
                Path = r[3] as string ?? "/",
                ExpiresUtc = ToLong(r[4]),
                IsSecure = (int)ToLong(r[5]),
                IsHttpOnly = (int)ToLong(r[6]),
                LastAccessUtc = ToLong(r[7]),
                HasExpires = (int)ToLong(r[8]),
                IsPersistent = (int)ToLong(r[9]),
                Priority = (int)ToLong(r[10]),
                SameSite = (int)ToLong(r[11]),
                SourceScheme = (int)ToLong(r[12]),
                SourcePort = (int)ToLong(r[13]),
                CreationUtc = ToLong(r[14]),
                TopFrameSiteKey = r[15] as string ?? "",
                LastUpdateUtc = ToLong(r[16]),
                SourceType = (int)ToLong(r[17]),
                HasCrossSiteAncestor = (int)ToLong(r[18])
            });
        }
        return list;
    }

    private static long ToLong(object? o) =>
        o == null ? 0 : long.TryParse(o.ToString(), out var v) ? v : 0;

    // ---------------------------------------------------------------- 写

    /// <summary>
    /// 用给定的 cookie 集合替换目标库中的全部 GitHub cookie。
    /// cookies 里的密文将用 targetKey 重新加密，因此可以跨 Chrome 目录迁移。
    /// </summary>
    public static void ReplaceGitHubCookies(
        string targetCookiesDbPath, byte[] sourceKey, byte[] targetKey,
        IEnumerable<ChromeCookie> cookies)
    {
        using var db = SqliteDb.Open(targetCookiesDbPath, readOnly: false);
        db.Execute("BEGIN IMMEDIATE");
        try
        {
            db.Execute("DELETE FROM cookies WHERE host_key LIKE '%github%'");

            var now = NowUtc;
            foreach (var c in cookies)
            {
                // 若源密钥可用且密文可解，则解出明文后用目标密钥重加密；
                // 否则（同一密钥场景）直接沿用原文。
                byte[] newEnc;
                if (sourceKey.Length > 0 && !sourceKey.SequenceEqual(targetKey))
                {
                    var plain = Decrypt(sourceKey, c.EncryptedValue);
                    newEnc = Encrypt(targetKey, plain);
                }
                else
                {
                    newEnc = c.EncryptedValue;
                }

                db.Execute(
                    "INSERT OR REPLACE INTO cookies(host_key,name,encrypted_value,value,path,expires_utc,is_secure," +
                    "is_httponly,last_access_utc,has_expires,is_persistent,priority,samesite,source_scheme," +
                    "source_port,creation_utc,top_frame_site_key,last_update_utc,source_type,has_cross_site_ancestor) " +
                    "VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                    c.HostKey, c.Name, newEnc, "", c.Path,
                    c.ExpiresUtc, c.IsSecure, c.IsHttpOnly, c.LastAccessUtc,
                    c.HasExpires, c.IsPersistent, c.Priority, c.SameSite,
                    c.SourceScheme, c.SourcePort,
                    c.CreationUtc == 0 ? now : c.CreationUtc,
                    c.TopFrameSiteKey,
                    c.LastUpdateUtc == 0 ? now : c.LastUpdateUtc,
                    c.SourceType, c.HasCrossSiteAncestor);
            }

            db.Execute("COMMIT");
        }
        catch
        {
            try { db.Execute("ROLLBACK"); } catch { }
            throw;
        }
    }

    /// <summary>确认某组 cookie 的主密钥能否解开全部条目。</summary>
    public static (int ok, int total) VerifyDecryptable(byte[] key, IEnumerable<ChromeCookie> cookies)
    {
        int ok = 0, total = 0;
        foreach (var c in cookies)
        {
            total++;
            try
            {
                var plain = Decrypt(key, c.EncryptedValue);
                if (plain.Length >= 32) ok++;
            }
            catch { }
        }
        return (ok, total);
    }
}
