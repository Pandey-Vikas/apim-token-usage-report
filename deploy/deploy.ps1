<#
.SYNOPSIS
    Provisions a minimal Azure App Service web app that generates the Token Usage Report
    on demand from Application Insights and serves it through a secured API. No storage
    account, no private endpoint, no VNet — the app "routes" reports via its own code.

.DESCRIPTION
    One script, end to end (every step idempotent — safe to re-run / resume after a failure):
      1. Creates/reuses a resource group.
      2. Creates/reuses a Windows App Service plan and Web App with a system-assigned identity.
      3. Grants the identity least-privilege read access:
           - Monitoring Reader on the Application Insights component
           - Reader on the subscription (Resource Graph + list AI deployments for enrichment)
      4. Publishes the web app and sets a generated REPORTS_API_KEY.

    The app exposes (all require the X-Api-Key header):
      POST /api/run                          generate the default-window report
      GET  /api/report?lookbackHours=N       generate any window (JSON)
      GET  /api/report.csv?lookbackHours=N    same, raw CSV (Power BI Web connector friendly)

.PREREQUISITES
    - Azure CLI (az) logged in:  az login
    - .NET SDK 9+
    - An existing Application Insights component already receiving APIM token metrics.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $AppInsightsResourceId,

    [Parameter(Mandatory = $false)]
    [string] $Location = "eastus",

    [Parameter(Mandatory = $false)]
    [string] $SubscriptionId,

    [Parameter(Mandatory = $false)]
    [string] $BaseName = "tokrpt",

    [Parameter(Mandatory = $false)]
    [string] $ResourceGroup,

    [Parameter(Mandatory = $false)]
    [string] $PlanName,

    [Parameter(Mandatory = $false)]
    [string] $PlanResourceGroup,

    [Parameter(Mandatory = $false)]
    [string] $AppName,

    # App Service plan SKU. B1 keeps the app warm; F1 (Free) also works for on-demand.
    [Parameter(Mandatory = $false)]
    [string] $PlanSku = "B1",

    [Parameter(Mandatory = $false)]
    [int] $LookbackHours = 24,

    [Parameter(Mandatory = $false)]
    [int] $EndOffsetHours = 0,

    # Trigger a report run at the end of deployment.
    [switch] $RunNow,

    # POC convenience: also grant Reader on every enabled subscription you can access so model/account/SKU
    # enrichment works for Azure OpenAI accounts in other subscriptions (no manual role assignment needed).
    [switch] $GrantAllSubscriptions
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Write-Step([string] $Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }

# --- Resolve defaults ------------------------------------------------------
$sha = [Security.Cryptography.SHA256]::Create()
$hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($AppInsightsResourceId))
$suffix = ([BitConverter]::ToString($hashBytes) -replace '-', '').Substring(0, 6).ToLower()
if (-not $ResourceGroup)     { $ResourceGroup = "$BaseName-rg" }
if (-not $AppName)           { $AppName = "$BaseName-app-$suffix" }
if (-not $PlanName)          { $PlanName = "$BaseName-plan" }
if (-not $PlanResourceGroup) { $PlanResourceGroup = $ResourceGroup }

$scriptRoot  = $PSScriptRoot
$projectPath = Join-Path $scriptRoot "..\src\TokenUsageReport\TokenUsageReport.csproj" | Resolve-Path

# --- Verify az login -------------------------------------------------------
Write-Step "Verifying Azure CLI login"
$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not logged in. Run 'az login' first." }
if ($SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }
$account = az account show | ConvertFrom-Json
$SubscriptionId = $account.id
Write-Host "Subscription: $($account.name) ($SubscriptionId)"

# --- Resource group --------------------------------------------------------
if ((az group exists --name $ResourceGroup) -eq "true") {
    Write-Step "Using existing resource group '$ResourceGroup'"
} else {
    Write-Step "Creating resource group '$ResourceGroup' in $Location"
    az group create --name $ResourceGroup --location $Location --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create resource group '$ResourceGroup'." }
}

