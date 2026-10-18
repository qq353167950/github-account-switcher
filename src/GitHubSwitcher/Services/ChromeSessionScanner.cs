using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubSwitcher.Services;

/// <summary>浏览器里检测到的一个已登录 GitHub 账号。</summary>
public sealed class DetectedSession
{
    /// <summary>GitHub 数字 ID。</summary>
    public string UserId { get; set; } = "";

    /// <summary>登录名（探测成功时才有）。</summary>
    public string Login { get; set; } = "";

    /// <summary>会话令牌。</summary>
    public string Token { get; set; } = "";

    /// <summary>是否为当前正在使用的账号。</summary>
    public bool IsCurrent { get; set; }

    /// <summary>令牌校验是否成功。</summary>
    public bool Verified { get; set; }

    /// <summary>校验失败原因。</summary>
    public string? Error { get; set; }
}

/// <summary>
/// 从 Chrome 中扫描「已经登录过的 GitHub 账号」。
///
/// 原理（已实测确认）：
/// GitHub 在浏览器里用 github.com 的 saved_user_sessions cookie 保存多账号会话，
/// 格式为 URL 编码的 "id1:token1|id2:token2|..."。
/// 而 user_session cookie 保存的是当前正在使用的那个。
///
/// 因此只要读这个 cookie，就能枚举出浏览器记住的所有账号；
/// 再用各自的 token 访问一次 /dashboard，从页面里的 currentUserLogin
/// 就能拿到对应的登录名 —— 这样用户不必对已登录的账号重复走设备授权。
///
/// 注意：这里只读取本地 cookie 并做一次自己的鉴权请求，不修改任何数据。
/// </summary>
public static class ChromeSessionScanner
{
    private const string SessionCookieName = "saved_user_sessions";
    private const string CurrentSessionCookieName = "user_session";
    private const string UserCookieName = "dotcom_user";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials,
            AllowAutoRedirect = true
        };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        return c;
    }

    /// <summary>
    /// 扫描浏览器中的已登录账号。
    /// 返回空列表表示没找到（可能没登录、或加密方式不支持）。
    /// </summary>
    public static async Task<(List<DetectedSession> sessions, string? error)> ScanAsync(
        CancellationToken ct = default)
    {
        var result = new List<DetectedSession>();

        try
        {
            var ud = BrowserProfileService.DefaultUserDataDir;

            var cap = AbeService.Inspect(ud);
            if (cap.State == AbeState.Enabled)
                return (result, "Chrome 已启用 App-Bound 加密，无法读取浏览器登录态。");

            var dbPath = ChromeCookieStore.FindCookiesDb(ud);
            var key = ChromeCookieStore.TryGetMasterKey(ud);
            if (dbPath == null || key == null)
                return (result, "无法访问 Chrome 的 cookie 数据。");

            var cookies = ChromeCookieStore.ReadGitHubCookies(dbPath);

            // 解出需要的几个 cookie
            var currentToken = DecodeCookieValue(key, cookies, CurrentSessionCookieName);
            var currentUser = DecodeCookieValue(key, cookies, UserCookieName);
            var savedRaw = DecodeCookieValue(key, cookies, SessionCookieName);

            var sessions = new List<DetectedSession>();

            if (!string.IsNullOrWhiteSpace(savedRaw))
            {
                var decoded = Uri.UnescapeDataString(savedRaw);
                foreach (var seg in decoded.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var idx = seg.IndexOf(':');
                    if (idx <= 0 || idx >= seg.Length - 1) continue;

                    var uid = seg[..idx].Trim();
                    var tok = seg[(idx + 1)..].Trim();
                    if (uid.Length == 0 || tok.Length == 0) continue;

                    sessions.Add(new DetectedSession
                    {
                        UserId = uid,
                        Token = tok,
                        IsCurrent = !string.IsNullOrEmpty(currentToken) && tok == currentToken
                    });
                }
            }

            // 兜底：若 saved_user_sessions 不存在，但当前有会话，就只报当前这一个
            if (sessions.Count == 0 && !string.IsNullOrWhiteSpace(currentToken))
            {
                sessions.Add(new DetectedSession
                {
                    UserId = "",
                    Token = currentToken,
                    IsCurrent = true,
                    Login = currentUser ?? ""
                });
            }

            // 逐个探测登录名（当前账号直接用 dotcom_user，省一次请求）
            foreach (var s in sessions)
            {
                ct.ThrowIfCancellationRequested();

                if (s.IsCurrent && !string.IsNullOrWhiteSpace(currentUser))
                {
                    s.Login = currentUser!;
                    s.Verified = true;
                    continue;
                }

                var (login, err) = await ProbeLoginAsync(s.Token, ct).ConfigureAwait(false);
                s.Login = login ?? "";
                s.Verified = !string.IsNullOrWhiteSpace(login);
                s.Error = err;
            }

            return (sessions, null);
        }
        catch (OperationCanceledException)
        {
            return (result, "已取消。");
        }
        catch (Exception ex)
        {
            return (result, $"扫描失败：{ex.Message}");
        }
    }

    private static string? DecodeCookieValue(
        byte[] key, IEnumerable<ChromeCookie> cookies, string name)
    {
        var c = cookies.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (c == null) return null;

        try
        {
            var plain = ChromeCookieStore.Decrypt(key, c.EncryptedValue);
            var val = ChromeCookieStore.ExtractValue(plain, c.HostKey);
            return Encoding.UTF8.GetString(val);
        }
        catch { return null; }
    }

    /// <summary>
    /// 用会话令牌访问 /dashboard，从返回 HTML 里提取 currentUserLogin。
    /// </summary>
    private static async Task<(string? login, string? error)> ProbeLoginAsync(
        string token, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://github.com/dashboard");
            req.Headers.TryAddWithoutValidation("Cookie", $"user_session={token}");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"HTTP {(int)resp.StatusCode}");

            var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // 优先取明确的当前用户字段，避免误匹配页面里别人的 login
            foreach (var pattern in new[]
            {
                "\"currentUserLogin\":\"([A-Za-z0-9\\-]+)\"",
                "data-login=\"([A-Za-z0-9\\-]+)\"",
                "\"currentUser\":\"([A-Za-z0-9\\-]+)\""
            })
            {
                var m = Regex.Match(html, pattern);
                if (m.Success) return (m.Groups[1].Value, null);
            }

            // 若被重定向到登录页，说明令牌已失效
            if (html.Contains("Sign in to GitHub", StringComparison.OrdinalIgnoreCase) &&
                !html.Contains("currentUserLogin", StringComparison.OrdinalIgnoreCase))
                return (null, "会话已失效");

            return (null, "未能在页面中识别账号名");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
