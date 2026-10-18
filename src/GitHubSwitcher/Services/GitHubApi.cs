using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubSwitcher.Services;

public sealed class DeviceCodeInfo
{
    public string DeviceCode { get; set; } = "";
    public string UserCode { get; set; } = "";
    public string VerificationUri { get; set; } = "";
    public int ExpiresIn { get; set; }
    public int Interval { get; set; } = 5;
}

public sealed class GitHubUser
{
    public string Login { get; set; } = "";
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
}

/// <summary>GitHub REST + OAuth 设备流访问。</summary>
public sealed class GitHubApi : IDisposable
{
    private readonly HttpClient _http;

    public GitHubApi()
    {
        var handler = new HttpClientHandler
        {
            // 本机常配本地代理；让 .NET 自动读取 IE/系统代理设置
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GitHubSwitcher/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------ 设备流

    /// <summary>申请设备码：用户拿 user_code 去浏览器授权。</summary>
    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(string clientId, string scope, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("scope", scope)
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        { Content = form };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"申请设备码失败（HTTP {(int)resp.StatusCode}）：{Trim(body)}");

        var node = JsonNode.Parse(body) ?? throw new InvalidOperationException("设备码响应为空。");
        if (node["error"] is not null)
            throw new InvalidOperationException($"无法申请设备码：{node["error_description"]?.GetValue<string>() ?? node["error"]!.ToString()}");

        return new DeviceCodeInfo
        {
            DeviceCode = node["device_code"]?.GetValue<string>() ?? "",
            UserCode = node["user_code"]?.GetValue<string>() ?? "",
            VerificationUri = node["verification_uri"]?.GetValue<string>() ?? "https://github.com/login/device",
            ExpiresIn = node["expires_in"]?.GetValue<int>() ?? 900,
            Interval = Math.Max(5, node["interval"]?.GetValue<int>() ?? 5)
        };
    }

    /// <summary>
    /// 轮询等待用户完成授权。成功返回 access_token；
    /// 用户取消或超时返回 null；其它错误抛异常。
    /// </summary>
    public async Task<string?> PollForTokenAsync(string clientId, DeviceCodeInfo info, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, info.ExpiresIn));
        var interval = TimeSpan.FromSeconds(info.Interval);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);

            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("device_code", info.DeviceCode),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            { Content = form };
            req.Headers.Accept.Clear();
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            JsonNode? node;
            try { node = JsonNode.Parse(body); }
            catch { continue; }

            if (node is null) continue;

            var token = node["access_token"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(token)) return token;

            var error = node["error"]?.GetValue<string>();
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "expired_token":
                    throw new InvalidOperationException("授权码已过期，请重新发起登录。");
                case "access_denied":
                    return null;          // 用户点了拒绝
                case null:
                    continue;
                default:
                    throw new InvalidOperationException(
                        $"授权失败：{node["error_description"]?.GetValue<string>() ?? error}");
            }
        }
        throw new TimeoutException("等待授权超时，请重新发起登录。");
    }

    // ------------------------------------------------------------ 用户信息

    public async Task<GitHubUser?> GetUserAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) return null;
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"获取用户信息失败（HTTP {(int)resp.StatusCode}）。");

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(body)!;
        return new GitHubUser
        {
            Login = node["login"]?.GetValue<string>() ?? "",
            Id = node["id"]?.GetValue<long>() ?? 0,
            Name = node["name"]?.GetValue<string>() ?? "",
            Email = node["email"]?.GetValue<string>()
        };
    }

    /// <summary>用 token 换取该 token 实际拥有的 OAuth scopes。</summary>
    public async Task<string[]> GetScopesAsync(string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("X-OAuth-Scopes", out var vals))
        {
            var raw = string.Join(",", vals);
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return Array.Empty<string>();
    }

    /// <summary>校验 token 是否有效。</summary>
    public async Task<bool> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try { return await GetUserAsync(token, ct).ConfigureAwait(false) is not null; }
        catch { return false; }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
