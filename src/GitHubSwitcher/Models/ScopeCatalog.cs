using System;
using System.Collections.Generic;
using System.Linq;

namespace GitHubSwitcher.Models;

/// <summary>一个可勾选的 OAuth scope。</summary>
public sealed class ScopeOption
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsDangerous { get; init; }
}

/// <summary>GitHub 常用 OAuth scopes 目录。</summary>
public static class ScopeCatalog
{
    public static IReadOnlyList<ScopeOption> All { get; } = new List<ScopeOption>
    {
        new() { Name = "repo",           Description = "读写公有/私有仓库代码（推送、拉取必需）", IsDefault = true },
        new() { Name = "workflow",       Description = "修改 GitHub Actions 工作流文件", IsDefault = true },
        new() { Name = "read:org",       Description = "读取组织成员与团队信息", IsDefault = true },
        new() { Name = "gist",           Description = "读写 Gist 代码片段", IsDefault = true },
        new() { Name = "user",           Description = "读取与修改个人资料" },
        new() { Name = "notifications",  Description = "读取通知" },
        new() { Name = "write:packages", Description = "发布包到 GitHub Packages" },
        new() { Name = "delete_repo",    Description = "删除仓库（危险）", IsDangerous = true },
        new() { Name = "admin:org",      Description = "完全管理组织（危险）", IsDangerous = true },
        new() { Name = "admin:repo_hook",Description = "管理仓库 Webhook" },
        new() { Name = "admin:public_key", Description = "管理 SSH 公钥" },
        new() { Name = "project",        Description = "读写 Projects" },
    };

    public static string[] DefaultScopes =>
        All.Where(s => s.IsDefault).Select(s => s.Name).ToArray();

    public static ScopeOption? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
