using System;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubSwitcher.Services;

/// <summary>
/// 用 gh CLI 把 token 注册进凭据存储，使 git push（https）自动使用当前账号。
///
/// 为什么这样做：本机 git 的 credential.helper 已指向 `gh auth git-credential`，
/// 所以只要 gh 认识这个账号，git 就能用它推送，无需重复输入密码。
/// </summary>
public static class GhCli
{
    public static bool IsAvailable => ProcessRunner.Exists("gh");

    /// <summary>把 token 写入 gh 的账号库（多账号并存）。</summary>
    public static async Task<(bool ok, string message)> RegisterTokenAsync(
        string token, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, "未检测到 gh CLI。git 推送仍可用，但需要手动配置凭据（见说明）。");

        // --with-token 从 stdin 读 token
        var r = await ProcessRunner.RunAsync(
            "gh", "auth login --hostname github.com --with-token", token, null, ct)
            .ConfigureAwait(false);

        if (!r.Ok)
            return (false, $"gh auth login 失败：{r.Combined}");

        return (true, "已注册到 gh 凭据库");
    }

    /// <summary>切换 gh 的活跃账号（影响 git push / gh 命令）。</summary>
    public static async Task<(bool ok, string message)> SwitchAsync(
        string login, CancellationToken ct = default)
    {
        if (!IsAvailable) return (false, "未检测到 gh CLI");

        var r = await ProcessRunner.RunAsync(
            "gh", $"auth switch --hostname github.com --user \"{login}\"", null, null, ct)
            .ConfigureAwait(false);

        return r.Ok
            ? (true, "命令行认证已切换")
            : (false, $"gh auth switch 失败：{r.Combined}");
    }

    /// <summary>查询 gh 里已登录的账号列表。</summary>
    public static async Task<string[]> ListAccountsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return Array.Empty<string>();
        var r = await ProcessRunner.RunAsync("gh", "auth status", null, null, ct).ConfigureAwait(false);
        var text = r.Combined;
        var list = new System.Collections.Generic.List<string>();
        foreach (var line in text.Split('\n'))
        {
            // 形如： "  ✓ Logged in to github.com account xxx (keyring)"
            var i = line.IndexOf("account ", StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var rest = line[(i + "account ".Length)..].Trim();
            var end = rest.IndexOf(' ');
            var name = end > 0 ? rest[..end] : rest;
            name = name.Trim().TrimEnd(',');
            if (!string.IsNullOrWhiteSpace(name) && !list.Contains(name)) list.Add(name);
        }
        return list.ToArray();
    }

    public static async Task<string?> GetActiveLoginAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        var r = await ProcessRunner.RunAsync("gh", "api user --jq .login", null, null, ct)
            .ConfigureAwait(false);
        return r.Ok ? r.Output.Trim() : null;
    }
}
