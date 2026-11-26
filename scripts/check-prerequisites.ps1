<#
.SYNOPSIS
    Checks the tools and sign-in needed to build, run and deploy the report.

.EXAMPLE
    .\scripts\check-prerequisites.ps1
#>
[CmdletBinding()]
param()

$script:ok = $true
function Check([string] $Name, [bool] $Pass, [string] $Detail, [string] $Fix) {
    if ($Pass) { Write-Host ("[ok]      {0,-20} {1}" -f $Name, $Detail) -ForegroundColor Green }
    else { Write-Host ("[MISSING] {0,-20} {1}" -f $Name, $Fix) -ForegroundColor Red; $script:ok = $false }
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$sdk = $null
if ($dotnet) { $sdk = (dotnet --list-sdks | ForEach-Object { [version](($_ -split ' ')[0] -replace '-.*$', '') } | Sort-Object -Descending | Select-Object -First 1) }
Check ".NET SDK 9+" ($null -ne $sdk -and $sdk.Major -ge 9) "$sdk" "Install 9.0 or newer from https://dotnet.microsoft.com/download"

$az = Get-Command az -ErrorAction SilentlyContinue
Check "Azure CLI" ([bool]$az) "installed" "Install from https://aka.ms/installazurecliwindows"

$account = $null
if ($az) { $account = az account show -o json 2>$null | ConvertFrom-Json }
Check "Azure sign-in" ([bool]$account) "$($account.user.name) / tenant $($account.tenantId)" "Run: az login"

$node = Get-Command node -ErrorAction SilentlyContinue
Check "Node.js (tests)" ([bool]$node) "$(if ($node) { node --version })" "Install Node 20+ from https://nodejs.org (only needed by run-tests.ps1)"

if ($account) {
    $subs = @(az account list --query "[?state=='Enabled'].{n:name,id:id}" -o json | ConvertFrom-Json)
    Write-Host "`nSubscriptions visible to this sign-in: $($subs.Count)"
    $subs | Select-Object -First 15 | ForEach-Object { Write-Host ("  {0}  {1}" -f $_.id, $_.n) }
    if ($subs.Count -gt 15) { Write-Host "  ... and $($subs.Count - 15) more (az account list -o table)" }
}
if (-not $script:ok) { exit 1 }
Write-Host "`nAll prerequisites are in place." -ForegroundColor Green
