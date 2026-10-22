using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DeployPortal.Services;

/// <summary>
/// Thin wrapper around the Azure CLI. Mirrors the reference project's approach:
/// shell out to `az` (az.cmd on Windows), always request JSON, and parse.
/// </summary>
public sealed class AzCli
{
    private static readonly string Command = OperatingSystem.IsWindows() ? "az.cmd" : "az";
    private static readonly bool UseShell = OperatingSystem.IsWindows();

    /// <summary>Runs an az command and returns parsed JSON (or null for empty output).</summary>
    public async Task<JsonElement?> JsonAsync(IEnumerable<string> args, int timeoutMs = 120_000, CancellationToken ct = default)
    {
        var result = await RawAsync(args.Concat(new[] { "--only-show-errors", "--output", "json" }), timeoutMs, ct);
        if (result.ExitCode != 0)
            throw new AzCliException(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
        var text = result.StdOut.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>Runs an az command and returns the raw process result (no JSON flag added).</summary>
    public async Task<ProcessResult> RawAsync(IEnumerable<string> args, int timeoutMs = 120_000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = UseShell ? "cmd.exe" : Command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (UseShell)
        {
            // Arguments are re-parsed by cmd.exe; refuse shell metacharacters (command injection).
            foreach (var a in args)
                if (a.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', '\r', '\n' }) >= 0)
                    throw new AzCliException("Argument contains unsupported characters.");

            // Invoke via cmd so PATHEXT resolves az.cmd reliably.
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(Command);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw new AzCliException("az command timed out.");
        }

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}

public sealed class AzCliException : Exception
{
    public AzCliException(string message) : base(message.Trim()) { }
}
