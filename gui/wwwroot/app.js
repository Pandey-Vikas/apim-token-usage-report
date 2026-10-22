'use strict';

const $ = (id) => document.getElementById(id);
const api = {
  async get(path) { const r = await fetch(path); return r.json(); },
  async post(path, body) {
    const r = await fetch(path, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body || {}),
    });
    return r.json();
  },
};

let pollTimer = null;

// --- Step navigation -------------------------------------------------------
function showStep(n) {
  $('landing').classList.add('hidden');
  $('viewer').classList.add('hidden');
  $('stepsBar').classList.remove('hidden');
  for (let i = 1; i <= 4; i++) {
    $(`step${i}`).classList.toggle('hidden', i !== n);
    const dot = $(`stepDot${i}`);
    dot.classList.toggle('active', i === n);
    dot.classList.toggle('done', i < n);
  }
}

function showLanding() {
  $('landing').classList.remove('hidden');
  $('viewer').classList.add('hidden');
  $('stepsBar').classList.add('hidden');
  for (let i = 1; i <= 4; i++) $(`step${i}`).classList.add('hidden');
}

document.querySelectorAll('[data-back]').forEach((b) =>
  b.addEventListener('click', () => showStep(Number(b.dataset.back))));

// --- Step 1: prerequisites + login ----------------------------------------
async function checkPrereqs() {
  const p = await api.get('/api/prerequisites');
  const azcli = p.tools?.azcli;
  if (azcli?.installed) {
    $('prereq').className = 'status ok';
    $('prereq').textContent = `Azure CLI detected (${azcli.version || 'ok'}).`;
  } else {
    $('prereq').className = 'status err';
    $('prereq').innerHTML = 'Azure CLI not found. Install it from <a href="https://aka.ms/installazurecli" target="_blank">aka.ms/installazurecli</a>, then reload.';
  }
  if (!p.scriptFound) {
    $('prereq').className = 'status err';
    $('prereq').textContent = `Deploy script not found at ${p.scriptPath}.`;
  }
}

async function refreshStatus() {
  const s = await api.get('/api/status');
  if (s.signedIn && s.account) {
    onSignedIn(s.account);
  }
}

function onSignedIn(account) {
  window.signedIn = true;
  window.currentTenantId = account.tenantId || null;
  const name = account.user?.name || account.name;
  const tenant = account.tenantId ? ` · tenant ${account.tenantId}` : '';
  $('loginStatus').className = 'status ok';
  $('loginStatus').textContent = `Signed in: ${name}${tenant}`;
  $('whoami').textContent = (account.user?.name || account.name || '—') + (account.tenantId ? ` (tenant ${account.tenantId})` : '');
  loadTenants();
}

// --- Tenant picker ---------------------------------------------------------
const OTHER_TENANT = '__other__';

async function loadTenants() {
  $('tenantPicker').classList.remove('hidden');
  $('tenantManual').classList.add('hidden');
  $('btnSignedInNext').disabled = true;
  $('tenantSelect').innerHTML = '<option>Loading your tenants…</option>';
  const r = await api.get('/api/tenants');
  window.currentTenantId = r.currentTenantId || window.currentTenantId;
  const sel = $('tenantSelect');
  sel.innerHTML = '';
  (r.tenants || []).forEach((t) => {
    const o = document.createElement('option');
    o.value = t.tenantId;
    const label = t.displayName || t.defaultDomain || t.tenantId;
    o.textContent = `${label}${t.defaultDomain && t.displayName ? ` (${t.defaultDomain})` : ''} — ${t.tenantId}${t.current ? '  [current]' : ''}`;
    if (t.current) o.selected = true;
    sel.appendChild(o);
  });
  const other = document.createElement('option');
  other.value = OTHER_TENANT;
  other.textContent = 'Other… (enter a tenant ID or domain)';
  sel.appendChild(other);
  updateTenantUi();
}

function tenantTarget() {
  const v = $('tenantSelect').value;
  return v === OTHER_TENANT ? $('tenant').value.trim() : v;
}

function updateTenantUi() {
  if (!window.signedIn) { $('btnSignedInNext').disabled = true; return; }
  const other = $('tenantSelect').value === OTHER_TENANT;
  $('tenantManual').classList.toggle('hidden', !other);
  const target = tenantTarget();
  const needSwitch = !!target && target.toLowerCase() !== (window.currentTenantId || '').toLowerCase();
  $('btnSwitchTenant').disabled = !needSwitch;
  $('btnSignedInNext').disabled = needSwitch;
  $('tenantHint').innerHTML = needSwitch
    ? 'You picked a different tenant. Click <strong>Switch tenant</strong> to sign in to it, then continue.'
    : 'Pick the directory that contains the subscription you want to deploy to. If it differs from your current one, click <strong>Switch tenant</strong> — a sign-in window opens for that tenant.';
}

$('tenantSelect').addEventListener('change', updateTenantUi);
$('tenant').addEventListener('input', updateTenantUi);
$('btnSwitchTenant').addEventListener('click', () => {
  const target = tenantTarget();
  if (target) startLogin('browser', target);
});

