<#
.SYNOPSIS
    Starts the local Deployment Portal + live report dashboard and opens it in the browser.

.DESCRIPTION
    The portal runs only on 127.0.0.1. It uses your Azure CLI sign-in: it can walk you through the install wizard
    (tenant > subscriptions > detect Foundry > hosting > deploy) and open the report dashboard directly.

.EXAMPLE
    .\scripts\start-portal.ps1
    .\scripts\start-portal.ps1 -Port 5100 -NoBrowser
#>
[CmdletBinding()]
param(
    [int] $Port = 5099,
    [switch] $NoBrowser
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path $PSScriptRoot -Parent
$url = "http://127.0.0.1:$Port"
$env:PORTAL_URL = $url

if (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use. Pick another with -Port."
}
if (-not $NoBrowser) { Start-Job -ScriptBlock { Start-Sleep -Seconds 5; Start-Process $using:url } | Out-Null }
Write-Host "Portal: $url  (Ctrl+C to stop)" -ForegroundColor Cyan
dotnet run --project (Join-Path $repoRoot "gui\DeployPortal.csproj") -c Release
