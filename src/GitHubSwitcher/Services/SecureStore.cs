using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GitHubSwitcher.Services;

/// <summary>
/// 用 Windows DPAPI（当前用户作用域）加密保存 token。
/// 不使用 NuGet 包，直接 P/Invoke，保证离线可构建。
/// </summary>
public static class SecureStore
{
    private static readonly object Gate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>DPAPI UI_FORBIDDEN，避免解密时弹窗。</summary>
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static byte[]? Protect(byte[] plain)
    {
        var inBlob = new DATA_BLOB();
        var outBlob = new DATA_BLOB();
        var inPtr = Marshal.AllocHGlobal(plain.Length);
        try
        {
            Marshal.Copy(plain, 0, inPtr, plain.Length);
            inBlob.cbData = plain.Length;
            inBlob.pbData = inPtr;

            if (!CryptProtectData(ref inBlob, "GitHubSwitcher", IntPtr.Zero, IntPtr.Zero,
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

    private static byte[]? Unprotect(byte[] cipher)
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

    private static string SecretsFile => System.IO.Path.Combine(AppPaths.Root, "secrets.bin");

    private static Dictionary<string, string> LoadAll()
    {
        try
        {
            if (!System.IO.File.Exists(SecretsFile)) return new();
            var cipher = System.IO.File.ReadAllBytes(SecretsFile);
            var plain = Unprotect(cipher);
            if (plain == null) return new();
            var json = Encoding.UTF8.GetString(plain);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    private static void SaveAll(Dictionary<string, string> map)
    {
        AppPaths.EnsureRoot();
        var json = JsonSerializer.Serialize(map);
        var cipher = Protect(Encoding.UTF8.GetBytes(json));
        if (cipher == null) throw new InvalidOperationException("无法加密保存凭据（DPAPI 失败）。");
        System.IO.File.WriteAllBytes(SecretsFile, cipher);
    }

    public static void SetToken(string login, string token, DateTimeOffset? expiresAt = null)
    {
        lock (Gate)
        {
            var map = LoadAll();
            map[login.ToLowerInvariant()] = token;
            SaveAll(map);

            var meta = LoadMeta();
            if (expiresAt.HasValue) meta[login.ToLowerInvariant()] = expiresAt.Value;
            else meta.Remove(login.ToLowerInvariant());
            SaveMeta(meta);
        }
    }

    /// <summary>该账号已保存 token 的到期时间；null = 永不过期或未知。</summary>
    public static DateTimeOffset? GetExpiration(string login)
    {
        lock (Gate)
        {
            return LoadMeta().TryGetValue(login.ToLowerInvariant(), out var t) ? t : null;
        }
    }

    public static string? GetToken(string login)
    {
        lock (Gate)
        {
            var map = LoadAll();
            return map.TryGetValue(login.ToLowerInvariant(), out var t) ? t : null;
        }
    }

    public static bool HasToken(string login) => GetToken(login) is { Length: > 0 };

    public static void RemoveToken(string login)
    {
        lock (Gate)
        {
            var map = LoadAll();
            if (map.Remove(login.ToLowerInvariant())) SaveAll(map);
            var meta = LoadMeta();
            if (meta.Remove(login.ToLowerInvariant())) SaveMeta(meta);
        }
    }

    // ---- 到期时间元数据（同样 DPAPI 保护，存独立文件，兼容旧 secrets.bin）----

    private static string MetaFile => System.IO.Path.Combine(AppPaths.Root, "token_meta.bin");

    private static Dictionary<string, DateTimeOffset> LoadMeta()
    {
        try
        {
            if (!System.IO.File.Exists(MetaFile)) return new();
            var plain = Unprotect(System.IO.File.ReadAllBytes(MetaFile));
            if (plain == null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(
                Encoding.UTF8.GetString(plain)) ?? new();
        }
        catch { return new(); }
    }

    private static void SaveMeta(Dictionary<string, DateTimeOffset> map)
    {
        AppPaths.EnsureRoot();
        var cipher = Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(map)));
        if (cipher == null) return;
        System.IO.File.WriteAllBytes(MetaFile, cipher);
    }
}