async function startLogin(kind, tenantOverride) {
  clearInterval(pollTimer);
  const manualVisible = !$('tenantManual').classList.contains('hidden');
  const tenantId = tenantOverride || (manualVisible ? $('tenant').value.trim() : '') || null;
  $('btnSignedInNext').disabled = true;
  $('btnSwitchTenant').disabled = true;
  $('loginStatus').className = 'status info';
  $('loginStatus').innerHTML = '<span class="spinner"></span>Starting sign-in… a browser window will open.';

  const res = await api.post(kind === 'device' ? '/api/login-device-code' : '/api/login-start', { tenantId });
  if (res.error) {
    $('loginStatus').className = 'status err';
    $('loginStatus').textContent = res.error;
    return;
  }

  $('loginStatus').className = 'status info';
  $('loginStatus').innerHTML = '<span class="spinner"></span>Waiting for you to complete sign-in in the window that opened…';

  let opened = false;
  let codeShown = false;
  pollTimer = setInterval(async () => {
    const p = await api.get('/api/login-poll');

    if (p.state === 'success') {
      clearInterval(pollTimer);
      onSignedIn(p.account);
      return;
    }
    if (p.state === 'failed') {
      clearInterval(pollTimer);
      $('loginStatus').className = 'status err';
      $('loginStatus').textContent = p.error || 'Sign-in failed.';
      updateTenantUi();
      return;
    }

    // still pending — surface the device code or browser URL as soon as az emits it
    if (kind === 'device') {
      if (p.code && !codeShown) {
        codeShown = true;
        $('loginStatus').className = 'status info';
        $('loginStatus').innerHTML =
          `Go to <a href="${p.url}" target="_blank">${p.url}</a> and enter code:<br>` +
          `<span class="code-box">${p.code}</span>`;
      }
    } else if (p.url && !opened) {
      opened = true;
      window.open(p.url, '_blank');
      $('loginStatus').className = 'status info';
      $('loginStatus').innerHTML = '<span class="spinner"></span>Complete sign-in in the browser window…';
    }
  }, 1500);
}

$('btnLogin').addEventListener('click', () => startLogin('browser'));
$('btnDeviceCode').addEventListener('click', () => startLogin('device'));
$('btnSignedInNext').addEventListener('click', async () => { await loadSubscriptions(); showStep(2); });

// --- Step 2: subscriptions -------------------------------------------------
async function loadSubscriptions() {
  const tenantId = window.currentTenantId || '';
  const subs = await api.get(`/api/subscriptions?tenantId=${encodeURIComponent(tenantId)}`);
  const sel = $('subscription');
  sel.innerHTML = '';
  const list = Array.isArray(subs) ? subs : [];
  list.forEach((s) => {
    const opt = document.createElement('option');
    opt.value = s.id;
    opt.textContent = `${s.name} (${s.id})`;
    if (s.isDefault) opt.selected = true;
    sel.appendChild(opt);
  });
  const tName = $('tenantSelect').selectedOptions[0]?.textContent.replace(/\s*\[current\]$/, '') || tenantId;
  $('subSub').textContent = list.length
    ? `Showing ${list.length} subscription(s) in tenant ${tName}. Resources will be created in the one you select.`
    : `No enabled subscriptions found in tenant ${tName} for your account. Go back and switch to a different tenant.`;
  $('btnSubNext').disabled = list.length === 0;
}

$('btnSubNext').addEventListener('click', async () => {
  const subscriptionId = $('subscription').value;
  $('subStatus').className = 'status info';
  $('subStatus').innerHTML = '<span class="spinner"></span>Selecting subscription…';
  const r = await api.post('/api/subscription', { subscriptionId });
  if (r.error) {
    $('subStatus').className = 'status err';
    $('subStatus').textContent = r.error;
    return;
  }
  $('subStatus').textContent = '';
  await Promise.all([loadResourceGroups(), loadLocations()]);
  showStep(3);
});

// --- Step 3: resource group ------------------------------------------------
async function loadResourceGroups() {
  const groups = await api.get('/api/resource-groups');
  const sel = $('resourceGroup');
  sel.innerHTML = '';
  (Array.isArray(groups) ? groups : []).forEach((g) => {
    const opt = document.createElement('option');
    opt.value = g.name;
    opt.textContent = `${g.name} (${g.location})`;
    sel.appendChild(opt);
  });
}

async function loadLocations() {
  const locs = await api.get('/api/locations');
  ['rgLocation', 'location'].forEach((id) => {
    const sel = $(id);
    sel.innerHTML = '';
    (Array.isArray(locs) ? locs : []).forEach((l) => {
      const opt = document.createElement('option');
      opt.value = l.name;
      opt.textContent = l.displayName || l.name;
      sel.appendChild(opt);
    });
  });
}

function rgMode() {
  return document.querySelector('input[name="rgMode"]:checked').value;
}
document.querySelectorAll('input[name="rgMode"]').forEach((r) =>
  r.addEventListener('change', () => {
    const isNew = rgMode() === 'new';
    $('rgNewFields').classList.toggle('hidden', !isNew);
    $('resourceGroup').disabled = isNew;
  }));

