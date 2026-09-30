# Token Usage Report

A **minimal .NET App Service web app** that pulls **Azure OpenAI / AI Foundry token usage**
from Application Insights, enriches it with Cognitive Services model/account/SKU metadata,
and **serves the report as CSV through a secured API** — ready to consume in a portal or
**Power BI**.

It is deliberately simple and cheap: **no storage account, no private endpoint, no VNet.**
The app generates the report **on demand** from Application Insights (which already retains
the raw token telemetry) and returns it — the portal and Power BI call the app; the client
never touches Blob Storage.

It replaces the previous Terraform + Logic App solution with:

- One **.NET web app** ([src/TokenUsageReport](./src/TokenUsageReport)) — a secured reports API.
- One **PowerShell script** ([deploy/deploy.ps1](./deploy/deploy.ps1)) that provisions everything and deploys the app.
- A **deployment portal** ([gui/](./gui)) — a local web GUI for point-and-click sign-in, deployment, and viewing reports.

### Secured reports API

Every `/api/*` call requires an **`X-Api-Key`** header (a key generated at deploy time):

| Endpoint | Purpose |
|----------|---------|
| `POST /api/run` | Generate the default-window report and return the CSV (JSON) |
| `GET /api/report?lookbackHours=N&endOffsetHours=M` | Generate any window (JSON) |
| `GET /api/report.csv?lookbackHours=N` | Same, raw `text/csv` — **Power BI Web connector friendly** |
| `GET /healthz` | Health check (no key required) |

The app authenticates to Application Insights (and Azure Resource Graph/ARM for enrichment)
with its **managed identity** — granted *Monitoring Reader* on the component and *Reader* on
the subscription. No secrets, no storage.

> **Optional blob persistence:** if you set `STORAGE_BLOB_ENDPOINT` on the app, it will also
> write a daily CSV to that container and expose `GET /api/reports` / `GET /api/reports/content`.
> This is off by default because it requires the app to reach the storage account (a private
> endpoint when the storage blocks public access), which adds cost and networking.

---

## What it produces

The report CSV columns:

| Column | Source |
|--------|--------|
| SubscriptionId, DeploymentName, Backend | App Insights `customMetrics` dimensions |
| ModelName, AccountName, CogSvcSubscriptionId, AccountId, SkuName, SkuCapacity | Resource Graph + ARM inventory |
| PromptTokens, CompletionTokens, CachedTokens, TotalTokens, Calls | App Insights token metrics |

> `TotalTokens = PromptTokens + CompletionTokens`. Reasoning tokens are already included in
> completion tokens and are intentionally not reported separately.

---

## How it works

1. **Query Application Insights** (`customMetrics`) for the report window using Azure Monitor Query.
2. **Build inventory** of Cognitive Services deployments via Azure Resource Graph + the ARM REST API.
3. **Left-join** token rows to inventory on `Backend + DeploymentName`.
4. **Render CSV** and **upload to Blob Storage**.

All Azure calls authenticate with the App Service **system-assigned managed identity** (no secrets).

---

## Prerequisites

- An existing **Application Insights** component already receiving APIM token metrics
  (`Prompt Tokens`, `Completion Tokens`, `Prompt Cached Tokens`, `Total Tokens` with
  `Deployment`, `Subscription ID`, `Backend` dimensions).
- **Azure CLI** logged in: `az login`
- **.NET SDK 9+**
- Permission to create resources and assign roles in the target subscription.

---

## Deploy

You can deploy two ways: a **point-and-click web GUI** (recommended) or the **PowerShell script** directly.

### Option A — Deployment portal (GUI)

A local web app that walks you through: **Az Login → Subscription → Resource group → Configure → Deploy**.
It signs you in with the Azure CLI, lets you pick/create a resource group, choose your Application
Insights component, and then provisions everything and sets the Entra/RBAC permissions — with live logs.

```powershell
cd gui
./run.ps1
```

This builds and starts the portal, then opens `http://127.0.0.1:5099`. Steps in the browser:

1. **Sign in** — browser or device-code login (via `az login`).
2. **Subscription** — choose the target subscription.
3. **Resource group** — pick an existing one or create a new one.
4. **Configure & deploy** — select the Application Insights component, tweak options, and click
   **Deploy to Azure**. Under the hood the portal runs [deploy.ps1](./deploy/deploy.ps1) and streams its output.

Requirements: **.NET SDK 9+** and **Azure CLI**.

### Option B — PowerShell script directly

```powershell
cd deploy
./deploy.ps1 `
    -AppInsightsResourceId "/subscriptions/<sub>/resourceGroups/<rg>/providers/microsoft.insights/components/<appi>" `
    -Location "eastus" `
    -RunNow
```

The script will:

1. Create a resource group, storage account, and `token-reports` container.
2. Create a Windows App Service plan (`B1` by default) + Web App with a managed identity.
3. Grant the identity least-privilege roles:
   - **Monitoring Reader** on the Application Insights component
   - **Reader** on the subscription (Resource Graph + list AI deployments)
   - **Storage Blob Data Contributor** on the report storage account
4. Publish the worker and deploy it as the **`TokenUsageReport`** triggered WebJob.
5. With `-RunNow`, trigger it once so you get a CSV immediately.

### Useful parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-AppInsightsResourceId` | *(required)* | App Insights component to read metrics from |
| `-Location` | `eastus` | Azure region |
| `-SubscriptionId` | current | Target subscription |
| `-BaseName` | `tokrpt` | Prefix for generated resource names |
| `-ResourceGroup` | `<BaseName>-rg` | Resource group name |
| `-PlanSku` | `B1` | App Service plan SKU (Basic+ required for scheduled WebJobs) |
| `-ReportContainer` | `token-reports` | Blob container for CSVs |
| `-Schedule` | `0 0 6 * * *` | WebJob CRON (6-field: sec min hour day month day-of-week) |
| `-LookbackHours` | `24` | Query window size |
| `-EndOffsetHours` | `0` | Hours subtracted from UTC midnight to end the window |
| `-RunNow` | off | Trigger the WebJob once after deploy |

---

## Application settings

Set automatically by the deploy script; override on the App Service if needed:

| Setting | Description |
|---------|-------------|
| `APPINSIGHTS_RESOURCE_ID` | ARM id of the App Insights component |
| `STORAGE_BLOB_ENDPOINT` | Blob endpoint, e.g. `https://acct.blob.core.windows.net/` |
| `REPORT_CONTAINER` | Container for CSVs (default `token-reports`) |
| `REPORT_LOOKBACK_HOURS` | Window size in hours (default `24`) |
| `REPORT_END_OFFSET_HOURS` | Hours before UTC midnight to end the window (default `0`) |
| `REPORT_DATE` | Optional explicit report date `yyyy-MM-dd` (backfill / manual runs) |

---

## Run locally

```powershell
cd src/TokenUsageReport
$env:APPINSIGHTS_RESOURCE_ID = "/subscriptions/.../components/<appi>"
$env:STORAGE_BLOB_ENDPOINT   = "https://<acct>.blob.core.windows.net/"
dotnet run
```

Locally it authenticates with your `az login` / Visual Studio credentials, so that identity
needs the same three roles listed above.

---

## Connect Power BI

Use the **Azure Blob Storage** connector, point it at the `token-reports` container, and
combine the daily CSVs. Because each file is dated, incremental refresh works naturally.
