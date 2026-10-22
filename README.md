# Token Usage Report

A **minimal .NET App Service web app** that pulls **Azure OpenAI / AI Foundry token usage**
from Application Insights (populated by API Management token metrics), enriches it with
Cognitive Services model/account/SKU metadata, and **serves the report as CSV through a secured
API** — ready to consume in a portal or **Power BI**.

It is deliberately simple and cheap: **no storage account, no private endpoint, no VNet.**
The app generates the report **on demand** from Application Insights (which already retains
the raw token telemetry) and returns it.

Contents of this repo:

- [src/TokenUsageReport](./src/TokenUsageReport) — the .NET 9 web app (secured reports API).
- [deploy/deploy.ps1](./deploy/deploy.ps1) — one PowerShell script that provisions everything and deploys the app.
- [gui/](./gui) — a local **Deployment Portal** (web GUI) for point-and-click sign-in, deployment and report viewing.

---

## Table of contents

1. [Architecture](#architecture)
2. [Prerequisites](#prerequisites)
3. [Installation (step by step)](#installation-step-by-step)
4. [Verify the deployment](#verify-the-deployment)
5. [Using the API](#using-the-api)
6. [Connect Power BI](#connect-power-bi)
7. [Report columns](#report-columns)
8. [Networking & storage](#networking--storage-read-before-a-production-rollout)
9. [Application settings reference](#application-settings-reference)
10. [Run locally](#run-locally)
11. [Update / redeploy / uninstall](#update--redeploy--uninstall)
12. [Troubleshooting](#troubleshooting)

---

## Architecture

```
APIM (token metrics) ──► Application Insights (customMetrics)
                                   │  Azure Monitor Query
                                   ▼
Client / Power BI ──X-Api-Key──► App Service (.NET 9, managed identity) ──► Azure Resource Graph + ARM
                                   │                                         (model / account / SKU inventory)
                                   ▼
                                CSV / JSON report
```

1. **Query Application Insights** (`customMetrics`) for the report window.
2. **Build an inventory** of Cognitive Services deployments via Azure Resource Graph + ARM.
3. **Left-join** token rows to inventory on `Backend + DeploymentName`.
4. **Render CSV/JSON** and return it from the secured API.

All Azure calls use the App Service **system-assigned managed identity** (no secrets). The
identity is granted **Monitoring Reader** on the Application Insights component and **Reader**
on the subscription.

---

## Prerequisites

### Azure

| Requirement | Details |
|-------------|---------|
| Azure subscription | You need **Owner**, or **Contributor + User Access Administrator**, on the target subscription/resource group (the deploy creates resources **and assigns roles**). |
| Application Insights component | Must already exist and be receiving APIM token metrics: `Prompt Tokens`, `Completion Tokens`, `Prompt Cached Tokens`, `Total Tokens`, with dimensions `Deployment`, `Subscription ID`, `Backend`. |
| Network | Outbound internet access to `*.azure.com`, `*.azurewebsites.net`, `login.microsoftonline.com`. |

### Tools on the machine you deploy from (Windows)

| Tool | Version | Install |
|------|---------|---------|
| **.NET SDK** | 9.0 or later | `winget install Microsoft.DotNet.SDK.9` |
| **Azure CLI** | latest | `winget install Microsoft.AzureCLI` |
| **PowerShell** | 7+ recommended (Windows PowerShell 5.1 works) | `winget install Microsoft.PowerShell` |
| **Git** | any | `winget install Git.Git` |

Open a **new** terminal after installing so the tools are on `PATH`, then verify:

```powershell
dotnet --version     # 9.x or higher
az --version
git --version
```

---

## Installation (step by step)

### Step 1 — Get the code

The repo is public, so no GitHub access is needed.

```powershell
git clone https://github.com/Pandey-Vikas/apim-token-usage-report.git
cd apim-token-usage-report
```

### Step 2 — Sign in to Azure

```powershell
az login
az account set --subscription "<subscription name or id>"
az account show --query "{name:name, id:id}" -o table
```

### Step 3 — Find your Application Insights resource ID

```powershell
az extension add --name application-insights   # first time only
az monitor app-insights component show `
    --app <appi-name> --resource-group <appi-rg> --query id -o tsv
```

The ID looks like
`/subscriptions/<sub>/resourceGroups/<rg>/providers/microsoft.insights/components/<appi>`.

### Step 4 — Deploy (choose one option)

#### Option A — Deployment Portal (GUI, recommended)

```powershell
cd gui
./run.ps1
```

This builds and starts the portal and opens **http://127.0.0.1:5099**. In the browser:

1. **Sign in** — browser or device-code login (via `az login`).
2. **Tenant** — after sign-in, a **Tenant (directory)** dropdown lists every tenant you can access (name, domain, ID). To use a different one, pick it and click **Switch tenant** (a sign-in window opens for that tenant). A tenant that is not listed can be entered by ID or domain via **Other…**. **Next** is enabled once the active tenant matches your selection.
3. **Subscription** — only subscriptions from the selected tenant are listed.
4. **Resource group** — pick an existing one or create a new one.
5. **Configure & deploy** — select the Application Insights component, adjust options, click
   **Deploy to Azure**. The portal runs [deploy.ps1](./deploy/deploy.ps1) and streams live logs.

If port 5099 is busy: `./run.ps1 -Port 5100`.

#### Option B — PowerShell script

```powershell
cd deploy
./deploy.ps1 `
    -AppInsightsResourceId "/subscriptions/<sub>/resourceGroups/<rg>/providers/microsoft.insights/components/<appi>" `
    -Location "eastus" `
    -RunNow
```

If script execution is blocked: `Set-ExecutionPolicy -Scope Process Bypass`.

The script is **idempotent** (safe to re-run or resume after a failure) and will:

1. Create/reuse the resource group.
2. Create/reuse a **Windows** App Service plan (`B1` by default) and a Web App with a system-assigned identity.
3. Grant the identity **Monitoring Reader** on the App Insights component and **Reader** on the subscription (and, with `-GrantAllSubscriptions`, on all your other subscriptions); enable public query access on the component if it was disabled.
4. Publish the app and set a generated `REPORTS_API_KEY`.
5. With `-RunNow`, call the API once to prove the report works.

### Step 5 — Save the output

At the end the script prints (the portal shows the same):

```
App URL     : https://<app>.azurewebsites.net
Api Key     : <generated key>
Report API  : https://<app>.azurewebsites.net/api/report.csv?lookbackHours=24
```

**Copy the API key** and store it securely (e.g. a password manager). It can be re-read any time:

```powershell
az webapp config appsettings list -g <rg> -n <app> --query "[?name=='REPORTS_API_KEY'].value" -o tsv
```

> Role assignments can take a few minutes to propagate. If the first report call returns an
> authorization error, wait 2–5 minutes and retry.

### Deploy script parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-AppInsightsResourceId` | *(required)* | App Insights component to read metrics from |
| `-Location` | `eastus` | Azure region |
| `-SubscriptionId` | current `az` subscription | Target subscription |
| `-BaseName` | `tokrpt` | Prefix for generated resource names |
| `-ResourceGroup` | `<BaseName>-rg` | Resource group name |
| `-PlanName` / `-PlanResourceGroup` | generated | Reuse an existing **Windows** App Service plan |
| `-AppName` | generated | Web App name (must be globally unique) |
| `-PlanSku` | `B1` | App Service plan SKU (`F1` also works for on-demand use) |
| `-LookbackHours` | `24` | Default report window size |
| `-EndOffsetHours` | `0` | Hours subtracted from UTC midnight to end the window |
| `-RunNow` | off | Generate a report once after deploy |
| `-GrantAllSubscriptions` | off (ticked by default in the portal) | Also grant Reader on all other accessible subscriptions |

---

## Verify the deployment

### Step-by-step check using the portal UI (no commands needed)

The portal ([gui/](./gui)) is the easiest way to deploy **and** test. Start it with `cd gui; ./run.ps1`
(it opens http://127.0.0.1:5099).

| # | Where in the UI | What to do | Expected result |
|---|-----------------|------------|-----------------|
| 1 | Sign in to Azure | Choose browser or device-code login and sign in | Footer shows `Signed in as <your account>` |
| 2 | Tenant (directory) | Pick the tenant that holds your subscription; if it is not the current one, click **Switch tenant** and sign in | Dropdown shows `[current]` on your choice and **Next** is enabled |
| 3 | Choose a subscription | Select the subscription that holds your App Insights | Only subscriptions from the chosen tenant are listed |
| 4 | Resource group | Pick an existing group or create a new one (pick a region) | Group is selected |
| 5 | Configure & deploy | Select the Application Insights component, then click **Deploy to Azure** | Live log streams; ends with the **App URL** and **API key** |
| 6 | Welcome back / **View reports →** | Open the report viewer (shown automatically on later launches) | Viewer shows App, App URL and Resource group |
| 7 | **Show sample report** | Click it | A sample table appears — confirms the viewer works |
| 8 | **Run report now** | Pick a quick range or start/end dates (UTC) and click it (can take up to a minute) | Table of token usage, or a message *"no token metrics were found"* if APIM has not emitted data in that window |
| 9 | Report table | Check PromptTokens, CompletionTokens, CachedTokens, TotalTokens, Calls | Totals match the APIM traffic for that period; the file also appears in **Generated reports** and can be downloaded |

> **Quick test of the UI itself:** if step 8 says no data, widen the date range (e.g. the last 30 days).
> Role assignments can take a few minutes after the first deploy — retry if you see an authorization error.

### Step-by-step check using commands

```powershell
$url = "https://<app>.azurewebsites.net"
$key = "<api key>"

Invoke-RestMethod "$url/healthz"                                    # no key required
(Invoke-WebRequest "$url/api/report.csv?lookbackHours=24" -Headers @{ 'X-Api-Key' = $key }).Content
```

You should see CSV rows with token counts per deployment. An empty report means no token
metrics exist in that window — check APIM is emitting them into the same App Insights.

---

## Using the API

Every `/api/*` call requires an **`X-Api-Key`** header.

| Endpoint | Purpose |
|----------|---------|
| `POST /api/run` | Generate the default-window report and return the CSV (JSON) |
| `GET /api/report?startDate=2026-10-01&endDate=2026-10-06` | Inclusive UTC date range (JSON) |
| `GET /api/report.csv?lookbackHours=N` | Lookback window as raw `text/csv` — **Power BI friendly** |
| `GET /healthz` | Health check (no key required) |

```powershell
curl.exe -H "X-Api-Key: <key>" "https://<app>.azurewebsites.net/api/report.csv?lookbackHours=168" -o report.csv
```

---

## Connect Power BI

1. **Get Data → Web → Advanced**.
2. URL: `https://<app>.azurewebsites.net/api/report.csv?lookbackHours=168`
3. Add an HTTP request header: `X-Api-Key` = `<your key>`.
4. Authentication: **Anonymous** (the key is passed via the header).
5. Load, then schedule refresh in the Power BI service.

---

## Report columns

| Column | Source |
|--------|--------|
| SubscriptionId, DeploymentName, Backend | App Insights `customMetrics` dimensions |
| ModelName, AccountName, CogSvcSubscriptionId, AccountId, SkuName, SkuCapacity | Resource Graph + ARM inventory |
| PromptTokens, CompletionTokens, CachedTokens, TotalTokens, Calls | App Insights token metrics |

> `TotalTokens = PromptTokens + CompletionTokens`. Reasoning tokens are already included in
> completion tokens and are not reported separately.

---

## Networking & storage (read before a production rollout)

**Default mode needs no storage account and no private networking.** Check these points if something does not work:

| Area | What is required | Symptom if missing |
|------|------------------|--------------------|
| Inbound to the app | App must be reachable over HTTPS from the caller (portal machine / Power BI service). Do not enable *private-only* access or IP restrictions that exclude them. | Timeouts / 403 from `*.azurewebsites.net` |
| Outbound from the app | `login.microsoftonline.com`, `management.azure.com`, `api.applicationinsights.io` / `api.loganalytics.io` over 443. | Report call returns 500 with a connection error |
| Application Insights network isolation | The deploy **automatically switches on public query access** for the component if it was disabled (POC convenience). If it is behind a private link scope (AMPLS) that still blocks the app, add VNet integration + a private endpoint to the AMPLS. | 403 / "public network access disabled" from the Monitor query |
| Reader on other subscriptions | In the portal, leave **"Grant read access on all my subscriptions"** ticked (default) — or pass `-GrantAllSubscriptions` to `deploy.ps1`. The deploy then grants Reader on every subscription you can access, so no manual roles are needed. Without it, model/account/SKU columns for accounts in other subscriptions stay blank (token numbers are still correct). | Blank ModelName / AccountName / SkuName columns |
| Local portal | Runs only on `127.0.0.1`; foreign Host/Origin requests are rejected. Run it on the same machine as your browser. | `403 Forbidden: portal is local-only` when browsed via another hostname |

### Optional blob persistence (off by default)

Only enable this if you need a stored history of daily CSVs. The deploy script does **not** create storage or assign storage roles, so do it explicitly:

1. Create a storage account + container (`token-reports`).
2. Grant the app's managed identity **Storage Blob Data Contributor** on the storage account (RBAC can take a few minutes).
3. Set `STORAGE_BLOB_ENDPOINT=https://<account>.blob.core.windows.net/` (and optionally `REPORT_CONTAINER`) on the App Service.
4. If the storage account blocks public access, add **VNet integration** to the app and a **private endpoint** (`blob`) with private DNS (`privatelink.blob.core.windows.net`) so the app can reach it.

When enabled the app writes a daily CSV at 06:00 UTC (`REPORT_SCHEDULE_HOUR_UTC`) and on `POST /api/run`, and lists them via `GET /api/reports`. A blob failure
never loses a report: the CSV is still returned and the error is written to the app log (Log stream).

---

## Application settings reference

Set automatically by the deploy script; override on the App Service (Settings → Environment variables) if needed:

| Setting | Description |
|---------|-------------|
| `APPINSIGHTS_RESOURCE_ID` | ARM id of the App Insights component (required) |
| `REPORTS_API_KEY` | API key required in the `X-Api-Key` header |
| `REPORT_LOOKBACK_HOURS` | Default window size in hours (default `24`) |
| `REPORT_END_OFFSET_HOURS` | Hours before UTC midnight to end the window (default `0`) |
| `REPORT_DATE` | Optional explicit report date `yyyy-MM-dd` (backfill / manual runs) |
| `STORAGE_BLOB_ENDPOINT`, `REPORT_CONTAINER` | *Optional.* If set, the app also writes a daily CSV to Blob Storage (off by default; requires the app to reach the storage account) |

To rotate the API key:

```powershell
az webapp config appsettings set -g <rg> -n <app> --settings REPORTS_API_KEY=<new-key>
```

---

## Run locally

```powershell
cd src/TokenUsageReport
$env:APPINSIGHTS_RESOURCE_ID = "/subscriptions/.../components/<appi>"
$env:REPORTS_API_KEY         = "local-test-key"
dotnet run
```

Locally it authenticates with your `az login` / Visual Studio credentials, so your identity needs
**Monitoring Reader** on the App Insights component and **Reader** on the subscription.

---

## Update / redeploy / uninstall

- **Update:** `git pull`, then re-run `deploy.ps1` (or the portal) with the same parameters. The existing API key is kept.
- **Uninstall:** delete the resource group created by the deploy (only if it is dedicated to this app):

  ```powershell
  az group delete --name <rg> --yes --no-wait
  ```

  If you reused a shared resource group, delete just the Web App (and the plan, if created by this deploy),
  and remove the role assignments of its managed identity.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `az` / `dotnet` not recognized | Install the tools above and open a **new** terminal. |
| Script blocked by execution policy | `Set-ExecutionPolicy -Scope Process Bypass` |
| Role assignment fails (`AuthorizationFailed`) | You need Owner or User Access Administrator on the scope. |
| `401/403` from App Insights or Resource Graph right after deploy | Role propagation delay — wait a few minutes and retry. |
| `401 Unauthorized` from the API | Missing/incorrect `X-Api-Key` header. |
| `503 REPORTS_API_KEY is not configured` | The API fails closed without a key. Set `REPORTS_API_KEY` on the app (the deploy script does this automatically). |
| App Service plan "is a LINUX plan" error | This app needs a **Windows** plan; use a different `-PlanName` or delete the Linux plan. |
| App name already taken | Pass a unique `-AppName` (or a different `-BaseName`). |
| Empty report | No token metrics in the window; widen `lookbackHours` and confirm APIM emits metrics to this App Insights. |
| Wrong tenant / subscription missing in the list | On step 1 choose the right tenant from **Tenant (directory)** and click **Switch tenant** (or `az login --tenant <id>` for the script). Subscriptions are filtered to the active tenant. |
| Portal port in use | `./run.ps1 -Port 5100` |
| First request slow | Cold start; `B1` with Always On keeps the app warm. |