$('btnRgNext').addEventListener('click', async () => {
  if (rgMode() === 'new') {
    const name = $('rgNewName').value.trim();
    const location = $('rgLocation').value;
    if (!name) { $('rgStatus').className = 'status err'; $('rgStatus').textContent = 'Enter a resource group name.'; return; }
    $('rgStatus').className = 'status info';
    $('rgStatus').innerHTML = '<span class="spinner"></span>Creating resource group…';
    const r = await api.post('/api/create-resource-group', { name, location });
    if (r.error) { $('rgStatus').className = 'status err'; $('rgStatus').textContent = r.error; return; }
    window.selectedRg = name;
    // default the deploy location to the RG location
    $('location').value = location;
  } else {
    window.selectedRg = $('resourceGroup').value;
  }
  $('rgStatus').textContent = '';
  await loadStep4Resources();
  showStep(4);
});

// --- Step 4: configure & deploy -------------------------------------------
async function loadAppInsights() {
  const [items, links] = await Promise.all([
    api.get('/api/appinsights'),
    api.get('/api/apim-links').catch(() => []),
  ]);
  const sel = $('appInsights');
  sel.innerHTML = '';
  const list = Array.isArray(items) ? items : [];
  const linkMap = new Map();
  (Array.isArray(links) ? links : []).forEach((l) => {
    const k = (l.appInsightsId || '').toLowerCase();
    linkMap.set(k, [...(linkMap.get(k) || []), l.apim]);
  });
  let preferred = null;
  list.forEach((a) => {
    const opt = document.createElement('option');
    opt.value = a.id;
    const apims = linkMap.get((a.id || '').toLowerCase());
    opt.textContent = `${a.name} (${a.resourceGroup})${apims ? `  ★ APIM '${apims.join("', '")}' logs here` : ''}`;
    if (apims && !preferred) preferred = a.id;
    sel.appendChild(opt);
  });
  if (preferred) sel.value = preferred;
  window.apimLinkedCount = [...linkMap.keys()].length;
  renderAiGuide();
  return list.length;
}

// Explain where token data actually comes from, so the right Application Insights is chosen.
function renderAiGuide() {
  const mode = document.querySelector('input[name="aiMode"]:checked')?.value;
  const box = $('aiGuide');
  const sel = $('appInsights');
  const chosenOpt = sel.options[sel.selectedIndex];
  const linked = chosenOpt && chosenOpt.textContent.includes('★');
  if (mode === 'new') {
    box.className = 'hint callout warn';
    box.innerHTML = '<strong>A new component is empty.</strong> Token numbers come from the Application Insights that <em>APIM</em> logs to (policy <code>llm-emit-token-metric</code>), or from Foundry/OpenAI tracing. After deploying, point your APIM logger (or your Foundry project\'s Application Insights connection) at this new component — otherwise reports stay empty. If APIM already logs to an existing component, choose <em>Use existing</em> and pick that one instead (marked ★).';
  } else if (linked) {
    box.className = 'hint callout good';
    box.innerHTML = '★ APIM logs to this component, so its token metrics are exactly what the default query reads. This is the right choice.';
  } else {
    box.className = 'hint callout warn';
    box.innerHTML = (window.apimLinkedCount
      ? 'This component is not the one your APIM logs to (those are marked ★). '
      : 'No APIM logger was detected in this subscription. ')
      + 'Reports only have data if APIM (<code>llm-emit-token-metric</code>) writes into this component. Using Foundry directly without APIM? Pick the Application Insights connected to your Foundry project and choose the <em>Foundry / GenAI telemetry</em> query below.';
  }
}

// Populate a <select> with {name, resourceGroup} items; stash the RG on each option.
function fillResourceSelect(selId, items, labelFn) {
  const sel = $(selId);
  sel.innerHTML = '';
  const list = Array.isArray(items) ? items : [];
  list.forEach((it) => {
    const opt = document.createElement('option');
    opt.value = it.name;
    opt.dataset.rg = it.resourceGroup || '';
    opt.textContent = labelFn(it);
    sel.appendChild(opt);
  });
  return list.length;
}

// Wire an existing/create radio group; disables "existing" when no options exist.
function wirePicker(modeName, existingEl, newEl, hasExisting) {
  const radios = document.querySelectorAll(`input[name="${modeName}"]`);
  const apply = () => {
    const mode = document.querySelector(`input[name="${modeName}"]:checked`).value;
    $(existingEl).classList.toggle('hidden', mode !== 'existing');
    $(newEl).classList.toggle('hidden', mode !== 'new');
  };
  radios.forEach((r) => r.addEventListener('change', apply));
  const ex = document.querySelector(`input[name="${modeName}"][value="existing"]`);
  const nw = document.querySelector(`input[name="${modeName}"][value="new"]`);
  if (!hasExisting) {
    // No existing resources — force "create new".
    ex.disabled = true;
    nw.checked = true;
  } else {
    ex.disabled = false;
  }
  apply();
}

async function loadStep4Resources() {
  const [plans, apps, aiCount] = await Promise.all([
    api.get('/api/plans'),
    api.get(`/api/webapps?resourceGroup=${encodeURIComponent(window.selectedRg)}`),
    loadAppInsights(),
    loadQueries(),
  ]);

  const hasPlans = fillResourceSelect('planExisting', plans, (p) => `${p.name} (${p.resourceGroup}${p.sku ? ', ' + p.sku : ''})`);
  const hasApps = fillResourceSelect('appExisting', apps, (a) => `${a.name}`);

  wirePicker('aiMode', 'appInsights', 'aiNewFields', aiCount > 0);
  wirePicker('planMode', 'planExisting', 'planNewFields', hasPlans);
  wirePicker('appMode', 'appExisting', 'appNew', hasApps);
  renderAiGuide();
}

