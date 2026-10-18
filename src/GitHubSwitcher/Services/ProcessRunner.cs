using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubSwitcher.Services;

public sealed class ProcessResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Ok => ExitCode == 0;
    public string Combined => string.Join("\n",
        new[] { Output, Error }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

/// <summary>外部命令执行封装（隐藏窗口、捕获输出）。</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName, string arguments, string? stdin = null,
        string? workingDir = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDir)) psi.WorkingDirectory = workingDir;

        using var p = new Process { StartInfo = psi };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult { ExitCode = -1, Error = $"无法启动 {fileName}：{ex.Message}" };
        }

        if (stdin != null)
        {
            await p.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
            await p.StandardInput.FlushAsync().ConfigureAwait(false);
            p.StandardInput.Close();
        }

        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }

        return new ProcessResult
        {
            ExitCode = p.ExitCode,
            Output = await outTask.ConfigureAwait(false),
            Error = await errTask.ConfigureAwait(false)
        };
    }

    public static bool Exists(string fileName)
    {
        try
        {
            if (fileName.Contains('\\') || fileName.Contains('/')) return File.Exists(fileName);
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var ext in new[] { ".exe", ".cmd", ".bat", "" })
                {
                    var cand = Path.Combine(dir.Trim(), fileName + ext);
                    if (File.Exists(cand)) return true;
                }
            }
        }
        catch { }
        return false;
    }
}
