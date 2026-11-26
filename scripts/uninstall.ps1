<#
.SYNOPSIS
    Removes the report app (and optionally the App Service plan or the whole resource group).

.EXAMPLE
    .\scripts\uninstall.ps1 -ResourceGroup rg_tokenrpt -AppName tokrpt-app-4f3dd3
    .\scripts\uninstall.ps1 -ResourceGroup rg_tokenrpt -AppName tokrpt-app-4f3dd3 -PlanName tokrpt-plan -Force
    .\scripts\uninstall.ps1 -ResourceGroup rg_tokenrpt -DeleteResourceGroup   # only if the group holds nothing else
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "High")]
param(
    [Parameter(Mandatory = $true)] [string] $ResourceGroup,
    [string] $AppName,
    [string] $PlanName,
    [string] $SubscriptionId,
    [switch] $DeleteResourceGroup,
    [switch] $Force
)

$ErrorActionPreference = "Stop"
if ($SubscriptionId) { az account set --subscription $SubscriptionId | Out-Null }
if (-not ($AppName -or $PlanName -or $DeleteResourceGroup)) { throw "Specify -AppName, -PlanName and/or -DeleteResourceGroup." }

if ($DeleteResourceGroup) {
    if ($Force -or $PSCmdlet.ShouldProcess($ResourceGroup, "Delete the whole resource group and everything in it")) {
        az group delete -n $ResourceGroup --yes --no-wait
        Write-Host "Deletion of resource group $ResourceGroup started."
    }
    return
}
if ($AppName -and ($Force -or $PSCmdlet.ShouldProcess($AppName, "Delete web app"))) {
    az webapp delete -g $ResourceGroup -n $AppName
    Write-Host "Deleted web app $AppName. Azure removes the role assignments of its managed identity with it."
}
if ($PlanName -and ($Force -or $PSCmdlet.ShouldProcess($PlanName, "Delete App Service plan"))) {
    az appservice plan delete -g $ResourceGroup -n $PlanName --yes
    Write-Host "Deleted App Service plan $PlanName."
}
