<#
.SYNOPSIS
    Verifies a deployed Foundry token usage report app and prints a per-agent summary.

.DESCRIPTION
    Reads the app URL and API key from the App Service, calls /healthz and /api/usage (last 24 hours by default), and
    shows the totals per agent. Throws (non-zero exit) when the app is not healthy or the report call fails.

.EXAMPLE
    .\scripts\verify-deployment.ps1 -ResourceGroup rg_tokenrpt -AppName tokrpt-app-4f3dd3 -LookbackHours 168
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ResourceGroup,
    [Parameter(Mandatory = $true)] [string] $AppName,
    [string] $SubscriptionId,
    [int] $LookbackHours = 24
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
if ($SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }

$hostName = az webapp show -g $ResourceGroup -n $AppName --query defaultHostName -o tsv
if (-not $hostName) { throw "Web app $AppName was not found in $ResourceGroup." }
$url = "https://$hostName"
$key = az webapp config appsettings list -g $ResourceGroup -n $AppName --query "[?name=='REPORTS_API_KEY'].value | [0]" -o tsv
if (-not $key) { throw "The app has no REPORTS_API_KEY setting." }

Write-Host "App URL: $url"
$health = Invoke-RestMethod "$url/healthz" -TimeoutSec 180
if (-not $health.ok) { throw "/healthz did not report ok." }
Write-Host "Health : ok"

$report = Invoke-RestMethod "$url/api/usage?lookbackHours=$LookbackHours" -Headers @{ "X-Api-Key" = $key } -TimeoutSec 300
Write-Host ("Window : {0} -> {1}" -f $report.windowStartUtc, $report.windowEndUtc)
$projects = @($report.inventory.accounts | ForEach-Object { $_.projects }).Count
Write-Host "Scope  : $(@($report.inventory.subscriptions).Count) subscription(s), $projects Foundry project(s), $(@($report.inventory.appInsights).Count) Application Insights"
foreach ($w in @($report.warnings)) { Write-Warning "[$($w.scope)] $($w.message)" }

$rows = @($report.facts | Group-Object agentName | ForEach-Object {
    [pscustomobject]@{
        Agent  = $_.Name
        Calls  = ($_.Group | Measure-Object calls -Sum).Sum
        Input  = ($_.Group | Measure-Object promptTokens -Sum).Sum
        Output = ($_.Group | Measure-Object completionTokens -Sum).Sum
        Total  = ($_.Group | Measure-Object totalTokens -Sum).Sum
    }
})
if ($rows.Count -eq 0) {
    Write-Warning "The report works but returned no usage for the last $LookbackHours hour(s). Send a prompt to an agent, wait a few minutes for ingestion and run again."
} else {
    $rows | Sort-Object Total -Descending | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host ("Total tokens: {0}" -f ($rows | Measure-Object Total -Sum).Sum)
}
Write-Host "Verification passed." -ForegroundColor Green
