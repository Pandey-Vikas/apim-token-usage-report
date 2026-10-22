using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeployPortal.Services;

/// <summary>
/// Manages an interactive `az login` in the background and exposes a poll-able snapshot.
///
/// Design: the login is considered complete when the `az login` process EXITS (the Windows
/// account-picker / browser flow finishes) — at which point `az account show` reflects the
/// account the user actually chose (supporting account switching without a tenant hint).
/// As a fallback for environments where the process lingers, a detected change of the active
/// account (vs. the baseline captured at start) also counts as completion.
/// </summary>
public sealed partial class LoginManager
{
    private static readonly string Command = OperatingSystem.IsWindows() ? "az.cmd" : "az";
    private static readonly bool UseShell = OperatingSystem.IsWindows();

    private readonly AzCli _az;
    private readonly object _lock = new();
    private Process? _active;

    // Phase: idle | running | exited | done
    private string _phase = "idle";
    private int? _exitCode;
    private string? _error;
    private string? _url;
    private string? _code;
    private string? _baselineKey;
    private string? _requestedTenant;
    private long _baselineStamp;

    public LoginManager(AzCli az) => _az = az;

    public LoginSnapshot Snapshot()
    {
        lock (_lock)
            return new LoginSnapshot(_phase, _exitCode, _error, _url, _code, _baselineKey, _requestedTenant);
    }

    /// <summary>True once the Azure CLI token cache / profile has been written since login start
    /// (i.e. authentication just completed) — a fast, reliable completion signal.</summary>
    public bool CacheAdvanced()
    {
        lock (_lock)
            return AzureConfigStamp() > _baselineStamp;
    }

    /// <summary>Latest write time (ticks) across the Azure CLI session files.</summary>
    private static long AzureConfigStamp()
    {
        var dir = Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR");
        if (string.IsNullOrEmpty(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure");

        long max = 0;
        foreach (var name in new[] { "azureProfile.json", "msal_token_cache.json", "msal_token_cache.bin", "service_principal_entries.json" })
        {
            try
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p))
                {
                    var t = File.GetLastWriteTimeUtc(p).Ticks;
                    if (t > max) max = t;
                }
            }
            catch { /* ignore */ }
        }
        return max;
    }

    public Task StartBrowserLoginAsync(string? tenantId)
    {
        var args = new List<string> { "login", "--only-show-errors" };
        if (!string.IsNullOrWhiteSpace(tenantId)) { args.Add("--tenant"); args.Add(tenantId); }
        return StartAsync(args, LoginKind.Browser, tenantId);
    }

    public Task StartDeviceCodeLoginAsync(string? tenantId)
    {
        var args = new List<string> { "login", "--use-device-code", "--only-show-errors" };
        if (!string.IsNullOrWhiteSpace(tenantId)) { args.Add("--tenant"); args.Add(tenantId); }
        return StartAsync(args, LoginKind.DeviceCode, tenantId);
    }

    /// <summary>Confirmed a valid session — stop the (possibly lingering) login process.</summary>
    public void MarkResolved()
    {
        lock (_lock)
        {
            KillActive();
            _phase = "done";
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            KillActive();
            _phase = "idle";
            _exitCode = null; _error = null; _url = null; _code = null;
        }
    }

    /// <summary>Stable identity key (tenant + user) for an `az account show` result.</summary>
    public static string? AccountKey(JsonElement? account)
    {
        if (account is not { ValueKind: JsonValueKind.Object } e) return null;
        var tenant = e.TryGetProperty("tenantId", out var t) ? t.GetString() : null;
        var user = e.TryGetProperty("user", out var u) && u.TryGetProperty("name", out var n) ? n.GetString() : null;
        return $"{tenant}|{user}";
    }

    private enum LoginKind { Browser, DeviceCode }

    private async Task StartAsync(List<string> args, LoginKind kind, string? tenantId)
    {
        // Capture the current session (if any) so polling can detect a fresh/changed login.
        string? baseline = null;
        try { baseline = AccountKey(await _az.JsonAsync(new[] { "account", "show" }, timeoutMs: 15_000)); }
        catch { /* not signed in */ }

        var buffer = new StringBuilder();

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
            foreach (var a in args)
                if (a.IndexOfAny(new[] { '&', '|', '<', '>', '^', '%', '"', '\r', '\n' }) >= 0)
                    throw new AzCliException("Argument contains unsupported characters.");
            psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(Command);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void OnData(string? data)
        {
            if (data == null) return;
            buffer.AppendLine(data);
            var text = buffer.ToString();

            if (kind == LoginKind.Browser)
            {
                var m = BrowserUrlRegex().Match(text);
                if (m.Success)
                    lock (_lock)
                        if (_phase == "running" && _url is null) _url = m.Value;
            }
            else
            {
                var codeMatch = DeviceCodeRegex().Match(text);
                if (codeMatch.Success)
                {
                    var urlMatch = DeviceUrlRegex().Match(text);
                    var url = urlMatch.Success ? urlMatch.Value : "https://microsoft.com/devicelogin";
                    lock (_lock)
                        if (_phase == "running" && _code is null) { _url = url; _code = codeMatch.Groups[1].Value; }
                }
            }
        }

        process.OutputDataReceived += (_, e) => OnData(e.Data);
        process.ErrorDataReceived += (_, e) => OnData(e.Data);
        process.Exited += (_, _) =>
        {
            var code = process.ExitCode;
            lock (_lock)
            {
                if (!ReferenceEquals(_active, process)) { TryDispose(process); return; }
                _active = null;
                _phase = "exited";
                _exitCode = code;
                if (code != 0) _error = buffer.ToString().Trim();
            }
            TryDispose(process);
        };

        lock (_lock)
        {
            KillActive();
            _baselineKey = baseline;
            _requestedTenant = tenantId;
            _baselineStamp = AzureConfigStamp();
            _phase = "running";
            _exitCode = null; _error = null; _url = null; _code = null;
            _active = process;
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void KillActive()
    {
        if (_active is null) return;
        try { _active.Kill(entireProcessTree: true); } catch { /* ignore */ }
        _active = null;
    }

    private static void TryDispose(Process p) { try { p.Dispose(); } catch { /* ignore */ } }

    [GeneratedRegex(@"https://login\.microsoftonline\.com/[^\s""'<>]+")]
    private static partial Regex BrowserUrlRegex();

    [GeneratedRegex(@"enter the code\s+([A-Z0-9-]{6,})\s+to authenticate", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceCodeRegex();

    [GeneratedRegex(@"https?://\S+devicelogin", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceUrlRegex();
}

/// <summary>phase: idle | running | exited | done.</summary>
public sealed record LoginSnapshot(
    string Phase, int? ExitCode, string? Error, string? Url, string? Code, string? BaselineKey, string? RequestedTenant);
