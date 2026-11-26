<#
.SYNOPSIS
    Runs the report web app locally (same API as the deployed app) using your Azure CLI sign-in.

.DESCRIPTION
    Sets the app settings as environment variables and starts the app on http://127.0.0.1:<Port>.
    Call it with the X-Api-Key header, for example:
      curl.exe -H "X-Api-Key: local-key" "http://127.0.0.1:5200/api/usage?lookbackHours=24"

.EXAMPLE
    .\scripts\run-local.ps1 -SubscriptionIds a3b144fd-39ee-4729-b816-3ed42fc8f31f
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string[]] $SubscriptionIds,
    [int] $Port = 5200,
    [string] $ApiKey = "local-key"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$account = az account show -o json 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not signed in. Run 'az login' first." }

$env:REPORT_SUBSCRIPTIONS = (($SubscriptionIds | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) -join ',')
$env:REPORT_TENANT_ID = $account.tenantId
if ($account.tenantDisplayName) { $env:REPORT_TENANT_NAME = $account.tenantDisplayName }
$env:REPORTS_API_KEY = $ApiKey
$env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
Write-Host "Report app: http://127.0.0.1:$Port  (X-Api-Key: $ApiKey)" -ForegroundColor Cyan
dotnet run --project (Join-Path $repoRoot "src\TokenUsageReport\TokenUsageReport.csproj") -c Release
