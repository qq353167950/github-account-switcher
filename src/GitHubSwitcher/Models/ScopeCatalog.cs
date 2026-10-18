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
        new() { Name = "write:org",      Description = "修改组织成员与团队（被 admin:org 包含）" },
        new() { Name = "gist",           Description = "读写 Gist 代码片段", IsDefault = true },
        new() { Name = "user",           Description = "读取与修改个人资料" },
        new() { Name = "notifications",  Description = "读取通知" },
        new() { Name = "read:packages",  Description = "读取 GitHub Packages" },
        new() { Name = "write:packages", Description = "发布包到 GitHub Packages" },
        new() { Name = "delete:packages",Description = "删除 GitHub Packages（危险）", IsDangerous = true },
        new() { Name = "read:repo_hook", Description = "读取仓库 Webhook（被 admin:repo_hook 包含）" },
        new() { Name = "write:repo_hook",Description = "修改仓库 Webhook（被 admin:repo_hook 包含）" },
        new() { Name = "admin:repo_hook",Description = "完全管理仓库 Webhook（危险）", IsDangerous = true },
        new() { Name = "admin:org",      Description = "完全管理组织（危险，包含 read/write:org）", IsDangerous = true },
        new() { Name = "admin:org_hook", Description = "管理组织 Webhook（危险）", IsDangerous = true },
        new() { Name = "admin:public_key",Description = "完全管理 SSH 公钥（包含 read/write:public_key）", IsDangerous = true },
        new() { Name = "read:public_key",Description = "读取 SSH 公钥（被 admin:public_key 包含）" },
        new() { Name = "write:public_key",Description = "增删 SSH 公钥（被 admin:public_key 包含）" },
        new() { Name = "read:gpg_key",   Description = "读取 GPG 密钥（被 admin:gpg_key 包含）" },
        new() { Name = "write:gpg_key",  Description = "增删 GPG 密钥（被 admin:gpg_key 包含）" },
        new() { Name = "admin:gpg_key",  Description = "完全管理 GPG 密钥（危险）", IsDangerous = true },
        new() { Name = "read:ssh_signing_key",Description = "读取 SSH 签名密钥（被 admin:ssh_signing_key 包含）" },
        new() { Name = "admin:ssh_signing_key",Description = "完全管理 SSH 签名密钥（危险）", IsDangerous = true },
        new() { Name = "admin:enterprise",Description = "完全管理企业账号（危险）", IsDangerous = true },
        new() { Name = "audit_log",      Description = "读取组织审计日志" },
        new() { Name = "project",        Description = "读写 Projects" },
        new() { Name = "codespace",      Description = "完全管理 Codespaces" },
        new() { Name = "copilot",        Description = "管理 Copilot 访问" },
        new() { Name = "write:discussion",Description = "管理讨论" },
        new() { Name = "write:network_configurations", Description = "修改组织网络配置" },
        new() { Name = "delete_repo",    Description = "删除仓库", IsDangerous = true },
    };

    public static string[] DefaultScopes =>
        All.Where(s => s.IsDefault).Select(s => s.Name).ToArray();

    public static ScopeOption? Find(string name) =>
        All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// GitHub OAuth scope 的蕴含关系：勾选了 key，就自动拥有 value 的能力。
    /// 依据 GitHub 官方文档（admin:* 蕴含同前缀的 read:/write:*），
    /// 并已用真实 token 实测验证。
    /// </summary>
    public static IReadOnlyDictionary<string, string[]> Implies { get; } =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["admin:org"]              = new[] { "read:org", "write:org" },
            ["admin:repo_hook"]        = new[] { "read:repo_hook", "write:repo_hook" },
            ["admin:public_key"]       = new[] { "read:public_key", "write:public_key" },
            ["admin:gpg_key"]          = new[] { "read:gpg_key", "write:gpg_key" },
            ["admin:ssh_signing_key"]  = new[] { "read:ssh_signing_key" },
            ["repo"]                   = new[] { "repo:status", "repo_deployment",
                                                 "public_repo", "repo:invite" },
        };
}
