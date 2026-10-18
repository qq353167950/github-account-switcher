using System;
using System.Threading;
using System.Threading.Tasks;
using GitHubSwitcher.Models;

namespace GitHubSwitcher.Services;

/// <summary>写入 git 全局提交署名。</summary>
public static class GitConfig
{
    public static async Task<(bool ok, string message)> ApplyIdentityAsync(
        Account account, CancellationToken ct = default)
    {
        var name = string.IsNullOrWhiteSpace(account.Login) ? account.Label : account.Login;
        var email = string.IsNullOrWhiteSpace(account.Email)
            ? $"{account.GitHubId}+{account.Login}@users.noreply.github.com"
            : account.Email;

        var r1 = await ProcessRunner.RunAsync("git", $"config --global user.name \"{name}\"", null, null, ct)
            .ConfigureAwait(false);
        if (!r1.Ok) return (false, $"写入 user.name 失败：{r1.Combined}");

        var r2 = await ProcessRunner.RunAsync("git", $"config --global user.email \"{email}\"", null, null, ct)
            .ConfigureAwait(false);
        if (!r2.Ok) return (false, $"写入 user.email 失败：{r2.Combined}");

        // 校验
        var c1 = await ProcessRunner.RunAsync("git", "config --global --get user.name", null, null, ct)
            .ConfigureAwait(false);
        var c2 = await ProcessRunner.RunAsync("git", "config --global --get user.email", null, null, ct)
            .ConfigureAwait(false);

        if (c1.Output.Trim() != name || c2.Output.Trim() != email)
            return (false, $"校验不一致：{c1.Output.Trim()} / {c2.Output.Trim()}");

        return (true, $"提交署名已设为 {name} <{email}>");
    }

    public static async Task<(string name, string email)> ReadIdentityAsync(CancellationToken ct = default)
    {
        var n = await ProcessRunner.RunAsync("git", "config --global --get user.name", null, null, ct)
            .ConfigureAwait(false);
        var e = await ProcessRunner.RunAsync("git", "config --global --get user.email", null, null, ct)
            .ConfigureAwait(false);
        return (n.Output.Trim(), e.Output.Trim());
    }
}
