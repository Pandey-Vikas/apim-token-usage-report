<#
.SYNOPSIS
    Launches the Token Usage Report deployment portal (local web GUI) and opens it in a browser.

.DESCRIPTION
    Builds and runs the ASP.NET portal, then opens http://127.0.0.1:5099 where you can:
      1. Sign in to Azure (browser or device code, via the Azure CLI)
      2. Select a subscription
      3. Pick or create a resource group
      4. Configure and click Deploy — the portal provisions all resources and sets
         the required Entra/RBAC permissions by running deploy.ps1.

.PREREQUISITES
    - .NET SDK 9+
    - Azure CLI (az) installed
#>
[CmdletBinding()]
param(
    [int] $Port = 5099
)

$ErrorActionPreference = "Stop"
$guiDir = $PSScriptRoot
$url = "http://127.0.0.1:$Port"
$env:PORTAL_URL = $url

Write-Host "Starting deployment portal at $url ..." -ForegroundColor Cyan
Start-Job -ScriptBlock { Start-Sleep -Seconds 4; Start-Process $using:url } | Out-Null

dotnet run --project (Join-Path $guiDir "DeployPortal.csproj") -c Release
