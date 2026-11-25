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
const STEP_COUNT = 5;

// --- Step navigation -------------------------------------------------------
function showStep(n) {
  if (typeof stopLive === 'function') stopLive();
  $('appRoot').classList.remove('wide');
  $('landing').classList.add('hidden');
  $('viewer').classList.add('hidden');
  $('stepsBar').classList.remove('hidden');
  for (let i = 1; i <= STEP_COUNT; i++) {
    $(`step${i}`).classList.toggle('hidden', i !== n);
    const dot = $(`stepDot${i}`);
    dot.classList.toggle('active', i === n);
    dot.classList.toggle('done', i < n);
  }
}

function showLanding() {
  if (typeof stopLive === 'function') stopLive();
  $('appRoot').classList.remove('wide');
  $('landing').classList.remove('hidden');
  $('viewer').classList.add('hidden');
  $('stepsBar').classList.add('hidden');
  for (let i = 1; i <= STEP_COUNT; i++) $(`step${i}`).classList.add('hidden');
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
  window.tenantList = r.tenants || [];
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

// Display name of the active tenant, for report headings.
function currentTenantName() {
  const t = (window.tenantList || []).find((x) => (x.tenantId || '').toLowerCase() === (window.currentTenantId || '').toLowerCase());
  return (t && (t.displayName || t.defaultDomain)) || '';
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
    : 'Pick the directory that contains the subscriptions you want to report on. If it differs from your current one, click <strong>Switch tenant</strong> — a sign-in window opens for that tenant.';
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

// --- Step 2: subscriptions (multi-select) -----------------------------------
window.allSubscriptions = [];
window.subSelection = new Set();

async function loadSubscriptions() {
  const tenantId = window.currentTenantId || '';
  const subs = await api.get(`/api/subscriptions?tenantId=${encodeURIComponent(tenantId)}`);
  const list = Array.isArray(subs) ? subs : [];
  window.allSubscriptions = list.map((s) => ({ id: s.id, name: s.name }));
  // "All subscriptions" is the common intent, so everything starts ticked.
  window.subSelection = new Set(window.allSubscriptions.map((s) => s.id));
  const tName = currentTenantName() || tenantId;
  $('subSub').textContent = list.length
    ? `Tick every subscription whose Foundry usage should be in the report (tenant ${tName}).`
    : `No enabled subscriptions found in tenant ${tName} for your account. Go back and switch to a different tenant.`;
  $('subFilter').value = '';
  renderSubList();
}

function updateSubCount() {
  $('subCount').textContent = `${window.subSelection.size} of ${window.allSubscriptions.length} subscription(s) selected.`;
  $('btnSubNext').disabled = window.subSelection.size === 0;
}

function renderSubList() {
  const filter = ($('subFilter').value || '').trim().toLowerCase();
  const list = $('subList');
  list.innerHTML = '';
  window.allSubscriptions
    .filter((s) => !filter || `${s.name} ${s.id}`.toLowerCase().includes(filter))
    .forEach((s) => {
      const row = document.createElement('label');
      row.className = 'checkrow';
      const cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.checked = window.subSelection.has(s.id);
      cb.addEventListener('change', () => {
        if (cb.checked) window.subSelection.add(s.id); else window.subSelection.delete(s.id);
        updateSubCount();
      });
      const text = document.createElement('span');
      text.textContent = `${s.name} (${s.id})`;
      row.appendChild(cb);
      row.appendChild(text);
      list.appendChild(row);
    });
  updateSubCount();
}

$('subFilter').addEventListener('input', renderSubList);
$('btnSubAll').addEventListener('click', () => {
  const filter = ($('subFilter').value || '').trim().toLowerCase();
  window.allSubscriptions.filter((s) => !filter || `${s.name} ${s.id}`.toLowerCase().includes(filter)).forEach((s) => window.subSelection.add(s.id));
  renderSubList();
});
$('btnSubNone').addEventListener('click', () => { window.subSelection.clear(); renderSubList(); });
$('btnSubNext').addEventListener('click', () => { showStep(3); runDiscovery(); });

// --- Step 3: detect Foundry resources ---------------------------------------
let discoveryRun = 0;

async function runDiscovery() {
  const run = ++discoveryRun;
  $('discoverStatus').className = 'status info';
  $('discoverStatus').innerHTML = '<span class="spinner"></span>Scanning resource groups, Foundry resources, linked Application Insights, models and agents… this can take up to a minute.';
  $('discoverSummary').innerHTML = '';
  $('discoverTree').innerHTML = '';
  $('discoverWarnings').classList.add('hidden');
  $('btnDiscoverNext').disabled = true;
  $('btnDiscoverRescan').disabled = true;
  try {
    const r = await api.post('/api/discover', { subscriptionIds: [...window.subSelection] });
    if (run !== discoveryRun) return;
    if (r.error) {
      $('discoverStatus').className = 'status err';
      $('discoverStatus').textContent = r.error;
      return;
    }
    window.discovery = r;
    renderDiscovery(r);
    $('btnDiscoverNext').disabled = false;
  } catch (e) {
    if (run !== discoveryRun) return;
    $('discoverStatus').className = 'status err';
    $('discoverStatus').textContent = String(e);
  } finally {
    if (run === discoveryRun) $('btnDiscoverRescan').disabled = false;
  }
}

function el(tag, className, text) {
  const e = document.createElement(tag);
  if (className) e.className = className;
  if (text !== undefined) e.textContent = text;
  return e;
}

function treeNode(title, children, open) {
  const d = document.createElement('details');
  d.open = !!open;
  const s = document.createElement('summary');
  s.textContent = title;
  d.appendChild(s);
  children.forEach((c) => d.appendChild(c));
  return d;
}

function summarizeDiscovery(d) {
  const accounts = d.accounts || [];
  const projects = accounts.flatMap((a) => a.projects || []);
  return {
    subscriptions: (d.subscriptions || []).length,
    resourceGroups: (d.resourceGroups || []).length,
    foundryResources: accounts.length,
    projects: projects.length,
    appInsights: (d.appInsights || []).length,
    agents: projects.reduce((n, p) => n + (p.agents || []).length, 0),
    models: accounts.reduce((n, a) => n + (a.deployments || []).length, 0),
    unlinked: projects.filter((p) => !(p.appInsightsIds || []).length).length,
  };
}

function renderDiscovery(d) {
  const sum = summarizeDiscovery(d);
  const cards = [
    ['Subscriptions', sum.subscriptions], ['Resource groups', sum.resourceGroups], ['Foundry resources', sum.foundryResources],
    ['Projects', sum.projects], ['Linked App Insights', sum.appInsights], ['Agents', sum.agents], ['Deployed models', sum.models],
  ];
  const box = $('discoverSummary');
  box.innerHTML = '';
  cards.forEach(([k, v]) => {
    const c = el('div', 'kpi');
    c.appendChild(el('div', 'kpi-v', String(v)));
    c.appendChild(el('div', 'kpi-k', k));
    box.appendChild(c);
  });

  const status = $('discoverStatus');
  if (sum.foundryResources === 0) {
    status.className = 'status err';
    status.textContent = 'No Foundry resources were found in the selected subscriptions. You can still deploy — new projects are picked up when they appear — or go back and select more subscriptions.';
  } else if (sum.unlinked > 0) {
    status.className = 'status info';
    status.textContent = `Found ${sum.projects} project(s); ${sum.unlinked} have no linked Application Insights, so their usage cannot be reported.`;
  } else {
    status.className = 'status ok';
    status.textContent = `Found ${sum.foundryResources} Foundry resource(s) and ${sum.projects} project(s), all linked to Application Insights.`;
  }

  const tenantLabel = currentTenantName() || d.tenantId || window.currentTenantId || 'tenant';
  const insightsById = new Map((d.appInsights || []).map((a) => [(a.id || '').toLowerCase(), a]));
  const subName = new Map((d.subscriptions || []).map((s) => [s.id.toLowerCase(), s.name]));
  const subNodes = [];
  (d.subscriptions || []).forEach((s) => {
    const accounts = (d.accounts || []).filter((a) => a.subscriptionId.toLowerCase() === s.id.toLowerCase());
    const groups = (d.resourceGroups || []).filter((g) => g.subscriptionId.toLowerCase() === s.id.toLowerCase());
    const rgNodes = [];
    [...new Set(accounts.map((a) => a.resourceGroup))].sort((a, b) => a.localeCompare(b)).forEach((rg) => {
      const accountNodes = accounts.filter((a) => a.resourceGroup === rg).map((a) => {
        const kids = [];
        const models = (a.deployments || []).map((m) => `${m.name} → ${m.model}${m.version ? ' ' + m.version : ''}${m.sku ? ` [${m.sku}]` : ''}`);
        kids.push(el('div', 'leaf', models.length ? `Models (${models.length}): ${models.join(', ')}` : 'Models: none deployed'));
        (a.projects || []).forEach((p) => {
          const links = (p.appInsightsIds || []).map((id) => (insightsById.get(id.toLowerCase())?.name) || id.split('/').pop());
          const agents = (p.agents || []).map((x) => x.name);
          const pk = [];
          pk.push(el('div', links.length ? 'leaf' : 'leaf warn', links.length ? `Application Insights: ${links.join(', ')}` : '⚠ No linked Application Insights — usage cannot be reported'));
          pk.push(el('div', 'leaf', agents.length ? `Agents (${agents.length}): ${agents.join(', ')}` : (p.agentsError ? `Agents: could not be listed (${p.agentsError})` : 'Agents: none')));
          kids.push(treeNode(`Project ${p.name}`, pk, false));
        });
        return treeNode(`Foundry resource ${a.name} (${a.location})`, kids, false);
      });
      rgNodes.push(treeNode(`Resource group ${rg}`, accountNodes, false));
    });
    const scanned = groups.length;
    const withFoundry = rgNodes.length;
    const title = `Subscription ${subName.get(s.id.toLowerCase()) || s.name} — ${withFoundry} of ${scanned} resource group(s) contain Foundry`;
    subNodes.push(treeNode(title, rgNodes.length ? rgNodes : [el('div', 'leaf', 'No Foundry resources in this subscription.')], true));
  });
  const tree = $('discoverTree');
  tree.innerHTML = '';
  tree.appendChild(treeNode(`Tenant ${tenantLabel}`, subNodes, true));

  const warnings = d.warnings || [];
  $('discoverWarnings').classList.toggle('hidden', warnings.length === 0);
  const list = $('discoverWarningList');
  list.innerHTML = '';
  warnings.forEach((w) => list.appendChild(el('li', '', `[${w.scope}] ${w.message}`)));
}

$('btnDiscoverRescan').addEventListener('click', runDiscovery);
$('btnDiscoverNext').addEventListener('click', () => enterHosting());

// --- Step 4: hosting ---------------------------------------------------------
async function enterHosting() {
  const sel = $('hostSubscription');
  sel.innerHTML = '';
  window.allSubscriptions.filter((s) => window.subSelection.has(s.id)).forEach((s) => {
    const o = document.createElement('option');
    o.value = s.id;
    o.textContent = `${s.name} (${s.id})`;
    sel.appendChild(o);
  });
  await selectHostSubscription();
  showStep(4);
}

async function selectHostSubscription() {
  const subscriptionId = $('hostSubscription').value;
  $('rgStatus').className = 'status info';
  $('rgStatus').innerHTML = '<span class="spinner"></span>Loading resource groups…';
  const r = await api.post('/api/subscription', { subscriptionId });
  if (r.error) {
    $('rgStatus').className = 'status err';
    $('rgStatus').textContent = r.error;
    return;
  }
  $('rgStatus').textContent = '';
  await Promise.all([loadResourceGroups(), loadLocations()]);
}

$('hostSubscription').addEventListener('change', selectHostSubscription);

async function loadResourceGroups() {
  const groups = await api.get('/api/resource-groups');
  const sel = $('resourceGroup');
  sel.innerHTML = '';
  (Array.isArray(groups) ? groups : []).forEach((g) => {
    const opt = document.createElement('option');
    opt.value = g.name;
    opt.dataset.location = g.location || '';
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
  window.hostSubscriptionId = $('hostSubscription').value;
  if (rgMode() === 'new') {
    const name = $('rgNewName').value.trim();
    const location = $('rgLocation').value;
    if (!name) { $('rgStatus').className = 'status err'; $('rgStatus').textContent = 'Enter a resource group name.'; return; }
    $('rgStatus').className = 'status info';
    $('rgStatus').innerHTML = '<span class="spinner"></span>Creating resource group…';
    const r = await api.post('/api/create-resource-group', { name, location });
    if (r.error) { $('rgStatus').className = 'status err'; $('rgStatus').textContent = r.error; return; }
    window.selectedRg = name;
    window.selectedRgLocation = location;
  } else {
    window.selectedRg = $('resourceGroup').value;
    window.selectedRgLocation = $('resourceGroup').selectedOptions[0]?.dataset.location || '';
  }
  window.locationEdited = false;
  $('rgStatus').textContent = '';
  await loadStep5Resources();
  showStep(5);
});

// --- Step 5: configure & deploy ------------------------------------------------
// Populate a <select> with {name, resourceGroup} items; stash the RG and region on each option.
function fillResourceSelect(selId, items, labelFn) {
  const sel = $(selId);
  sel.innerHTML = '';
  const list = Array.isArray(items) ? items : [];
  list.forEach((it) => {
    const opt = document.createElement('option');
    opt.value = it.name;
    opt.dataset.rg = it.resourceGroup || '';
    opt.dataset.location = it.location || '';
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

function renderScopeSummary() {
  const d = window.discovery || {};
  const sum = summarizeDiscovery(d);
  $('scopeSummary').textContent =
    `Report scope: ${window.subSelection.size} subscription(s), ${sum.projects} Foundry project(s), ${sum.appInsights} linked Application Insights. ` +
    'The app\'s managed identity is granted Reader on the subscriptions and Monitoring Reader on those Application Insights components.';
}

async function loadStep5Resources() {
  const [plans, apps] = await Promise.all([
    api.get('/api/plans'),
    api.get(`/api/webapps?resourceGroup=${encodeURIComponent(window.selectedRg)}`),
  ]);
  const hasPlans = fillResourceSelect('planExisting', plans, (p) => `${p.name} (${p.resourceGroup}, ${p.location}${p.sku ? ', ' + p.sku : ''})`);
  const hasApps = fillResourceSelect('appExisting', apps, (a) => `${a.name}`);
  // Prefer a plan already in the region of the resource group over an unrelated first plan.
  const region = normalizeRegion(window.selectedRgLocation);
  const score = (o) => (region && normalizeRegion(o.dataset.location) === region ? 2 : 0)
    + ((o.dataset.rg || '').toLowerCase() === (window.selectedRg || '').toLowerCase() ? 1 : 0);
  const best = [...$('planExisting').options].reduce((b, o) => (!b || score(o) > score(b) ? o : b), null);
  if (best) $('planExisting').value = best.value;

  wirePicker('planMode', 'planExisting', 'planNewFields', hasPlans);
  wirePicker('appMode', 'appExisting', 'appNew', hasApps);
  renderScopeSummary();
  syncDeploymentLocation();
}

const normalizeRegion = (s) => (s || '').replace(/\s+/g, '').toLowerCase();

function syncDeploymentLocation() {
  const existingPlan = document.querySelector('input[name="planMode"]:checked')?.value === 'existing';
  const planLocation = existingPlan ? $('planExisting').selectedOptions[0]?.dataset.location : '';
  const preferred = planLocation || (!window.locationEdited && window.selectedRgLocation);
  if (preferred) {
    const value = normalizeRegion(preferred);
    if (![...$('location').options].some((o) => o.value === value)) {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = preferred;
      $('location').appendChild(option);
    }
    $('location').value = value;
  }
  $('location').disabled = !!planLocation;
  $('locationHint').textContent = planLocation
    ? `Hosting region is fixed by the existing App Service plan: ${planLocation}. Your Foundry resources and Application Insights are not moved.`
    : 'Region for the new app. Defaults to the hosting resource group\'s region; your Foundry resources are not moved.';
}

$('location').addEventListener('change', () => { window.locationEdited = true; });
$('planExisting').addEventListener('change', syncDeploymentLocation);
document.querySelectorAll('input[name="planMode"]').forEach((r) => r.addEventListener('change', syncDeploymentLocation));

// Prefix used for auto-generated names; restricted to characters valid in Azure resource names.
const namePrefix = () =>
  ($('baseName').value.trim().replace(/[^A-Za-z0-9-]+/g, '-').replace(/-+/g, '-').replace(/^-|-$/g, '')) || 'tokrpt';

// The name prefix only feeds auto-generated names; show exactly what a blank field will become.
function updateNamePreview() {
  const base = namePrefix();
  $('planNew').placeholder = `Leave blank to auto-name (${base}-plan)`;
  $('appNew').placeholder = `Leave blank to auto-name (${base}-app-xxxxxx)`;
  $('namePreview').innerHTML = `The prefix is only used for resources whose name you leave blank below — e.g. <code>${base}-plan</code>, <code>${base}-app-xxxxxx</code> (random suffix). Names you type in yourself are used exactly as entered. Web app names must be unique across all of Azure.`;
}
$('baseName').addEventListener('input', updateNamePreview);
updateNamePreview();

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

function buildDeployRequest() {
  const d = window.discovery || { accounts: [], appInsights: [] };
  const plan = resolvePicker('planMode', 'planExisting', 'planNew');
  const appPick = resolvePicker('appMode', 'appExisting', 'appNew');
  return {
    location: $('location').value,
    subscriptionId: window.hostSubscriptionId,
    baseName: namePrefix(),
    resourceGroup: window.selectedRg,
    planSku: $('planSku').value,
    runNow: $('runNow').checked,
    planName: plan.name,
    planResourceGroup: plan.resourceGroup,
    appName: appPick.name,
    subscriptions: [...window.subSelection],
    tenantId: window.currentTenantId,
    tenantName: currentTenantName(),
    appInsightsIds: (d.appInsights || []).map((a) => a.id),
    foundryProjectIds: (d.accounts || []).flatMap((a) => (a.projects || []).map((p) => p.id)),
    grantSubscriptionMonitoringReader: $('grantSubMon').checked,
    grantAgentReadAccess: $('grantAgents').checked,
  };
}

$('btnDeploy').addEventListener('click', async () => {
  const req = buildDeployRequest();
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
      $('deployStatus').textContent = '✔ Deployment complete. The app serves the Foundry report through its secured API.';
      // Parse the app URL + API key the script printed.
      const full = log.textContent;
      const mApp = full.match(/App Service\s*:\s*(\S+)/);
      const mUrl = full.match(/App URL\s*:\s*(\S+)/);
      const mKey = full.match(/Api Key\s*:\s*(\S+)/);
      $('viewerLaunch').classList.remove('hidden');

      // Persist config so next launch can jump straight to the report.
      const config = {
        tenantId: req.tenantId,
        tenantName: req.tenantName,
        subscriptionIds: req.subscriptions,
        subscriptionId: req.subscriptionId,
        resourceGroup: req.resourceGroup,
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

$('btnOpenViewer').addEventListener('click', () => openDashboard('step5'));

// --- Landing (remembered deployment) ---------------------------------------
function renderSummary(cfg) {
  const rows = [
    ['Tenant', cfg.tenantName || cfg.tenantId],
    ['Subscriptions', cfg.subscriptionIds ? `${cfg.subscriptionIds.length} selected` : ''],
    ['App Service', cfg.appName],
    ['App URL', cfg.appUrl],
    ['Resource group', cfg.resourceGroup],
    ['Location', cfg.location],
    ['Signed in as', cfg.account],
    ['Deployed', cfg.deployedAt ? new Date(cfg.deployedAt).toLocaleString() : ''],
  ].filter(([, v]) => v);
  $('landingSummary').innerHTML = '';
  rows.forEach(([k, v]) => {
    const row = el('div', 'row');
    row.appendChild(el('span', 'k', k));
    row.appendChild(el('span', 'v', String(v)));
    $('landingSummary').appendChild(row);
  });
}

$('btnLandingViewer').addEventListener('click', () => openDashboard('landing'));

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
