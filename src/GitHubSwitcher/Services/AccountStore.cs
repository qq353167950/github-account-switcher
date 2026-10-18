using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GitHubSwitcher.Models;

namespace GitHubSwitcher.Services;

/// <summary>accounts.json 的读写。</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public AccountStoreData Data { get; private set; } = new();

    public IReadOnlyList<Account> Accounts => Data.Accounts;

    public Account? Active => Data.Find(Data.ActiveLogin);

    public void Load()
    {
        AppPaths.EnsureRoot();
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
            {
                var json = File.ReadAllText(AppPaths.AccountsFile);
                Data = JsonSerializer.Deserialize<AccountStoreData>(json, Options) ?? new();
            }
        }
        catch
        {
            Data = new();
        }
        // 兜底：老数据可能缺字段
        if (string.IsNullOrWhiteSpace(Data.OAuthClientId))
            Data.OAuthClientId = AccountStoreData.DefaultClientId;
        foreach (var a in Data.Accounts)
        {
            if (string.IsNullOrWhiteSpace(a.Label)) a.Label = a.Login;
            if (string.IsNullOrWhiteSpace(a.Email))
                a.Email = $"{a.GitHubId}+{a.Login}@users.noreply.github.com";
            if (string.IsNullOrWhiteSpace(a.ChromeUserDataDir))
                a.ChromeUserDataDir = AppPaths.ChromeDirFor(a.Login);
        }
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        var json = JsonSerializer.Serialize(Data, Options);
        var tmp = AppPaths.AccountsFile + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, AppPaths.AccountsFile, true);
    }

    public Account AddOrUpdate(Account account)
    {
        var existing = Data.Find(account.Login);
        if (existing != null)
        {
            existing.Label = account.Label;
            existing.Email = account.Email;
            existing.GitHubId = account.GitHubId;
            if (!string.IsNullOrWhiteSpace(account.ChromeUserDataDir))
                existing.ChromeUserDataDir = account.ChromeUserDataDir;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(account.Label)) account.Label = account.Login;
            if (string.IsNullOrWhiteSpace(account.ChromeUserDataDir))
                account.ChromeUserDataDir = AppPaths.ChromeDirFor(account.Login);
            Data.Accounts.Add(account);
            existing = account;
        }
        Save();
        return existing;
    }

    public void Remove(string login)
    {
        var a = Data.Find(login);
        if (a != null)
        {
            Data.Accounts.Remove(a);
            if (string.Equals(Data.ActiveLogin, login, StringComparison.OrdinalIgnoreCase))
                Data.ActiveLogin = null;
            Save();
        }
        SecureStore.RemoveToken(login);
    }

    public void SetActive(string? login)
    {
        Data.ActiveLogin = login;
        Save();
    }
}