# --- App Service plan (Windows) --------------------------------------------
$existingPlan = az appservice plan show --name $PlanName --resource-group $PlanResourceGroup -o json 2>$null | ConvertFrom-Json
if ($existingPlan) {
    if ($existingPlan.reserved -eq $true -or ($existingPlan.kind -and $existingPlan.kind -match 'linux')) {
        throw "App Service plan '$PlanName' (rg: $PlanResourceGroup) is a LINUX plan. This app requires a WINDOWS plan. Delete it (az appservice plan delete -g $PlanResourceGroup -n $PlanName) or choose a different plan name, then re-run."
    }
    Write-Step "Using existing App Service plan '$PlanName' (rg: $PlanResourceGroup)"
    $planId = $existingPlan.id
} else {
    Write-Step "Creating Windows App Service plan '$PlanName' ($PlanSku)"
    az appservice plan create --name $PlanName --resource-group $ResourceGroup `
        --location $Location --sku $PlanSku --is-linux false --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create App Service plan '$PlanName'." }
    $planId = az appservice plan show --name $PlanName --resource-group $ResourceGroup --query id -o tsv
}
if (-not $planId) { throw "Could not resolve App Service plan id for '$PlanName'." }

# --- Web app (Windows) -----------------------------------------------------
$existingApp = az webapp show --name $AppName --resource-group $ResourceGroup -o json 2>$null | ConvertFrom-Json
if ($existingApp) {
    Write-Step "Using existing Web App '$AppName'"
} else {
    Write-Step "Creating Web App '$AppName'"
    az webapp create --name $AppName --resource-group $ResourceGroup --plan $planId `
        --runtime "dotnet:9" --output none
    if ($LASTEXITCODE -ne 0) { throw "Failed to create Web App '$AppName'." }
}

$appId = az webapp show --name $AppName --resource-group $ResourceGroup --query id -o tsv 2>$null
if (-not $appId) { throw "Web App '$AppName' does not exist after create step; aborting." }

Write-Step "Enabling Always On + assigning managed identity"
az webapp config set --name $AppName --resource-group $ResourceGroup --always-on true --output none 2>$null
$principalId = az webapp identity assign --name $AppName --resource-group $ResourceGroup `
    --query principalId -o tsv
if (-not $principalId) { throw "Failed to assign a managed identity to '$AppName'." }

# --- App settings ----------------------------------------------------------
# Reuse an existing API key if the app already has one (keeps the portal's saved key valid).
$apiKey = az webapp config appsettings list --name $AppName --resource-group $ResourceGroup `
    --query "[?name=='REPORTS_API_KEY'].value | [0]" -o tsv 2>$null
if (-not $apiKey) { $apiKey = [Guid]::NewGuid().ToString('N') }

Write-Step "Configuring application settings"
az webapp config appsettings set --name $AppName --resource-group $ResourceGroup --settings `
    "APPINSIGHTS_RESOURCE_ID=$AppInsightsResourceId" `
    "REPORT_LOOKBACK_HOURS=$LookbackHours" `
    "REPORT_END_OFFSET_HOURS=$EndOffsetHours" `
    "REPORTS_API_KEY=$apiKey" `
    --output none
if ($LASTEXITCODE -ne 0) { throw "Failed to set application settings on '$AppName'." }

# --- Role assignments (idempotent + retry for identity propagation) ---------
function Grant-Role([string] $Role, [string] $Scope, [int] $Retries = 6) {
    $existing = az role assignment list --assignee $principalId --role $Role --scope $Scope --query "[0].id" -o tsv 2>$null
    if ($existing) { Write-Host "  '$Role' already assigned"; return }
    $lastErr = ""
    for ($i = 1; $i -le $Retries; $i++) {
        $lastErr = az role assignment create --assignee-object-id $principalId `
            --assignee-principal-type ServicePrincipal --role $Role --scope $Scope --output none 2>&1
        if ($LASTEXITCODE -eq 0) { Write-Host "  granted '$Role'"; return }
        if ($lastErr -match 'already exists|RoleAssignmentExists') { Write-Host "  '$Role' already assigned"; return }
        Start-Sleep -Seconds 10
    }
    throw "Failed to assign role '$Role' on scope '$Scope'. Last error: $lastErr"
}

