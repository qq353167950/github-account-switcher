using System;
using System.Collections.Generic;
using System.Linq;

namespace GitHubSwitcher.Models;

/// <summary>一个可勾选的 OAuth scope。</summary>
public sealed class ScopeOption
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Category { get; init; } = "";
    public bool IsDefault { get; init; }
    public bool IsDangerous { get; init; }
}

/// <summary>GitHub 常用 OAuth scopes 目录（按分类分组显示）。</summary>
public static class ScopeCatalog
{
    public const string CatRepo = "仓库";
    public const string CatOrg = "组织";
    public const string CatHook = "Webhook";
    public const string CatPackages = "Packages";
    public const string CatKeys = "密钥";
    public const string CatAccount = "账号";
    public const string CatMisc = "其他";

    public static IReadOnlyList<ScopeOption> All { get; } = new List<ScopeOption>
    {
        // 仓库
        new() { Name = "repo",        Category = CatRepo, Description = "读写公有/私有仓库代码（推送、拉取必需）", IsDefault = true },
        new() { Name = "workflow",    Category = CatRepo, Description = "修改 GitHub Actions 工作流文件", IsDefault = true },
        new() { Name = "delete_repo", Category = CatRepo, Description = "删除仓库", IsDangerous = true },
        // 组织
        new() { Name = "read:org",       Category = CatOrg, Description = "读取组织成员与团队信息", IsDefault = true },
        new() { Name = "write:org",      Category = CatOrg, Description = "修改组织成员与团队（被 admin:org 包含）" },
        new() { Name = "admin:org",      Category = CatOrg, Description = "完全管理组织（危险，包含 read/write:org）", IsDangerous = true },
        new() { Name = "admin:org_hook", Category = CatOrg, Description = "管理组织 Webhook（危险）", IsDangerous = true },
        new() { Name = "audit_log",      Category = CatOrg, Description = "读取组织审计日志" },
        new() { Name = "project",        Category = CatOrg, Description = "读写 Projects" },
        new() { Name = "write:network_configurations", Category = CatOrg, Description = "修改组织网络配置" },
        // Webhook
        new() { Name = "read:repo_hook",  Category = CatHook, Description = "读取仓库 Webhook（被 admin:repo_hook 包含）" },
        new() { Name = "write:repo_hook", Category = CatHook, Description = "修改仓库 Webhook（被 admin:repo_hook 包含）" },
        new() { Name = "admin:repo_hook", Category = CatHook, Description = "完全管理仓库 Webhook（危险）", IsDangerous = true },
        // Packages
        new() { Name = "read:packages",   Category = CatPackages, Description = "读取 GitHub Packages" },
        new() { Name = "write:packages",  Category = CatPackages, Description = "发布包到 GitHub Packages" },
        new() { Name = "delete:packages", Category = CatPackages, Description = "删除 GitHub Packages（危险）", IsDangerous = true },
        // 密钥
        new() { Name = "read:public_key",        Category = CatKeys, Description = "读取 SSH 公钥（被 admin:public_key 包含）" },
        new() { Name = "write:public_key",       Category = CatKeys, Description = "增删 SSH 公钥（被 admin:public_key 包含）" },
        new() { Name = "admin:public_key",       Category = CatKeys, Description = "完全管理 SSH 公钥（危险）", IsDangerous = true },
        new() { Name = "read:gpg_key",           Category = CatKeys, Description = "读取 GPG 密钥（被 admin:gpg_key 包含）" },
        new() { Name = "write:gpg_key",          Category = CatKeys, Description = "增删 GPG 密钥（被 admin:gpg_key 包含）" },
        new() { Name = "admin:gpg_key",          Category = CatKeys, Description = "完全管理 GPG 密钥（危险）", IsDangerous = true },
        new() { Name = "read:ssh_signing_key",   Category = CatKeys, Description = "读取 SSH 签名密钥（被 admin:ssh_signing_key 包含）" },
        new() { Name = "admin:ssh_signing_key",  Category = CatKeys, Description = "完全管理 SSH 签名密钥（危险）", IsDangerous = true },
        // 账号
        new() { Name = "user",          Category = CatAccount, Description = "读取与修改个人资料" },
        new() { Name = "notifications", Category = CatAccount, Description = "读取通知" },
        new() { Name = "gist",          Category = CatAccount, Description = "读写 Gist 代码片段", IsDefault = true },
        new() { Name = "copilot",       Category = CatAccount, Description = "管理 Copilot 访问" },
        // 其他
        new() { Name = "codespace",        Category = CatMisc, Description = "完全管理 Codespaces" },
        new() { Name = "write:discussion", Category = CatMisc, Description = "管理讨论" },
        new() { Name = "admin:enterprise", Category = CatMisc, Description = "完全管理企业账号（危险）", IsDangerous = true },
    };

    /// <summary>分类显示顺序。</summary>
    public static readonly string[] CategoryOrder =
        { CatRepo, CatOrg, CatHook, CatPackages, CatKeys, CatAccount, CatMisc };

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
