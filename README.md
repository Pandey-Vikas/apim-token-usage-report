# Token Usage Report

A **minimal .NET App Service web app** that pulls **Azure OpenAI / AI Foundry token usage**
from Application Insights (API Management token metrics or Foundry / GenAI telemetry), enriches it with
Cognitive Services model/account/SKU metadata, and **serves the report as CSV through a secured
API** — ready to consume in a portal or **Power BI**.

It is deliberately simple and cheap: **no storage account, no private endpoint, no VNet.**
The app generates the report **on demand** from Application Insights (which already retains
the raw token telemetry) and returns it.

## Quick start (everything is a script)

All tasks are PowerShell scripts in this repository; nothing needs to be done by hand or through an assistant.
Run them from the repository root in Windows PowerShell 5.1 or PowerShell 7.

| Task | Command |
|------|---------|
| 1. Check tools and sign-in | `.\scripts\check-prerequisites.ps1` |
| 2. Sign in to Azure | `az login` (add `--tenant <tenant-id>` for a specific tenant) |
| 3. Run all tests and builds | `.\scripts\run-tests.ps1` |
| 4. Open the local report dashboard / install wizard | `.\scripts\start-portal.ps1` (opens http://127.0.0.1:5099) |
| 5. Deploy to Azure (no GUI) | `.\scripts\deploy-foundry-report.ps1 -SubscriptionIds <sub1>,<sub2> -ResourceGroup <rg> -Location <region>` |
| 6. Verify the deployment | `.\scripts\verify-deployment.ps1 -ResourceGroup <rg> -AppName <app>` |
| 7. Run the report API on your machine | `.\scripts\run-local.ps1 -SubscriptionIds <sub1>` |
| 8. Remove the deployment | `.\scripts\uninstall.ps1 -ResourceGroup <rg> -AppName <app> [-PlanName <plan>]` |

Typical first run:

```powershell
git clone https://github.com/Pandey-Vikas/apim-token-usage-report.git
cd apim-token-usage-report
.\scripts\check-prerequisites.ps1            # tools + which subscriptions you can see
az login                                      # if the check says you are not signed in
.\scripts\run-tests.ps1                       # optional: 68 .NET + 34 portal tests
.\scripts\start-portal.ps1                    # look at the live dashboard from your own sign-in (nothing is deployed)
.\scripts\deploy-foundry-report.ps1 -SubscriptionIds <sub-id> -ResourceGroup rg_tokenrpt -Location swedencentral
.\scripts\verify-deployment.ps1 -ResourceGroup rg_tokenrpt -AppName <app name printed by the deploy>
```

What `deploy-foundry-report.ps1` does (it wraps [deploy/deploy.ps1](./deploy/deploy.ps1), which holds all the Azure logic):
checks the sign-in, discovers the Application Insights components in the selected subscriptions (Azure Resource Graph), creates or
reuses the resource group, App Service plan and web app, enables the managed identity, grants it **Reader** on the subscriptions and
**Monitoring Reader** on those components, publishes the app, sets the API key (kept on re-runs) and prints the URL and key.
Re-running it **updates** the same app. Useful options: `-AppName` / `-PlanName` / `-PlanSku F1|B1|B2|S1` to reuse or size the hosting,
`-AppInsightsIds` to restrict access to specific components, `-IncludeFutureProjects` (Monitoring Reader on the whole subscriptions so new
projects are included), `-ListIdleAgents` (opt-in *Foundry User* role so agents with no activity are listed; it can also read account keys,
so it is off by default) and `-RunNow` (run one report at the end). Without `-ListIdleAgents` the app logs a harmless HTTP 403 warning
per project, and agents appear once they have usage.

Permissions needed to deploy: Owner (or Contributor + User Access Administrator) on the hosting resource group and on the monitored subscriptions.

---

## Hierarchical Foundry report (default)

The report is grouped **Tenant → Subscriptions → Resource groups → Foundry resources (and their linked
Application Insights) → Projects → Agents → Models**, with Daily / Weekly / Monthly views.

**Install wizard (local Deployment Portal)** — five steps:

1. **Sign in** – shows the Azure CLI account and tenant.
2. **Subscriptions** – select one *or more* subscriptions.
3. **Detect Foundry** – scans every resource group of the selected subscriptions for Foundry resources and projects,
   follows each project's *Application Insights* connection, and lists deployed models and agents. Projects with no
   linked Application Insights are flagged because their usage cannot be reported.
4. **Hosting** – resource group, App Service plan and web app (existing or new).
5. **Deploy** – grants the app's managed identity **Reader** on the selected subscriptions and **Monitoring Reader** on
   each detected Application Insights (or on the whole subscriptions if you tick *include projects created later*).
   Listing agents that have no activity is opt-in (*Foundry User* role on each project) because that role can also
   list account keys and connection secrets.

**Dashboard** – a Power BI-style report, top to bottom: **Scope** → **Date range** (with the *Quick range* buttons on the same line) → **Filters**
(subscription, resource group, project, agent, model, plus **Clear filters**) → **Token breakdown** cards → a multi-line **chart** → **Top 5 agents** and
**Top 5 models** → the **Detailed report** (expandable Tenant › Subscription › RG › Project › Agent › Model matrix with Azure portal links and
"Break down by period"), then a by-period table. Detail CSV and grouped CSV export are built in.

- **Group by (Day / Week / Month):** the time bucket used by the chart, the "By period" table, the peak card, "Break down by period" in the
  detailed report and the detail CSV export. Weeks run Monday to Sunday and months are calendar months, both in UTC. It only changes how
  usage is bucketed over time; totals, cost and the per-agent numbers are the same whichever you choose.
- **Light / dark:** the header button switches the report between a light (Power BI) and a dark theme; the choice is remembered in the browser.
- **Token breakdown:** total, input (prompt), output (completion), cached input and uncached input are always shown; *reasoning* and *cache write* tokens appear
  only when your telemetry reports them (Foundry traces currently carry input, output and cached tokens only). Each card has its own colour and toggles that line
  on the chart. The chart has Day/Week/Month grain, a legend, and a hover read-out of every line.
- **Top 5:** clicking an agent or model filters the whole dashboard (cards, chart, matrix); click again or use *Clear filters* to remove it.
- **Latency:** *Avg model latency* is the average time of one model (LLM) request. *Avg agent run* is the average end-to-end time of a whole agent run
  (`invoke_agent` span: all model calls, tool calls and waits), so it is usually longer. Each run's time is counted once, on its first model call.
- **Live refresh:** tick *Live refresh every* and choose a number of **minutes or hours** (1 minute to 24 hours, default 5 minutes, remembered in the browser).
  Every refresh re-scans Azure and queries every linked Application Insights, so use a longer interval to reduce load.

Cost uses the cost reported in telemetry when present;
otherwise it is a **list-price estimate** from the Azure Retail Prices API (shown as `n/a` when no price exists, e.g. model-router,
gpt-5 and gpt-4.1 at the time of writing). Override prices with `REPORT_PRICE_OVERRIDES`
(`{"model":{"input":x,"cachedInput":y,"output":z}}`, USD per 1M tokens). The data source can be *this machine* (your
Azure CLI identity) or the deployed app.

Each row carries Tenant, Subscription, Resource group, Foundry resource, Project, Application Insights, Agent, Model,
Deployment, input/output/cached/uncached/(reasoning, cache-write) tokens, total, cache-hit %, tokens per call, calls, errors,
average **model-call** latency, agent runs, average **agent-run** latency and cost.

API (deployed app, `X-Api-Key` required): `GET /api/usage` (JSON) and `GET /api/usage.csv?granularity=day|week|month`,
both taking `lookbackHours` or `startDate`/`endDate`. Weeks start on Monday (UTC).

The sections below that mention the query editor, *Diagnose* and `/api/report` describe the **legacy single-Application-Insights
mode**, which is only active when `APPINSIGHTS_RESOURCE_ID` is set on the app. The portal's query editor and Diagnose were removed.

Contents of this repo:

- [scripts/](./scripts) — one script per task (check prerequisites, run tests, start the portal, deploy, verify, run locally, uninstall).
- [src/TokenUsageReport](./src/TokenUsageReport) — the .NET 9 web app (secured reports API).
- [deploy/deploy.ps1](./deploy/deploy.ps1) — one PowerShell script that provisions everything and deploys the app.
- [gui/](./gui) — a local **Deployment Portal** (web GUI) for point-and-click sign-in, deployment and report viewing.

---

## Table of contents

0. [Quick start (scripts)](#quick-start-everything-is-a-script)
1. [Architecture](#architecture)
2. [Prerequisites](#prerequisites)
3. [Installation (step by step)](#installation-step-by-step)
4. [Verify the deployment](#verify-the-deployment)
5. [Using the API](#using-the-api)
6. [Connect Power BI](#connect-power-bi)
7. [Report columns](#report-columns)
8. [Networking & storage](#networking--storage-read-before-a-production-rollout)
9. [Choosing the data source and query](#choosing-the-data-source-and-query)
10. [Application settings reference](#application-settings-reference)
11. [Run locally](#run-locally)
12. [Update / redeploy / uninstall](#update--redeploy--uninstall)
13. [Troubleshooting](#troubleshooting)

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

1. **Query Application Insights** for the report window. The default checks APIM metrics, then Foundry usage spans, then GenAI token metrics until a source returns rows.
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
| Application Insights component | Use the component receiving your Foundry / OpenAI telemetry or APIM token metrics. Foundry telemetry must include usage token attributes, not just trace messages. APIM emits `Prompt Tokens`, `Completion Tokens`, `Prompt Cached Tokens`, `Total Tokens` with dimensions `Deployment`, `Subscription ID`, `Backend`. A newly created component is empty until instrumentation writes to it. |
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

The **Location** is the hosting region, not a change to an existing Application Insights component.
For a new plan it defaults to the selected component's region (or the resource group's region).
For an existing plan it is fixed to that plan's actual region. The component picker shows its own
region separately. Selecting Central India no longer falls through to the first alphabetical region.

If port 5099 is busy: `./run.ps1 -Port 5100`.

#### Option B — PowerShell script

```powershell
cd deploy
./deploy.ps1 `
    -AppInsightsResourceId "/subscriptions/<sub>/resourceGroups/<rg>/providers/microsoft.insights/components/<appi>" `
    -Location "eastus" `
    -RunNow
```

If you have no Application Insights yet, replace `-AppInsightsResourceId ...` with `-NewAppInsightsName "tokrpt-appi"` to create one.

If script execution is blocked: `Set-ExecutionPolicy -Scope Process Bypass`.

The script is **idempotent** (safe to re-run or resume after a failure) and will:

1. Create/reuse the resource group and register the required resource providers.
2. Create/reuse the Application Insights component (+ Log Analytics workspace) when `-NewAppInsightsName` is given.
3. Create/reuse a **Windows** App Service plan (`B1` by default) and a Web App with a system-assigned identity.
4. Grant the identity **Monitoring Reader** on the App Insights component and **Reader** on the subscription (and, with `-GrantAllSubscriptions`, on your other subscriptions **in the same tenant**); enable public query access on the component if it was disabled.
5. Publish the app and set a generated `REPORTS_API_KEY`.
6. With `-RunNow`, call the API once to prove the report works.

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
| `-AppInsightsResourceId` | *(one of these two is required)* | Existing App Insights component to read metrics from |
| `-NewAppInsightsName` | – | Create a new App Insights component (+ Log Analytics workspace) with this name instead |
| `-Location` | `eastus` | Azure region |
| `-SubscriptionId` | current `az` subscription | Target subscription |
| `-BaseName` | `tokrpt` | Name prefix — only used to auto-name resources you don't name yourself (`<BaseName>-plan`, `<BaseName>-app-xxxxxx`, default resource group `<BaseName>-rg`). Called **Name prefix** in the portal |
| `-ResourceGroup` | `<BaseName>-rg` | Resource group name |
| `-PlanName` / `-PlanResourceGroup` | generated | Reuse an existing **Windows** App Service plan |
| `-AppName` | generated | Web App name (must be globally unique) |
| `-PlanSku` | `B1` | App Service plan SKU (`F1` also works for on-demand use) |
| `-LookbackHours` | `24` | Default report window size |
| `-EndOffsetHours` | `0` | Hours before *now* at which the rolling report window ends (0 = up to the moment of the request) |
| `-RunNow` | off | Generate a report once after deploy |
| `-GrantAllSubscriptions` | off (ticked by default in the portal) | Also grant Reader on your other subscriptions in the **same tenant** (subscriptions in other tenants are skipped automatically) |
| `-ReportQueryFile` | *(bundled default)* | Path to a `.kql` file that replaces the report query (see [Choosing the data source and query](#choosing-the-data-source-and-query)). Omit to use — or reset to — the default |

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
| AgentId, AgentName | Foundry / GenAI span or metric attributes; blank when not emitted or unambiguously attributable |
| ReportedCost, CostCurrency | Cost and currency explicitly emitted in usage telemetry; blank when unavailable |
| ResourceId, PortalUrl | Foundry telemetry's `gen_ai.azure_ai_project.id` (the Foundry project), else the matched Cognitive Services account from the inventory. `PortalUrl` is an `https://portal.azure.com/#resource<id>/overview` deep link, shown in the viewer as **Open in Azure portal**. Blank when no valid ARM id is available |

> `TotalTokens = PromptTokens + CompletionTokens`. Reasoning tokens are already included in
> completion tokens and are not reported separately.

The four agent/cost columns are appended after the existing columns. Foundry rows are grouped by
agent, model/deployment, backend and reported currency. `Calls` counts usage spans, not distinct
agent invocations. Parent agent/tool totals are excluded when child model usage is available to
avoid counting both totals and model calls. Agent identity is taken from the span, its direct parent,
or a single unambiguous agent in the same trace. Ambiguous multi-agent traces keep blank identity
rather than assigning usage to the wrong agent.

**Costs are not inferred from tokens.** The Foundry template reads `gen_ai.usage.cost` or
`llm.cost.total` from custom dimensions/measurements and currency from
`gen_ai.usage.cost.currency` or `llm.cost.currency`. These are supported exporter fields, not
guaranteed Foundry attributes. A group has a cost only if every counted usage record includes one;
unknown currency remains blank. A cost estimate visible in the Foundry portal may be calculated
by the portal and not persisted in Application Insights. Reproducing such estimates requires
explicit model/deployment pricing and currency, including cached-input rates; this report does
not invent prices or claim to be an Azure billing export.

---

## Networking & storage (read before a production rollout)

**Default mode needs no storage account and no private networking.** Check these points if something does not work:

| Area | What is required | Symptom if missing |
|------|------------------|--------------------|
| Inbound to the app | App must be reachable over HTTPS from the caller (portal machine / Power BI service). Do not enable *private-only* access or IP restrictions that exclude them. | Timeouts / 403 from `*.azurewebsites.net` |
| Outbound from the app | `login.microsoftonline.com`, `management.azure.com`, `api.applicationinsights.io` / `api.loganalytics.io` over 443. | Report call returns 500 with a connection error |
| Application Insights network isolation | The deploy **automatically switches on public query access** for the component if it was disabled (POC convenience). If it is behind a private link scope (AMPLS) that still blocks the app, add VNet integration + a private endpoint to the AMPLS. | 403 / "public network access disabled" from the Monitor query |
| Reader on other subscriptions | In the portal, leave **"Grant read access on all my other subscriptions in this tenant"** ticked (default) — or pass `-GrantAllSubscriptions` to `deploy.ps1`. The deploy then grants Reader on every other subscription in the app's tenant where you are allowed to assign roles; the log lists any it skipped. **Subscriptions in other tenants can never be granted** (a managed identity belongs to a single tenant), so Azure OpenAI accounts there show blank model/account/SKU columns — token numbers are still correct. | Blank ModelName / AccountName / SkuName columns |
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

## Choosing the data source and query

**Where do the token numbers come from?** Application Insights only contains token data if something writes it there. There are two sources, and the report must read the one you use:

| Source | What writes the data | Which Application Insights | Query template |
|--------|----------------------|----------------------------|----------------|
| **APIM** | The APIM `llm-emit-token-metric` policy → `customMetrics` (`Prompt Tokens`, `Completion Tokens`, …) | The one set as the **APIM logger** of your APIM instance | *Automatic* (APIM first) |
| **Foundry / OpenAI tracing** | OpenTelemetry `gen_ai.*`, legacy `llm.usage.*` or OpenInference `llm.token_count.*` usage in `dependencies`, `requests`, `traces`, `customEvents`; or `gen_ai.client.token.usage` in `customMetrics` | The one **connected to your Foundry project** | *Foundry / GenAI telemetry (per agent)* or *GenAI token-usage metric*; also tried automatically when APIM has no rows |

Linking a Foundry project does **not** produce APIM metrics. The default now falls back to Foundry
and then GenAI metrics when APIM returns no rows. Sources are **not summed together**, to avoid
counting the same request through multiple telemetry paths. If both APIM and Foundry exist and
you want per-agent attribution, explicitly choose the **Foundry** template. A custom query is
always respected and is never automatically replaced.

**Recommended:** in the portal, choose **Use existing** and pick the component marked **★ APIM '…' logs here** — the portal detects which Application Insights each APIM instance logs to and preselects it. Use **Create new** only if you will then point your APIM logger (or Foundry connection) at the new component.

### Report window and "empty report" diagnosis

- To watch prompts sent from **Foundry or an external app**, open **View reports** and enable
  **Live token tracking**. It queries today's UTC usage immediately and again 30 seconds after
  each query finishes (no overlapping requests). Select an actual **agent / model** from the
  returned telemetry to filter the table and input/output/cached/total token summary. Unattributed
  LLM usage is labelled separately; an agent is never invented. Download CSV retains the full
  report, not just the displayed filter.
- For a local demo without redeploying, enable **Read Foundry telemetry directly with my Azure CLI
  sign-in** and select the Application Insights component actually connected to your Foundry
  project. This queries the bundled Foundry KQL over the selected UTC window using your CLI
  identity. It does not change the deployed app, assign roles, or generate sample usage. CSVs in
  this mode contain query columns without Cognitive Services inventory enrichment.
  **Diagnose** is disabled in direct mode because it inspects the deployed app's different settings.
- Live tracking uses the deployed app's read-only `/api/report` endpoint; polls do not upload blobs
  or fill browser report history. Disabling tracking, leaving the viewer, or loading saved/sample
  data stops polling. Failed refreshes retain the last successful values with an explicit error;
  successful empty results show **NO DATA** and retry. Use Diagnose manually for live-mode failures.
- **This is ingestion-based monitoring, not immediate response streaming.** A prompt must emit
  usage into the selected Application Insights first, which can take several minutes. The
  dashboard shows the last check time, not the time the prompt completed. For per-agent monitoring,
  deploy the Foundry / GenAI template rather than APIM-only telemetry. The local portal does not
  invoke Foundry agents or change their instrumentation.
- A date range that includes **today** ends at the **moment of the request** (UTC), not at midnight, so the newest telemetry is included. `lookbackHours` (API / Power BI) is a rolling window ending now. The viewer shows the exact window used. The viewer's default range is *Last 7 days*; use **Today (up to now)** for the latest hours.
- Application Insights can take a few minutes to ingest new telemetry, so very recent calls may not appear immediately.
- Empty results are shown as **NO DATA**, not a successful report, and the viewer automatically runs **Why no data? (Diagnose)**. The API returns `empty`, `querySource`, `warning` and the exact UTC window. Diagnostics inspect the deployed app's actual component and query, recognised usage across all four span/event tables, agent names and token/agent/cost field names. Schema errors, missing result tables and partial query failures are errors, not empty successes.
- This diagnostic uses your locally signed-in Azure CLI identity, which needs access to the client's subscription and Application Insights. It does not expose prompt or completion content. Review diagnostic output before sharing it.

### Editing the report query

Step 4 of the portal has an expanded **Report data source & query (KQL)** section:

1. Pick a template (or write your own) in the editor.
2. Click **Validate query** — it runs against your Application Insights (last 30 days), checks required columns, and flags zero rows as **NO DATA**. The unchanged automatic template validates the same source fallback sequence as the deployed app.
3. Click **Deploy to Azure** — the query is stored on the app (`REPORT_QUERY_B64`). Leaving the default template untouched keeps the bundled default.

Rules for a custom query:
- It must return the columns `SubscriptionId, DeploymentName, Backend, PromptTokens, CompletionTokens, CachedTokens, TotalTokens, Calls`. Missing required columns fail explicitly, even for zero rows.
- Optional columns: `AgentId, AgentName, ReportedCost, CostCurrency`. Missing agent identity/cost is retained as blank and reported in a warning, not silently turned into a zero cost.
- Do **not** add a time filter — the app applies the report date range.
- `Backend` + `DeploymentName` are used to join model/account/SKU details, so keep them as the OpenAI endpoint host (e.g. `myaccount.openai.azure.com`) and the deployment name.

From the command line use `./deploy.ps1 ... -ReportQueryFile .\my-query.kql`. The bundled templates live in [src/TokenUsageReport/Queries](./src/TokenUsageReport/Queries). Re-deploying without a custom query resets the app to the default.

---

## Application settings reference

Set automatically by the deploy script; override on the App Service (Settings → Environment variables) if needed:

| Setting | Description |
|---------|-------------|
| `REPORT_SUBSCRIPTIONS` | Comma-separated subscription ids to scan (hierarchical report) |
| `REPORT_TENANT_ID`, `REPORT_TENANT_NAME` | Tenant shown at the top of the hierarchy |
| `REPORT_PRICE_OVERRIDES` | Optional JSON of per-model USD prices per 1M tokens (see above) |
| `APPINSIGHTS_RESOURCE_ID` | *Legacy mode only.* ARM id of a single App Insights component; enables `/api/report*` |
| `REPORTS_API_KEY` | API key required in the `X-Api-Key` header |
| `REPORT_LOOKBACK_HOURS` | Default window size in hours (default `24`) |
| `REPORT_END_OFFSET_HOURS` | Hours before *now* at which a rolling (`lookbackHours`) window ends (default `0` = right now) |
| `REPORT_DATE` | Optional explicit report date `yyyy-MM-dd` (backfill / manual runs) |
| `REPORT_QUERY_B64` | Optional custom KQL, base64-encoded UTF-8. Unset = automatic APIM → Foundry → GenAI metric source selection. Set by the portal / `-ReportQueryFile` |
| `STORAGE_BLOB_ENDPOINT`, `REPORT_CONTAINER` | *Optional.* If set, the app also writes a daily CSV to Blob Storage (off by default; requires the app to reach the storage account) |

To rotate the API key:

```powershell
az webapp config appsettings set -g <rg> -n <app> --settings REPORTS_API_KEY=<new-key>
```

---

## Run locally

```powershell
.\scripts\run-tests.ps1                                       # builds + 68 .NET tests + 34 portal tests + script syntax
.\scripts\start-portal.ps1                                    # dashboard and install wizard (add -Port 5100 -NoBrowser if needed)
.\scripts\run-local.ps1 -SubscriptionIds <sub-id>             # the report API on http://127.0.0.1:5200 (header X-Api-Key: local-key)
```

The tests check KQL semantics against Application Insights table schemas, mocked Azure Monitor responses, CSV serialization,
pricing, discovery and the dashboard (filters, chart, top 5, live interval). They do not replace validating against real telemetry:
send a prompt to an agent, wait a few minutes for ingestion and open the dashboard.

Locally the app and portal authenticate with your `az login` identity, so it needs **Reader** on the subscriptions and
**Monitoring Reader** on the Application Insights components (and the *Foundry User* role if you want idle agents listed).

---

## Update / redeploy / uninstall

- **Update:** `git pull`, then re-run `.\scripts\deploy-foundry-report.ps1` with the same parameters (the API key is kept), and
  `.\scripts\verify-deployment.ps1`.
- **Uninstall:** `.\scripts\uninstall.ps1 -ResourceGroup <rg> -AppName <app> [-PlanName <plan>]` removes the web app (and plan);
  add `-DeleteResourceGroup` only when the group holds nothing else. It asks for confirmation unless `-Force` is given.

---

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `az` / `dotnet` not recognized | Install the tools above and open a **new** terminal. |
| Script blocked by execution policy | `Set-ExecutionPolicy -Scope Process Bypass` |
| Role assignment fails (`AuthorizationFailed`) | You need Owner or User Access Administrator on the scope. |
| `401/403` from App Insights or Resource Graph right after deploy | Role propagation delay — wait a few minutes and retry. |
| `InvalidAuthenticationTokenTenant` / "wrong issuer" | The subscription belongs to a different tenant than your active sign-in. Choose that tenant on the portal's sign-in step and click **Switch tenant** (or `az login --tenant <id>`). The deploy script now checks this up front and skips other-tenant subscriptions when granting roles. |
| `401 Unauthorized` from the API | Missing/incorrect `X-Api-Key` header. |
| `503 REPORTS_API_KEY is not configured` | The API fails closed without a key. Set `REPORTS_API_KEY` on the app (the deploy script does this automatically). |
| App Service plan "is a LINUX plan" error | This app needs a **Windows** plan; use a different `-PlanName` or delete the Linux plan. |
| App name already taken | Pass a unique `-AppName` (or a different `-BaseName`). |
| Empty report | No token metrics in the window; widen `lookbackHours` and confirm APIM emits metrics to this App Insights. |
| Wrong tenant / subscription missing in the list | On step 1 choose the right tenant from **Tenant (directory)** and click **Switch tenant** (or `az login --tenant <id>` for the script). Subscriptions are filtered to the active tenant. |
| Portal port in use | `./run.ps1 -Port 5100` |
| First request slow | Cold start; `B1` with Always On keeps the app warm. |
