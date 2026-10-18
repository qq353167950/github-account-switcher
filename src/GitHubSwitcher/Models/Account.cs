using System;
using System.Collections.Generic;
using System.Linq;

namespace GitHubSwitcher.Models;

/// <summary>浏览器切换方式。</summary>
public static class BrowserModes
{
    /// <summary>把登录态注入到你日常使用的 Chrome（就是桌面图标打开的那个）。</summary>
    public const string Inject = "inject";

    /// <summary>每个账号一个独立的 Chrome 窗口（互不干扰，但书签是另一套）。</summary>
    public const string Isolated = "isolated";
}

/// <summary>一个已保存的 GitHub 账号档案。</summary>
public sealed class Account
{
    /// <summary>GitHub 登录名，作为唯一标识。</summary>
    public string Login { get; set; } = "";

    /// <summary>界面上显示的名称，默认等于 Login。</summary>
    public string Label { get; set; } = "";

    /// <summary>提交署名用的邮箱。</summary>
    public string Email { get; set; } = "";

    /// <summary>该账号专属的 Chrome 用户数据目录（绝对路径）。</summary>
    public string ChromeUserDataDir { get; set; } = "";

    /// <summary>GitHub 数字 id，用于生成 noreply 邮箱。</summary>
    public long GitHubId { get; set; }

    /// <summary>浏览器切换方式，见 <see cref="BrowserModes"/>。</summary>
    public string BrowserMode { get; set; } = BrowserModes.Inject;

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public string DisplayName =>
        string.IsNullOrWhiteSpace(Label) || Label == Login ? Login : $"{Label} ({Login})";
}

/// <summary>accounts.json 的根对象。</summary>
public sealed class AccountStoreData
{
    public List<Account> Accounts { get; set; } = new();

    /// <summary>最近一次切换到的账号 login。</summary>
    public string? ActiveLogin { get; set; }

    /// <summary>设备流登录用的 OAuth App client_id（可在设置里改）。</summary>
    public string OAuthClientId { get; set; } = DefaultClientId;

    /// <summary>全局默认的浏览器切换方式。</summary>
    public string BrowserMode { get; set; } = BrowserModes.Inject;

    /// <summary>
    /// GitHub CLI 公开的 OAuth App client id。
    /// 说明：这是 GitHub CLI 官方客户端的公开标识，通用做法是复用它做设备流；
    /// 如需完全私有，可在设置里填入自己注册的 OAuth App client id。
    /// </summary>
    public const string DefaultClientId = "178c6fc778ccc68e1d6a";

    public Account? Find(string? login) =>
        string.IsNullOrWhiteSpace(login)
            ? null
            : Accounts.FirstOrDefault(a => string.Equals(a.Login, login, StringComparison.OrdinalIgnoreCase));
}