document.querySelectorAll('input[name="aiMode"]').forEach((r) => r.addEventListener('change', renderAiGuide));
$('appInsights').addEventListener('change', renderAiGuide);

// Prefix used for auto-generated names; restricted to characters valid in Azure resource names.
const namePrefix = () =>
  ($('baseName').value.trim().replace(/[^A-Za-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '')) || 'tokrpt';

// The name prefix only feeds auto-generated names; show exactly what a blank field will become.
function updateNamePreview() {
  const base = namePrefix();
  $('aiNew').placeholder = `Leave blank to auto-name (${base}-appi)`;
  $('planNew').placeholder = `Leave blank to auto-name (${base}-plan)`;
  $('appNew').placeholder = `Leave blank to auto-name (${base}-app-xxxxxx)`;
  $('namePreview').innerHTML = `The prefix is only used for resources whose name you leave blank below — e.g. <code>${base}-plan</code>, <code>${base}-app-xxxxxx</code> (random suffix), <code>${base}-appi</code>. Names you type in yourself are used exactly as entered. Web app names must be unique across all of Azure.`;
}
$('baseName').addEventListener('input', updateNamePreview);
updateNamePreview();

// --- Report query (KQL) editor ------------------------------------------------
const normalizeQuery = (q) => (q || '').replace(/\r\n/g, '\n').trim();

async function loadQueries() {
  const r = await api.get('/api/queries');
  window.queryTemplates = r.queries || [];
  window.requiredColumns = r.required || [];
  const sel = $('queryTemplate');
  sel.innerHTML = '';
  window.queryTemplates.forEach((q) => {
    const o = document.createElement('option');
    o.value = q.id; o.textContent = q.name; sel.appendChild(o);
  });
  if (!window.queryTemplates.length) {
    $('queryBox').classList.add('hidden');
    return;
  }
  $('queryBox').classList.remove('hidden');
  applyTemplate();
  $('queryHint').textContent = `The query must return these columns: ${window.requiredColumns.join(', ')} (missing numeric columns count as 0). The app applies the report date range itself — don't add a time filter.`;
}

function currentTemplate() {
  return (window.queryTemplates || []).find((q) => q.id === $('queryTemplate').value);
}

function applyTemplate() {
  const t = currentTemplate();
  if (!t) return;
  $('reportQuery').value = t.query;
  $('queryDesc').textContent = t.description;
  $('queryStatus').textContent = '';
  $('queryStatus').className = 'status';
}

$('queryTemplate').addEventListener('change', applyTemplate);
$('btnResetQuery').addEventListener('click', applyTemplate);

$('btnValidateQuery').addEventListener('click', async () => {
  const st = $('queryStatus');
  const mode = document.querySelector('input[name="aiMode"]:checked').value;
  if (mode !== 'existing' || !$('appInsights').value) {
    st.className = 'status info';
    st.textContent = 'Validation runs against an existing Application Insights. A new component does not exist yet — after the deploy, use Run report now to check the query.';
    return;
  }
  st.className = 'status info';
  st.innerHTML = '<span class="spinner"></span>Validating against the last 30 days…';
  $('btnValidateQuery').disabled = true;
  try {
    const r = await api.post('/api/validate-query', {
      appInsightsResourceId: $('appInsights').value,
      query: $('reportQuery').value,
    });
    if (r.error || r.ok === false) {
      st.className = 'status err';
      st.textContent = r.error || 'Query failed.';
    } else if (r.missing && r.missing.length) {
      st.className = 'status err';
      st.textContent = `Query runs, but these required columns are missing: ${r.missing.join(', ')}.`;
    } else {
      st.className = 'status ok';
      st.textContent = `✔ Query is valid — ${r.rowCount} row(s) in the last 30 days, all required columns present.`;
    }
  } catch (e) {
    st.className = 'status err';
    st.textContent = String(e);
  } finally {
    $('btnValidateQuery').disabled = false;
  }
});

// Resolve a picker to { name, resourceGroup } (name may be null => script auto-names).
function resolvePicker(modeName, existingSelId, newInputId) {
  const mode = document.querySelector(`input[name="${modeName}"]:checked`).value;
  if (mode === 'existing') {
    const sel = $(existingSelId);
    const opt = sel.options[sel.selectedIndex];
    return { name: opt ? opt.value : null, resourceGroup: opt ? opt.dataset.rg : null, mode };
  }
  const val = $(newInputId).value.trim();
  return { name: val || null, resourceGroup: window.selectedRg, mode };
}

$('btnDeploy').addEventListener('click', async () => {
  const aiMode = document.querySelector('input[name="aiMode"]:checked').value;
  let appInsightsResourceId = '';
  let newAppInsightsName = null;
  if (aiMode === 'existing') {
    appInsightsResourceId = $('appInsights').value;
    if (!appInsightsResourceId) {
      $('deployStatus').className = 'status err';
      $('deployStatus').textContent = 'Select an Application Insights component, or choose "Create new".';
      return;
    }
  } else {
    const base = namePrefix();
    newAppInsightsName = $('aiNew').value.trim() || `${base}-appi`;
    if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,259}$/.test(newAppInsightsName)) {
      $('deployStatus').className = 'status err';
      $('deployStatus').textContent = 'Application Insights name may only contain letters, numbers, "-", "_" and ".".';
      return;
    }
  }

  const plan = resolvePicker('planMode', 'planExisting', 'planNew');
  const appPick = resolvePicker('appMode', 'appExisting', 'appNew');

  // Only send a custom query when it differs from the bundled default (null => app keeps its default query).
  const defaultTemplate = (window.queryTemplates || []).find((q) => q.id === 'apim-token-metrics');
  const queryText = $('reportQuery').value;
  if (!queryText.trim() && (window.queryTemplates || []).length) {
    $('deployStatus').className = 'status err';
    $('deployStatus').textContent = 'The report query is empty. Use "Reset to template" or enter a query.';
    return;
  }
  const reportQuery = defaultTemplate && normalizeQuery(queryText) === normalizeQuery(defaultTemplate.query) ? null : queryText;

  const req = {
    appInsightsResourceId,
    newAppInsightsName,
    reportQuery,
    location: $('location').value,
    subscriptionId: $('subscription').value,
    baseName: namePrefix(),
    resourceGroup: window.selectedRg,
    planSku: $('planSku').value,
    lookbackHours: Number($('lookback').value) || 24,
    endOffsetHours: 0,
    runNow: $('runNow').checked,
    grantAllSubscriptions: $('grantAll').checked,
    planName: plan.name,
    planResourceGroup: plan.resourceGroup,
    appName: appPick.name,
  };

  $('btnDeploy').disabled = true;
  $('deployStatus').className = 'status info';
  $('deployStatus').innerHTML = '<span class="spinner"></span>Deploying… this can take several minutes.';
  const log = $('log');
  log.classList.remove('hidden');
  log.textContent = '';

  try {
    const resp = await fetch('/api/deploy', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(req),
    });
    const reader = resp.body.getReader();
    const decoder = new TextDecoder();
    let succeeded = false;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      const chunk = decoder.decode(value, { stream: true });
      if (chunk.includes('__EXIT__ 0')) succeeded = true;
      log.textContent += chunk.replace(/__EXIT__ \d+\n?/g, '');
      log.scrollTop = log.scrollHeight;
    }
    if (succeeded) {
      $('deployStatus').className = 'status ok';
      $('deployStatus').textContent = '✔ Deployment complete. The app serves reports through its secured API.';
      // Parse the app URL + API key the script printed.
      const full = log.textContent;
      const mApp = full.match(/App Service\s*:\s*(\S+)/);
      const mUrl = full.match(/App URL\s*:\s*(\S+)/);
      const mKey = full.match(/Api Key\s*:\s*(\S+)/);
      const mAi = full.match(/App Insights\s*:\s*(\S+)/);
      $('viewerLaunch').classList.remove('hidden');

      // Persist config so next launch can jump straight to reports (via the app API).
      const config = {
        subscriptionId: req.subscriptionId,
        resourceGroup: req.resourceGroup,
        appInsightsResourceId: (mAi && mAi[1]) || req.appInsightsResourceId,
        appName: (mApp && mApp[1]) || '',
        appUrl: (mUrl && mUrl[1]) || '',
        apiKey: (mKey && mKey[1]) || '',
        location: req.location,
        deployedAt: new Date().toISOString(),
        account: $('whoami').textContent,
      };
      window.savedConfig = config;
      try { await api.post('/api/save-config', config); } catch { /* non-fatal */ }
    } else {
      $('deployStatus').className = 'status err';
      $('deployStatus').textContent = 'Deployment finished with errors — check the log above.';
    }
  } catch (e) {
    $('deployStatus').className = 'status err';
    $('deployStatus').textContent = String(e);
  } finally {
    $('btnDeploy').disabled = false;
  }
});

