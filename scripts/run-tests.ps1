<#
.SYNOPSIS
    Runs every automated check: builds, .NET unit tests, portal (JavaScript) tests and script syntax.

.EXAMPLE
    .\scripts\run-tests.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
$script:failed = @()
function Step([string] $Name, [scriptblock] $Block) {
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    & $Block
    if ($LASTEXITCODE -ne 0) { $script:failed += $Name; Write-Host "FAILED: $Name" -ForegroundColor Red }
}
try {
    Step "Build report app"          { dotnet build .\src\TokenUsageReport\TokenUsageReport.csproj -c Release --nologo -v q }
    Step "Build deployment portal"   { dotnet build .\gui\DeployPortal.csproj -c Release --nologo -v q }
    Step ".NET unit tests"           { dotnet test .\tests\TokenUsageReport.Tests\TokenUsageReport.Tests.csproj --nologo -v q }
    Step "Portal script syntax"      { node --check .\gui\wwwroot\wizard.js; if ($LASTEXITCODE -eq 0) { node --check .\gui\wwwroot\dashboard.js } }
    Step "Portal (JavaScript) tests" { node --test .\tests\portal.test.cjs }
    Step "PowerShell script syntax"  {
        $bad = 0
        Get-ChildItem .\deploy, .\scripts, .\gui -Filter *.ps1 -Recurse | ForEach-Object {
            $errs = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$errs)
            if ($errs.Count) { $bad++; Write-Host "$($_.Name): $($errs[0].Message)" -ForegroundColor Red }
        }
        $global:LASTEXITCODE = $bad
    }
    if ($script:failed.Count) { Write-Host "`nFailed: $($script:failed -join ', ')" -ForegroundColor Red; exit 1 }
    Write-Host "`nAll checks passed." -ForegroundColor Green
} finally { Pop-Location }
