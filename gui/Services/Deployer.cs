using System.Diagnostics;
using System.Threading.Channels;

namespace DeployPortal.Services;

/// <summary>Parameters collected from the GUI to drive deploy.ps1.</summary>
public sealed record DeployRequest(
    string Location,
    string SubscriptionId,
    string BaseName,
    string ResourceGroup,
    string PlanSku,
    bool RunNow,
    // Optional explicit resource names (existing-or-create). Null => derived by the script.
    string? PlanName = null,
    string? PlanResourceGroup = null,
    string? AppName = null,
    // Foundry hierarchy scope: the subscriptions to report on, and what discovery found in them.
    IReadOnlyList<string>? Subscriptions = null,
    string? TenantId = null,
    string? TenantName = null,
    IReadOnlyList<string>? AppInsightsIds = null,
    IReadOnlyList<string>? FoundryProjectIds = null,
    bool GrantSubscriptionMonitoringReader = false,
    bool GrantAgentReadAccess = false);

/// <summary>
/// Invokes the existing deploy.ps1 (single source of truth for provisioning + RBAC)
/// and streams its stdout/stderr line-by-line so the GUI can show live progress.
/// </summary>
public sealed class Deployer
{
    private static readonly System.Text.RegularExpressions.Regex ArmId =
        new(@"^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9._()-]+/providers/[A-Za-z0-9._()/-]+$");
    private static readonly System.Text.RegularExpressions.Regex TenantNamePattern = new(@"^[\w .,'()&-]{1,100}$");

    private readonly string _scriptPath;
    private readonly string _pwsh;

    public Deployer(IWebHostEnvironment env)
    {
        // gui/ lives next to deploy/. Resolve deploy.ps1 relative to the app content root.
        var root = Directory.GetParent(env.ContentRootPath)?.FullName ?? env.ContentRootPath;
        _scriptPath = Path.Combine(root, "deploy", "deploy.ps1");
        _pwsh = ResolvePwsh();
    }

    public string ScriptPath => _scriptPath;
    public bool ScriptExists => File.Exists(_scriptPath);

    private static string ResolvePwsh()
    {
        // Prefer PowerShell 7 (pwsh); fall back to Windows PowerShell.
        foreach (var candidate in new[] { "pwsh", "powershell" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-NoProfile -Command $PSVersionTable.PSVersion.Major",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p!.WaitForExit(8000);
                if (p.ExitCode == 0) return candidate;
            }
            catch { /* try next */ }
        }
        return "powershell";
    }

    /// <summary>Runs the deployment, yielding each output line as it is produced.</summary>
    public async IAsyncEnumerable<string> RunAsync(
        DeployRequest req,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!ScriptExists)
        {
            yield return $"ERROR: deploy script not found at {_scriptPath}";
            yield break;
        }

        var args = new List<string>
        {
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", _scriptPath,
            "-Location", req.Location,
            "-SubscriptionId", req.SubscriptionId,
            "-BaseName", req.BaseName,
            "-ResourceGroup", req.ResourceGroup,
            "-PlanSku", req.PlanSku,
        };
        if (req.RunNow) args.Add("-RunNow");
        if (req.GrantSubscriptionMonitoringReader) args.Add("-GrantSubscriptionMonitoringReader");
        if (req.GrantAgentReadAccess) args.Add("-GrantAgentReadAccess");

        void AddIf(string flag, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) { args.Add(flag); args.Add(value); }
        }

        // The script receives lists as comma-separated strings. Only well-formed ids are forwarded.
        var subscriptions = (req.Subscriptions ?? Array.Empty<string>()).Where(s => Guid.TryParse(s, out _)).Select(s => s.ToLowerInvariant()).Distinct().ToList();
        if (subscriptions.Count == 0)
        {
            yield return "ERROR: select at least one subscription to report on.";
            yield break;
        }
        static IEnumerable<string> ArmIds(IEnumerable<string>? ids) =>
            (ids ?? Array.Empty<string>()).Where(i => ArmId.IsMatch(i)).Distinct(StringComparer.OrdinalIgnoreCase);
        AddIf("-Subscriptions", string.Join(',', subscriptions));
        AddIf("-AppInsightsIds", string.Join(',', ArmIds(req.AppInsightsIds)));
        AddIf("-FoundryProjectIds", string.Join(',', ArmIds(req.FoundryProjectIds)));
        if (req.TenantId is not null && Guid.TryParse(req.TenantId, out _)) AddIf("-TenantId", req.TenantId);
        if (req.TenantName is not null && TenantNamePattern.IsMatch(req.TenantName)) AddIf("-TenantName", req.TenantName);
        AddIf("-PlanName", req.PlanName);
        AddIf("-PlanResourceGroup", req.PlanResourceGroup);
        AddIf("-AppName", req.AppName);

        var psi = new ProcessStartInfo
        {
            FileName = _pwsh,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var channel = Channel.CreateUnbounded<string>();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => { if (e.Data != null) channel.Writer.TryWrite(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) channel.Writer.TryWrite("! " + e.Data); };
        process.Exited += (_, _) =>
        {
            channel.Writer.TryWrite($"__EXIT__ {process.ExitCode}");
            channel.Writer.TryComplete();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch { } });

        await foreach (var line in channel.Reader.ReadAllAsync(CancellationToken.None))
            yield return line;
    }
}