// --- Report viewer ---------------------------------------------------------
function showViewer(show, from) {
  if (show) {
    window.viewerFrom = from || 'step4';
    $('viewer').classList.remove('hidden');
    $('landing').classList.add('hidden');
    $('stepsBar').classList.add('hidden');
    for (let i = 1; i <= 4; i++) $(`step${i}`).classList.add('hidden');
  } else {
    $('viewer').classList.add('hidden');
    if (window.viewerFrom === 'landing') showLanding();
    else showStep(4);
  }
}

function renderViewerAppInfo() {
  const cfg = window.savedConfig || {};
  const rows = [
    ['App', cfg.appName],
    ['App URL', cfg.appUrl],
    ['Resource group', cfg.resourceGroup],
  ].filter(([, v]) => v);
  $('viewerAppInfo').innerHTML = rows.length
    ? rows.map(([k, v]) => `<div class="row"><span class="k">${k}</span><span class="v">${v}</span></div>`).join('')
    : '<div class="row"><span class="k">App</span><span class="v">not deployed yet</span></div>';
}

$('btnOpenViewer').addEventListener('click', () => {
  renderViewerAppInfo();
  showViewer(true);
  if ((window.savedConfig || {}).appUrl) listBlobs();
});
$('btnViewerBack').addEventListener('click', () => showViewer(false));

