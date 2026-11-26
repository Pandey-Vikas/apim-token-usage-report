<#
.SYNOPSIS
    Non-interactive deployment of the Foundry token usage report (no portal needed).

.DESCRIPTION
    Wraps deploy\deploy.ps1 for the hierarchical report (Tenant > Subscriptions > Resource groups > Foundry >
    linked Application Insights > Agents). It
      1. checks the Azure CLI sign-in and the selected subscriptions,
      2. discovers the Application Insights components in those subscriptions (Azure Resource Graph) unless you pass
         -AppInsightsIds, so the app's managed identity is granted Monitoring Reader on exactly those components,
      3. runs deploy\deploy.ps1 (creates or reuses the resource group, App Service plan and web app; publishes the app).
    Everything is idempotent: re-running updates the same app and keeps its API key.

.EXAMPLE
    .\scripts\deploy-foundry-report.ps1 -SubscriptionIds a3b144fd-39ee-4729-b816-3ed42fc8f31f -ResourceGroup rg_tokenrpt -Location swedencentral

.EXAMPLE
    # Update an existing app and plan
    .\scripts\deploy-foundry-report.ps1 -SubscriptionIds <sub1>,<sub2> -ResourceGroup rg_tokenrpt -Location swedencentral `
        -AppName tokrpt-app-4f3dd3 -PlanName tokrpt-plan -PlanSku F1

.NOTES
    Needs: Azure CLI signed in (az login), .NET SDK 9+, and Owner or (Contributor + User Access Administrator)
    on the hosting resource group and on the monitored subscriptions to create the role assignments.
#>
[CmdletBinding()]
param(
    # Subscriptions the report covers (comma separated or an array).
    [Parameter(Mandatory = $true)]
    [string[]] $SubscriptionIds,

    # Resource group that hosts the report app (created if missing).
    [Parameter(Mandatory = $true)]
    [string] $ResourceGroup,

    [Parameter(Mandatory = $true)]
    [string] $Location,

    # Subscription that hosts the app. Defaults to the first of -SubscriptionIds.
    [string] $HostingSubscriptionId,

    [string] $BaseName = "tokrpt",
    [string] $AppName,
    [string] $PlanName,
    [string] $PlanResourceGroup,
    [ValidateSet("F1", "B1", "B2", "S1")]
    [string] $PlanSku = "B1",

    # Restrict Monitoring Reader to these Application Insights components (comma separated ARM ids).
    # Default: every Application Insights component found in the selected subscriptions.
    [string[]] $AppInsightsIds,

    # Grant Monitoring Reader on the whole subscriptions so Foundry projects created later are reported too.
    [switch] $IncludeFutureProjects,

    # Also list agents that have no activity. Grants the built-in "Foundry User" role on every Foundry project,
    # which can also list account keys and connection secrets, so it is off by default.
    [switch] $ListIdleAgents,

    # Run the report once after deployment.
    [switch] $RunNow
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$repoRoot = Split-Path $PSScriptRoot -Parent

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Split-Ids([string[]] $Values) { @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

# Runs an Azure Resource Graph query over the selected subscriptions and returns the ids.
function Invoke-GraphIds([string[]] $Subscriptions, [string] $Query) {
    $body = @{ subscriptions = $Subscriptions; query = $Query } | ConvertTo-Json -Compress
    $tmp = New-TemporaryFile
    try {
        Set-Content -Path $tmp -Value $body -Encoding utf8
        $ids = az rest --method post --url "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01" `
            --body "@$tmp" --query "data[].id" -o tsv
        if ($LASTEXITCODE -ne 0) { throw "Azure Resource Graph query failed." }
        return @($ids | Where-Object { $_ })
    } finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

$subs = @(Split-Ids $SubscriptionIds)
$insightsIds = @(Split-Ids $AppInsightsIds)
foreach ($s in $subs) { if ($s -notmatch '^[0-9a-fA-F-]{36}$') { throw "'$s' is not a subscription id (GUID)." } }
if (-not $HostingSubscriptionId) { $HostingSubscriptionId = $subs[0] }

Write-Step "Checking the Azure CLI sign-in"
$account = az account show -o json 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not signed in. Run 'az login' first." }
Write-Host "Signed in as $($account.user.name) (tenant $($account.tenantId))"
$visible = @(az account list --query "[].id" -o tsv)
foreach ($s in $subs + $HostingSubscriptionId) { if ($visible -notcontains $s) { throw "Subscription $s is not visible to this sign-in." } }

$tenantName = ""
try {
    $tenantName = az rest --method get --url "https://management.azure.com/tenants?api-version=2022-12-01" `
        --query "value[?tenantId=='$($account.tenantId)'].displayName | [0]" -o tsv 2>$null
} catch { }

if (-not $IncludeFutureProjects -and $insightsIds.Count -eq 0) {
    Write-Step "Discovering Application Insights components in $($subs.Count) subscription(s)"
    $insightsIds = Invoke-GraphIds $subs "resources | where type =~ 'microsoft.insights/components' | project id"
    if ($insightsIds.Count -eq 0) {
        Write-Warning "No Application Insights components found. Granting Monitoring Reader on the subscriptions instead."
        $IncludeFutureProjects = $true
    } else {
        Write-Host "Found $($insightsIds.Count) component(s); the app will get Monitoring Reader on each."
    }
}

$deployArgs = @{
    Subscriptions  = ($subs -join ',')
    SubscriptionId = $HostingSubscriptionId
    ResourceGroup  = $ResourceGroup
    Location       = $Location
    BaseName       = $BaseName
    PlanSku        = $PlanSku
    TenantId       = $account.tenantId
}
if ($tenantName)              { $deployArgs.TenantName = $tenantName }
if ($AppName)                 { $deployArgs.AppName = $AppName }
if ($PlanName)                { $deployArgs.PlanName = $PlanName }
if ($PlanResourceGroup)       { $deployArgs.PlanResourceGroup = $PlanResourceGroup }
if ($insightsIds.Count -gt 0) { $deployArgs.AppInsightsIds = ($insightsIds -join ',') }
if ($IncludeFutureProjects)   { $deployArgs.GrantSubscriptionMonitoringReader = $true }
if ($ListIdleAgents) {
    Write-Step "Discovering Foundry projects (for the Foundry User role)"
    $projects = Invoke-GraphIds $subs "resources | where type =~ 'microsoft.cognitiveservices/accounts/projects' | project id"
    if ($projects.Count -gt 0) { $deployArgs.FoundryProjectIds = ($projects -join ','); $deployArgs.GrantAgentReadAccess = $true }
}
if ($RunNow) { $deployArgs.RunNow = $true }

Write-Step "Running deploy\deploy.ps1"
& (Join-Path $repoRoot "deploy\deploy.ps1") @deployArgs
Write-Host "`nNext: .\scripts\verify-deployment.ps1 -ResourceGroup $ResourceGroup -AppName <app name printed above>" -ForegroundColor Green