Write-Step "Assigning RBAC roles to the app identity"
Grant-Role "Monitoring Reader"  $AppInsightsResourceId
Grant-Role "Reader"             "/subscriptions/$SubscriptionId"

if ($GrantAllSubscriptions) {
    Write-Step "Granting Reader on all other accessible subscriptions (for cross-subscription enrichment)"
    $otherSubs = az account list --query "[?state=='Enabled' && id!='$SubscriptionId'].id" -o tsv 2>$null
    foreach ($sub in $otherSubs) {
        if (-not $sub) { continue }
        try { Grant-Role "Reader" "/subscriptions/$sub" -Retries 1 }
        catch { Write-Warning "  Skipped subscription ${sub}: $($_.Exception.Message)" }
    }
}

# --- Make sure Application Insights can be queried over the public endpoint (best effort) -----
Write-Step "Checking Application Insights query access"
try {
    $env:AZURE_EXTENSION_USE_DYNAMIC_INSTALL = "yes_without_prompt"
    $queryAccess = az monitor app-insights component show --ids $AppInsightsResourceId `
        --query "publicNetworkAccessForQuery" -o tsv --only-show-errors 2>$null
    if ($queryAccess -and $queryAccess -ne "Enabled") {
        az monitor app-insights component update --ids $AppInsightsResourceId --query-access Enabled `
            --output none --only-show-errors 2>$null
        if ($LASTEXITCODE -eq 0) { Write-Host "  enabled public query access (was '$queryAccess')" }
        else { Write-Warning "  could not enable public query access; the report may fail if the component is network-isolated." }
    } else { Write-Host "  query access OK" }
}
catch { Write-Warning "  skipped query-access check: $($_.Exception.Message)" }

# --- Build & package the web app -------------------------------------------
Write-Step "Publishing .NET web app"
$publishDir = Join-Path $scriptRoot "_publish"
$zipPath    = Join-Path $scriptRoot "app.zip"
Remove-Item -Recurse -Force $publishDir, $zipPath -ErrorAction SilentlyContinue

dotnet publish $projectPath -c Release -o $publishDir | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Write-Step "Zipping deployment package"
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -Force

# --- Deploy ----------------------------------------------------------------
Write-Step "Deploying web app to '$AppName'"
az webapp deploy --name $AppName --resource-group $ResourceGroup `
    --src-path $zipPath --type zip --async false --output none
if ($LASTEXITCODE -ne 0) { throw "Failed to deploy the app package to '$AppName'." }

$appUrl = "https://$AppName.azurewebsites.net"

# --- Optional immediate run -----------------------------------------------
if ($RunNow) {
    Write-Step "Triggering an initial report run"
    Start-Sleep -Seconds 20   # allow the site to warm up after deploy
    try {
        $resp = Invoke-RestMethod -Method Post -Uri "$appUrl/api/run" -Headers @{ 'X-Api-Key' = $apiKey } -TimeoutSec 180
        Write-Host "  run complete: $($resp.rowCount) row(s) for $($resp.reportDate)."
    }
    catch {
        Write-Warning "Could not trigger the initial run yet (the app may still be starting): $($_.Exception.Message)"
        Write-Warning "Use 'Run report now' in the portal instead."
    }
}

# --- Cleanup ---------------------------------------------------------------
Remove-Item -Recurse -Force $publishDir, $zipPath -ErrorAction SilentlyContinue

Write-Step "Done"
Write-Host "App Service : $AppName"
Write-Host "App URL     : $appUrl"
Write-Host "Api Key     : $apiKey"
Write-Host "Report API  : $appUrl/api/report.csv?lookbackHours=$LookbackHours  (header X-Api-Key: <key>)"
Write-Host "`nThe app generates reports on demand from Application Insights and serves them through its"
Write-Host "secured API. No storage account, no private endpoint — the portal and Power BI call the app."