function csvToTable(csv, name) {
  window.currentCsv = csv;
  window.currentName = name || 'token-usage-report.csv';
  $('btnDownload').disabled = !csv;
  const wrap = $('csvTableWrap');
  wrap.innerHTML = '';
  const lines = csv.split(/\r?\n/).filter((l) => l.length > 0);
  if (lines.length === 0) { wrap.textContent = '(empty file)'; return; }
  const parseLine = (line) => {
    const out = []; let cur = ''; let q = false;
    for (let i = 0; i < line.length; i++) {
      const c = line[i];
      if (q) {
        if (c === '"' && line[i + 1] === '"') { cur += '"'; i++; }
        else if (c === '"') q = false;
        else cur += c;
      } else {
        if (c === '"') q = true;
        else if (c === ',') { out.push(cur); cur = ''; }
        else cur += c;
      }
    }
    out.push(cur);
    return out;
  };
  const rows = lines.map(parseLine);
  const table = document.createElement('table');
  table.className = 'csv';
  const thead = document.createElement('thead');
  const htr = document.createElement('tr');
  rows[0].forEach((h) => { const th = document.createElement('th'); th.textContent = h; htr.appendChild(th); });
  thead.appendChild(htr); table.appendChild(thead);
  const tbody = document.createElement('tbody');
  for (let i = 1; i < rows.length; i++) {
    const tr = document.createElement('tr');
    rows[i].forEach((v) => {
      const td = document.createElement('td');
      td.textContent = v;
      if (v !== '' && !isNaN(Number(v))) td.className = 'num';
      tr.appendChild(td);
    });
    tbody.appendChild(tr);
  }
  table.appendChild(tbody);
  wrap.appendChild(table);
  $('csvTitle').textContent = `${rows.length - 1} row(s) × ${rows[0].length} columns`;
}

const HISTORY_KEY = 'tokrptReportHistory';
function loadHistory() {
  try { return JSON.parse(localStorage.getItem(HISTORY_KEY) || '[]'); } catch { return []; }
}
function saveToHistory(name, csv, rows) {
  const list = loadHistory().filter((h) => h.name !== name);
  list.unshift({ name, csv, rows, savedAt: new Date().toISOString() });
  while (list.length > 0) {
    try { localStorage.setItem(HISTORY_KEY, JSON.stringify(list.slice(0, 25))); return; }
    catch { list.pop(); } // storage full: drop the oldest and retry
  }
}

function renderReportList(blobReports) {
  const sel = $('blobSelect');
  const previous = sel.value;
  sel.innerHTML = '';
  const add = (value, text) => { const o = document.createElement('option'); o.value = value; o.textContent = text; sel.appendChild(o); };
  const history = loadHistory();
  history.forEach((h) => add(`local|${h.name}`, `${h.name} — ${h.rows} row(s), generated ${new Date(h.savedAt).toLocaleString()}`));
  blobReports.forEach((b) => add(`blob|${b.name}`, `${b.name} (stored in blob, ${b.size} bytes)`));
  if ([...sel.options].some((o) => o.value === previous)) sel.value = previous;
  const hasAny = sel.options.length > 0;
  $('blobSelectLabel').classList.toggle('hidden', !hasAny);
  $('blobRow').classList.toggle('hidden', !hasAny);
  return hasAny;
}

async function listBlobs() {
  $('viewerStatus').className = 'status info';
  $('viewerStatus').innerHTML = '<span class="spinner"></span>Refreshing report list…';
  let r = {};
  try { r = await api.get('/api/app-reports'); } catch (e) { r = { error: String(e) }; }
  const hasAny = renderReportList((r && r.reports) || []);

  if (r && r.error) {
    $('viewerStatus').className = 'status err';
    $('viewerStatus').innerHTML = `Could not reach the deployed app: ${r.error}<br><span class="muted">Make sure the app is deployed and running, then try again. You can also click <strong>Run report now</strong> or <strong>Show sample report</strong>.</span>`;
  } else if (!hasAny) {
    $('viewerStatus').className = 'status info';
    $('viewerStatus').innerHTML = 'No reports yet. Click <strong>Run report now</strong> to generate one from Application Insights — it will be saved here so you can reopen or download it — or <strong>Show sample report</strong> to preview the format.';
  } else {
    $('viewerStatus').className = 'status ok';
    $('viewerStatus').textContent = `${$('blobSelect').options.length} saved report(s) available — pick one and click Load.`;
  }
}

async function loadBlob() {
  const [kind, ...rest] = ($('blobSelect').value || '').split('|');
  const name = rest.join('|');
  if (!name) return;
  if (kind === 'local') {
    const h = loadHistory().find((x) => x.name === name);
    if (!h) { $('viewerStatus').className = 'status err'; $('viewerStatus').textContent = 'That report is no longer saved in this browser.'; return; }
    $('viewerStatus').className = 'status ok';
    $('viewerStatus').textContent = `Showing ${name}`;
    csvToTable(h.csv, name);
    return;
  }
  $('viewerStatus').className = 'status info';
  $('viewerStatus').innerHTML = '<span class="spinner"></span>Loading via the app…';
  const r = await api.get(`/api/app-report?name=${encodeURIComponent(name)}`);
  if (r.error) { $('viewerStatus').className = 'status err'; $('viewerStatus').textContent = r.error; return; }
  $('viewerStatus').textContent = '';
  csvToTable(r.csv, name.split('/').pop());
}

function downloadCurrent() {
  if (!window.currentCsv) return;
  const blob = new Blob([window.currentCsv], { type: 'text/csv;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = window.currentName || 'token-usage-report.csv';
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

async function showSample() {
  $('viewerStatus').className = 'status info';
  $('viewerStatus').innerHTML = '<span class="spinner"></span>Loading sample…';
  const r = await api.get('/api/sample-report');
  $('viewerStatus').className = 'status info';
  $('viewerStatus').innerHTML = '<span class="muted">Preview of the report format (sample data, not from your storage).</span>';
  csvToTable(r.csv, 'sample-token-usage-report.csv');
}

async function runReport() {
  const startDate = $('reportStartDate').value;
  const endDate = $('reportEndDate').value;
  if (!startDate || !endDate) {
    $('viewerStatus').className = 'status err';
    $('viewerStatus').textContent = 'Choose both a start date and an end date.';
    return;
  }
  if (startDate > endDate) {
    $('viewerStatus').className = 'status err';
    $('viewerStatus').textContent = 'The start date must be on or before the end date.';
    return;
  }

  $('btnRunReport').disabled = true;
  $('viewerStatus').className = 'status info';
  $('viewerStatus').innerHTML = `<span class="spinner"></span>Generating the ${startDate} through ${endDate} report from Application Insights (this can take up to a minute)…`;
  $('csvTableWrap').innerHTML = '';
  $('csvTitle').textContent = '';
  try {
    const r = await api.post('/api/app-run', { startDate, endDate });
    if (r.error) {
      $('viewerStatus').className = 'status err';
      $('viewerStatus').textContent = r.error;
      return;
    }
    const fmt = (s) => new Date(s).toISOString().replace('T', ' ').slice(0, 16);
    const windowTxt = r.windowStartUtc ? `${fmt(r.windowStartUtc)} → ${fmt(r.windowEndUtc)} UTC` : `${startDate} → ${endDate} UTC`;
    if (r.empty || !r.csv) {
      $('viewerStatus').className = 'status info';
      $('viewerStatus').innerHTML = `Report ran successfully (window ${windowTxt}), but <strong>no token data was returned</strong>. Common reasons: the app's query doesn't match the kind of telemetry in this Application Insights (APIM metrics vs Foundry traces), the data is outside this window, or very recent telemetry is still being ingested (can take a few minutes). Click <strong>Why no data? (Diagnose)</strong> to see what is actually in Application Insights.`;
      return;
    }
    const reportName = `token-usage_${startDate}_to_${endDate}.csv`;
    saveToHistory(reportName, r.csv, r.rowCount);
    renderReportList([]);
    $('blobSelect').value = `local|${reportName}`;
    $('viewerStatus').className = 'status ok';
    $('viewerStatus').textContent = `✔ Report generated (${r.rowCount} row(s), window ${windowTxt}) and saved as ${reportName}.`;
    csvToTable(r.csv, reportName);
  } catch (e) {
    $('viewerStatus').className = 'status err';
    $('viewerStatus').textContent = String(e);
  } finally {
    $('btnRunReport').disabled = false;
  }
}

$('btnRunReport').addEventListener('click', runReport);
$('btnDiagnose').addEventListener('click', diagnose);

const QUERY_NAMES = {
  'apim-token-metrics': 'APIM token metrics (default)',
  'foundry-genai': 'Foundry / GenAI telemetry',
  'genai-token-metric': 'GenAI token-usage metric',
  custom: 'a custom query',
};
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

async function diagnose() {
  const box = $('diagBox');
  box.className = 'hint callout';
  box.innerHTML = '<span class="spinner"></span>Inspecting the app\'s Application Insights (last 30 days)…';
  $('btnDiagnose').disabled = true;
  try {
    const d = await api.post('/api/diagnose', {});
    if (d.error) { box.className = 'hint callout warn'; box.textContent = d.error; return; }

    const lines = [];
    const inUse = QUERY_NAMES[d.queryInUse] || d.queryInUse;
    lines.push(`<li>The deployed app currently uses the <strong>${esc(inUse)}</strong> query.</li>`);
    if (d.hasApimMetrics) lines.push('<li>✔ Found <strong>APIM token metrics</strong> (Prompt/Completion/Total Tokens) in <code>customMetrics</code>.</li>');
    if (d.genAiSpanCount > 0) lines.push(`<li>✔ Found <strong>${esc(d.genAiSpanCount)} Foundry/GenAI span(s)</strong> with token counts in <code>dependencies</code> (${esc(String(d.genAiSpanFirst).slice(0, 16))} → ${esc(String(d.genAiSpanLast).slice(0, 16))} UTC).</li>`);
    if (d.hasGenAiMetric) lines.push('<li>✔ Found the OpenTelemetry metric <code>gen_ai.client.token.usage</code> in <code>customMetrics</code>.</li>');
    if (!d.hasApimMetrics && !(d.genAiSpanCount > 0) && !d.hasGenAiMetric) lines.push('<li>✘ No recognised token data in this Application Insights in the last 30 days.</li>');

    let verdict, cls;
    if (!d.recommended) {
      cls = 'warn';
      verdict = 'Nothing the report knows how to read was found. Check that this is the Application Insights your APIM / Foundry project writes to, that traffic has run recently (ingestion can lag a few minutes), then look at the token-related fields below to write a custom query.';
    } else if (d.recommended === d.queryInUse) {
      cls = 'good';
      verdict = 'The app\'s query matches the data. If the report is still empty, widen the date range (use <em>Last 7 days</em> / <em>Today</em>) and retry in a few minutes.';
    } else {
      cls = 'warn';
      verdict = `<strong>Mismatch:</strong> your data fits the <strong>${esc(QUERY_NAMES[d.recommended] || d.recommended)}</strong> query, but the app is running ${esc(inUse)}. Redeploy and, in <em>Advanced: report query</em>, choose that template.`;
    }

    const names = (d.metrics || []).slice(0, 12).map((m) => `<code>${esc(m.name)}</code> (${esc(m.count)})`).join(', ');
    const keys = (d.tokenKeys || []).map((k) => `<code>${esc(k.itemType)}.${esc(k.key)}</code> (${esc(k.count)})`).join(', ');
    box.className = `hint callout ${cls}`;
    box.innerHTML = `<ul style="margin:0 0 8px 18px;padding:0">${lines.join('')}</ul><div>${verdict}</div>` +
      (names ? `<details style="margin-top:8px"><summary>Metrics present in Application Insights</summary>${names}</details>` : '') +
      (keys ? `<details style="margin-top:6px"><summary>Token-related fields found (last 7 days)</summary>${keys}</details>` : '');
  } catch (e) {
    box.className = 'hint callout warn';
    box.textContent = String(e);
  } finally {
    $('btnDiagnose').disabled = false;
  }
}
$('btnListBlobs').addEventListener('click', listBlobs);
$('btnLoadBlob').addEventListener('click', loadBlob);
$('btnDownload').addEventListener('click', downloadCurrent);
$('btnSample').addEventListener('click', showSample);

const iso = (d) => d.toISOString().slice(0, 10);
const utcDate = (y, m, d) => new Date(Date.UTC(y, m, d));
const todayUtc = utcDate(new Date().getUTCFullYear(), new Date().getUTCMonth(), new Date().getUTCDate());
const yesterdayUtc = utcDate(todayUtc.getUTCFullYear(), todayUtc.getUTCMonth(), todayUtc.getUTCDate() - 1);

function updateRangeSummary() {
  const s = $('reportStartDate').value, e = $('reportEndDate').value;
  const box = $('rangeSummary');
  if (!s || !e) { box.textContent = 'Pick a start and end date, or use a quick range.'; return; }
  if (s > e) { box.textContent = 'Start date must be on or before the end date.'; return; }
  const days = Math.round((Date.parse(e) - Date.parse(s)) / 86400000) + 1;
  box.textContent = `Selected: ${s} → ${e} (${days} day${days === 1 ? '' : 's'}, UTC)`;
}

function applyRange(kind) {
  const y = todayUtc.getUTCFullYear(), m = todayUtc.getUTCMonth(), d = todayUtc.getUTCDate();
  let start, end;
  if (kind === 'yesterday') { start = end = yesterdayUtc; }
  else if (kind === 'today') { start = end = todayUtc; }
  else if (kind === 'thisMonth') { start = utcDate(y, m, 1); end = todayUtc; }
  else if (kind === 'lastMonth') { start = utcDate(y, m - 1, 1); end = utcDate(y, m, 0); }
  else { start = utcDate(y, m, d - (Number(kind) - 1)); end = todayUtc; }
  $('reportStartDate').value = iso(start);
  $('reportEndDate').value = iso(end);
  document.querySelectorAll('#rangeChips .chip').forEach(c => c.classList.toggle('active', c.dataset.range === kind));
  updateRangeSummary();
}

document.querySelectorAll('#rangeChips .chip').forEach(c =>
  c.addEventListener('click', () => applyRange(c.dataset.range)));
['reportStartDate', 'reportEndDate'].forEach(id => $(id).addEventListener('input', () => {
  document.querySelectorAll('#rangeChips .chip').forEach(c => c.classList.remove('active'));
  updateRangeSummary();
}));
$('reportEndDate').max = iso(todayUtc);
$('reportStartDate').max = iso(todayUtc);
applyRange('7');

// --- Landing (remembered deployment) ---------------------------------------
function renderSummary(cfg) {
  const rows = [
    ['App Service', cfg.appName],
    ['App URL', cfg.appUrl],
    ['Resource group', cfg.resourceGroup],
    ['Location', cfg.location],
    ['Signed in as', cfg.account],
    ['Deployed', cfg.deployedAt ? new Date(cfg.deployedAt).toLocaleString() : ''],
  ].filter(([, v]) => v);
  $('landingSummary').innerHTML = rows
    .map(([k, v]) => `<div class="row"><span class="k">${k}</span><span class="v">${v}</span></div>`)
    .join('');
}

$('btnLandingViewer').addEventListener('click', () => {
  renderViewerAppInfo();
  showViewer(true, 'landing');
  if ((window.savedConfig || {}).appUrl) listBlobs();
});

$('btnLandingSetup').addEventListener('click', () => {
  showStep(1);
  checkPrereqs();
  refreshStatus();
});

// --- Init ------------------------------------------------------------------
async function init() {
  let cfg = {};
  try { cfg = await api.get('/api/config'); } catch { /* none */ }
  window.savedConfig = cfg;
  if (cfg && cfg.appUrl) {
    // A previous deployment is remembered — offer the fast path.
    renderSummary(cfg);
    showLanding();
  } else {
    showStep(1);
    checkPrereqs();
    refreshStatus();
  }
}
init();
