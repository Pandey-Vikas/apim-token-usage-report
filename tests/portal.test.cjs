const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// A tiny DOM: enough for the portal scripts, with real class lists, option selection and child tracking.
function portal(responses = {}) {
  class Element {
    constructor(tag) {
      this.tag = tag;
      this.options = [];   // children
      this.dataset = {};
      this.listeners = {};
      this.style = { setProperty(name, value) { this[name] = value; } };
      this.attrs = {};
      this._classes = new Set();
      this._value = '';
      this.classList = {
        add: (c) => this._classes.add(c),
        remove: (c) => this._classes.delete(c),
        toggle: (c, force) => { const on = force === undefined ? !this._classes.has(c) : !!force; if (on) this._classes.add(c); else this._classes.delete(c); return on; },
        contains: (c) => this._classes.has(c),
      };
    }
    set innerHTML(value) { this.options = []; this.html = value; }
    get innerHTML() { return this.html || ''; }
    set value(value) { this._value = value; this.options.forEach((o) => { o.selected = o.value === value; }); }
    get value() { return this.selectedOptions[0]?.value ?? this._value; }
    get selectedOptions() {
      const selected = this.options.find((o) => o.selected);
      return selected ? [selected] : this.options.slice(0, 1);
    }
    get selectedIndex() { return this.options.indexOf(this.selectedOptions[0]); }
    appendChild(o) { this.options.push(o); return o; }
    setAttribute(name, value) { this.attrs[name] = value; }
    remove() {}
    click() {}
    addEventListener(event, callback) { this.listeners[event] = callback; }
    set checked(value) {
      this._checked = value;
      if (value && this.group) this.group.forEach((r) => { if (r !== this) r._checked = false; });
    }
    get checked() { return this._checked; }
    get hidden() { return this._classes.has('hidden'); }
    get allText() { return (this.textContent || '') + this.options.map((c) => c.allText || '').join(' '); }
  }
  const elements = new Map();
  const get = (id) => { if (!elements.has(id)) elements.set(id, new Element('div')); return elements.get(id); };
  const modes = { planMode: 'existing', appMode: 'new', rgMode: 'existing' };
  const radios = {};
  for (const [name, selected] of Object.entries(modes)) {
    radios[name] = ['existing', 'new'].map((value) => {
      const radio = new Element('input');
      radio.value = value;
      radio.checked = value === selected;
      return radio;
    });
    radios[name].forEach((r) => { r.group = radios[name]; });
  }
  const document = {
    getElementById: get,
    createElement: (tag) => new Element(tag),
    body: new Element('body'),
    querySelector(selector) {
      const name = selector.match(/name="([^"]+)"/)?.[1];
      const value = selector.match(/\[value="([^"]+)"\]/)?.[1];
      return radios[name]?.find((r) => (value ? r.value === value : r.checked));
    },
    querySelectorAll(selector) {
      return Object.keys(radios).filter((name) => selector.includes(`name="${name}"`)).flatMap((name) => radios[name]);
    },
  };
  const timers = new Map();
  const requests = [];
  let nextTimer = 0;
  const context = vm.createContext({
    document, window: {}, console, URL, clearInterval, Blob: class { constructor(parts) { this.parts = parts; } },
    setTimeout(callback, delay) { const id = ++nextTimer; timers.set(id, { callback, delay }); return id; },
    clearTimeout(id) { timers.delete(id); },
    fetch: async (url, options) => ({
      json: async () => {
        if (!(url in responses)) throw new Error(`Unexpected request: ${url}`);
        requests.push({ url, body: options?.body && JSON.parse(options.body) });
        return typeof responses[url] === 'function' ? responses[url]() : responses[url];
      },
    }),
  });
  const root = path.join(__dirname, '..', 'gui', 'wwwroot');
  vm.runInContext(fs.readFileSync(path.join(root, 'wizard.js'), 'utf8').replace(/\binit\(\);\s*$/, ''), context);
  vm.runInContext(fs.readFileSync(path.join(root, 'dashboard.js'), 'utf8'), context);
  return {
    context, get, timers, requests,
    run: (code) => vm.runInContext(code, context),
    setMode(name, value) { radios[name].forEach((r) => { r.checked = r.value === value; }); },
  };
}

const SUB1 = 'a3b144fd-39ee-4729-b816-3ed42fc8f31f';
const SUB2 = '22222222-2222-2222-2222-222222222222';
const acct = (name) => `/subscriptions/${SUB1}/resourceGroups/rg1/providers/Microsoft.CognitiveServices/accounts/${name}`;
const PROJECT_A = `${acct('acct1')}/projects/p1`;
const PROJECT_B = `${acct('acct2')}/projects/p2`;

function inventory() {
  return {
    subscriptions: [{ id: SUB1, name: 'Prod', tenantId: 't' }],
    resourceGroups: [{ subscriptionId: SUB1, name: 'rg1', location: 'eastus' }, { subscriptionId: SUB1, name: 'empty-rg', location: 'eastus' }],
    accounts: [
      { id: acct('acct1'), name: 'acct1', subscriptionId: SUB1, resourceGroup: 'rg1', location: 'eastus2',
        deployments: [{ name: 'gpt-4o-mini', model: 'gpt-4o-mini', version: '2024-07-18', sku: 'GlobalStandard' }],
        projects: [{ id: PROJECT_A, name: 'p1', appInsightsIds: [`/subscriptions/${SUB1}/resourceGroups/obs/providers/microsoft.insights/components/ai1`],
          agents: [{ name: 'Alpha', id: 'a1' }, { name: 'Sleepy', id: 'a2' }] }] },
      { id: acct('acct2'), name: 'acct2', subscriptionId: SUB1, resourceGroup: 'rg1', location: 'eastus2', deployments: [],
        projects: [{ id: PROJECT_B, name: 'p2', appInsightsIds: [], agents: [] }] },
    ],
    appInsights: [{ id: `/subscriptions/${SUB1}/resourceGroups/obs/providers/microsoft.insights/components/ai1`, name: 'ai1', subscriptionId: SUB1, resourceGroup: 'obs', location: 'eastus2' }],
    warnings: [{ scope: 'acct2/p2', message: 'No linked Application Insights connection was found, so its usage cannot be reported.' }],
  };
}

function report(extraFacts = []) {
  return {
    tenant: { id: 't', name: 'Contoso' },
    windowStartUtc: '2026-11-23T00:00:00Z', windowEndUtc: '2026-11-30T12:00:00Z',
    inventory: inventory(),
    scopes: [{ subscriptionId: SUB1, subscriptionName: 'Prod', resourceGroup: 'rg1', accountName: 'acct1', projectName: 'p1', projectId: PROJECT_A,
      appInsightsName: 'ai1', portalUrl: `https://portal.azure.com/#resource${PROJECT_A}/overview`, appInsightsPortalUrl: '' }],
    facts: [
      { day: '2026-11-23', scope: 0, agentId: 'a1', agentName: 'Alpha', model: 'gpt-4o-mini-2024-07-18', deployment: 'gpt-4o-mini', sku: 'GlobalStandard',
        promptTokens: 1000, completionTokens: 100, cachedTokens: 200, totalTokens: 1100, calls: 2, errors: 0, durationMsSum: 1000, estimatedCost: 0.00021, costBasis: 'list', lastSeen: '2026-11-23T10:00:00Z' },
      { day: '2026-11-24', scope: 0, agentId: 'a1', agentName: 'Alpha', model: 'gpt-4o-mini-2024-07-18', deployment: 'gpt-4o-mini', sku: 'GlobalStandard',
        promptTokens: 500, completionTokens: 50, cachedTokens: 0, totalTokens: 550, calls: 1, errors: 1, durationMsSum: 600, estimatedCost: 0.0001, costBasis: 'list', lastSeen: '2026-11-24T10:00:00Z' },
      ...extraFacts,
    ],
    prices: [{ model: 'gpt-4o-mini-2024-07-18', sku: 'GlobalStandard', inputPer1M: 0.15, outputPer1M: 0.6, source: 'Azure list price (gpt-4o-mini-0718)' }],
    warnings: [],
  };
}

// ---------------------------------------------------------------- wizard

test('every subscription starts ticked, can be filtered and cleared, and Next needs a selection', async () => {
  const p = portal({ [`/api/subscriptions?tenantId=t`]: [{ id: SUB1, name: 'Prod' }, { id: SUB2, name: 'Dev' }] });
  p.context.window.currentTenantId = 't';
  await p.context.loadSubscriptions();
  assert.equal(p.get('subList').options.length, 2);
  assert.equal(p.context.window.subSelection.size, 2);
  assert.equal(p.get('btnSubNext').disabled, false);
  p.get('btnSubNone').listeners.click();
  assert.equal(p.get('btnSubNext').disabled, true);
  p.get('subFilter').value = 'dev';
  p.get('subFilter').listeners.input();
  assert.equal(p.get('subList').options.length, 1);
  p.get('btnSubAll').listeners.click(); // "Select all" only affects what the filter shows
  assert.deepEqual([...p.context.window.subSelection], [SUB2]);
});

test('discovery preview summarises the hierarchy and flags projects without Application Insights', () => {
  const p = portal();
  p.context.window.tenantList = [{ tenantId: 't', displayName: 'Contoso' }];
  p.context.window.currentTenantId = 't';
  p.context.renderDiscovery(inventory());
  assert.match(p.get('discoverStatus').textContent, /1 have no linked Application Insights/);
  const cards = p.get('discoverSummary').options.map((c) => c.allText);
  assert.ok(cards.some((c) => /Foundry resources/.test(c) && /2/.test(c)));
  assert.ok(cards.some((c) => /Agents/.test(c) && /2/.test(c)));
  const tree = p.get('discoverTree').allText;
  assert.match(tree, /Tenant Contoso/);
  assert.match(tree, /1 of 2 resource group\(s\) contain Foundry/); // the empty RG is counted as scanned, not listed
  assert.match(tree, /Agents \(2\): Alpha, Sleepy/);
  assert.match(tree, /No linked Application Insights/);
  assert.equal(p.get('discoverWarnings').hidden, false);
});

test('discovery of nothing explains itself instead of showing an empty success', () => {
  const p = portal();
  p.context.renderDiscovery({ subscriptions: [{ id: SUB1, name: 'Prod' }], resourceGroups: [], accounts: [], appInsights: [], warnings: [] });
  assert.equal(p.get('discoverStatus').className, 'status err');
  assert.match(p.get('discoverStatus').textContent, /No Foundry resources were found/);
});

test('deploy request carries the scope, discovered components and explicit permission choices', () => {
  const p = portal({ '/api/plans': [], '/api/webapps?resourceGroup=host-rg': [] });
  p.context.window.subSelection = new Set([SUB1, SUB2]);
  p.context.window.discovery = inventory();
  p.context.window.currentTenantId = 't';
  p.context.window.tenantList = [{ tenantId: 't', displayName: 'Contoso' }];
  p.context.window.hostSubscriptionId = SUB1;
  p.context.window.selectedRg = 'host-rg';
  p.get('location').value = 'swedencentral';
  p.get('baseName').value = 'tokrpt';
  p.get('grantSubMon').checked = true;
  p.get('grantAgents').checked = false;
  p.get('runNow').checked = true;
  const req = JSON.parse(JSON.stringify(p.context.buildDeployRequest())); // normalise arrays created inside the vm realm
  assert.deepEqual(req.subscriptions, [SUB1, SUB2]);
  assert.equal(req.tenantName, 'Contoso');
  assert.equal(req.subscriptionId, SUB1);
  assert.equal(req.appInsightsIds.length, 1);
  assert.deepEqual(req.foundryProjectIds, [PROJECT_A, PROJECT_B]);
  assert.equal(req.grantSubscriptionMonitoringReader, true);
  assert.equal(req.grantAgentReadAccess, false);
  assert.equal(req.runNow, true);
});

test('privileged and broad permission options are off by default in the page', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', 'gui', 'wwwroot', 'index.html'), 'utf8');
  const tag = (id) => html.match(new RegExp(`<input[^>]*id="${id}"[^>]*>`))[0];
  assert.ok(!/\bchecked\b/.test(tag('grantAgents')), 'the Foundry User role (can list keys) must not be on by default');
  assert.ok(!/\bchecked\b/.test(tag('grantSubMon')), 'subscription-wide Monitoring Reader must not be on by default');
  assert.ok(/\bchecked\b/.test(tag('runNow')));
  assert.match(html, /can also list account keys and connection secrets/);
});

test('hosting subscription choices are only the selected subscriptions', async () => {
  const p = portal({ '/api/subscription': { ok: true }, '/api/resource-groups': [], '/api/locations': [] });
  p.context.window.allSubscriptions = [{ id: SUB1, name: 'Prod' }, { id: SUB2, name: 'Dev' }];
  p.context.window.subSelection = new Set([SUB2]);
  await p.context.enterHosting();
  assert.deepEqual(p.get('hostSubscription').options.map((o) => o.value), [SUB2]);
  assert.equal(p.requests.find((r) => r.url === '/api/subscription').body.subscriptionId, SUB2);
});

function planResources(plans = []) {
  return { '/api/plans': plans, '/api/webapps?resourceGroup=client-rg': [] };
}

test('new hosting resources default to the resource group region, not the first alphabetic location', async () => {
  const p = portal(planResources());
  p.context.window.selectedRg = 'client-rg';
  p.context.window.selectedRgLocation = 'centralindia';
  p.context.window.subSelection = new Set([SUB1]);
  await p.context.loadStep5Resources();
  assert.equal(p.get('location').value, 'centralindia');
  assert.equal(p.get('location').disabled, false);
});

test('an existing plan fixes the hosting region; a manual choice for a new plan is kept', async () => {
  const p = portal(planResources([{ name: 'plan', resourceGroup: 'client-rg', location: 'Central India', sku: 'F1' }]));
  p.context.window.selectedRg = 'client-rg';
  p.context.window.subSelection = new Set([SUB1]);
  await p.context.loadStep5Resources();
  assert.equal(p.get('location').value, 'centralindia');
  assert.equal(p.get('location').disabled, true);
  assert.match(p.get('locationHint').textContent, /not moved/);

  p.setMode('planMode', 'new');
  p.context.window.locationEdited = true;
  p.context.window.selectedRgLocation = 'southindia';
  const eastus = p.context.document.createElement('option');
  eastus.value = 'eastus';
  p.get('location').appendChild(eastus);
  p.get('location').value = 'eastus';
  p.context.syncDeploymentLocation();
  assert.equal(p.get('location').value, 'eastus');
});

test('prefers an existing plan in the resource group region over an unrelated first plan', async () => {
  const p = portal(planResources([
    { name: 'australia-plan', resourceGroup: 'other-rg', location: 'Australia East' },
    { name: 'india-plan', resourceGroup: 'client-rg', location: 'Central India' },
  ]));
  p.context.window.selectedRg = 'client-rg';
  p.context.window.selectedRgLocation = 'centralindia';
  p.context.window.subSelection = new Set([SUB1]);
  await p.context.loadStep5Resources();
  assert.equal(p.get('planExisting').value, 'india-plan');
});

// ---------------------------------------------------------------- dashboard model

test('periods bucket days into Monday weeks and months, including empty ones', () => {
  const p = portal();
  assert.equal(p.run("periodStart('2026-11-25','week')"), '2026-11-23');
  assert.equal(p.run("periodStart('2026-11-29','week')"), '2026-11-23'); // Sunday belongs to the week that began Monday
  assert.equal(p.run("periodStart('2026-11-30','week')"), '2026-11-30');
  assert.equal(p.run("periodStart('2026-11-25','month')"), '2026-11-01');
  assert.equal(p.run("periodLabel('2026-11-23','week')"), 'Week of 23 Nov');
  assert.equal(p.run("periodLabel('2026-11-01','month')"), 'Nov 2026');
  assert.deepEqual(JSON.parse(p.run("JSON.stringify(periodsBetween('2026-10-15','2026-12-03','month'))")), ['2026-10-01', '2026-11-01', '2026-12-01']);
  assert.equal(JSON.parse(p.run("JSON.stringify(periodsBetween('2026-11-23','2026-11-29','day'))")).length, 7);
});

test('the hierarchy rolls usage up through every level and keeps idle agents and unlinked projects visible', () => {
  const p = portal();
  p.context.window.report = report();
  const tree = JSON.parse(p.run(`(() => {
    const records = flatten(window.report);
    const root = buildTree(records, { tenantLabel: 'Tenant: Contoso' });
    const strip = (n) => ({ level: n.level, label: n.label, total: n.totals.total, calls: n.totals.calls, note: n.meta.note, idle: n.meta.idle, kids: n.children.map(strip) });
    return JSON.stringify(strip(root));
  })()`));
  assert.equal(tree.label, 'Tenant: Contoso');
  assert.equal(tree.total, 1650);
  const sub = tree.kids[0];
  assert.deepEqual([sub.level, sub.label, sub.total], ['Subscription', 'Prod', 1650]);
  const rg = sub.kids[0];
  assert.deepEqual([rg.level, rg.label], ['Resource group', 'rg1']);
  const [p1, p2] = rg.kids; // biggest consumer first
  assert.deepEqual([p1.label, p1.total], ['acct1 / p1', 1650]);
  assert.equal(p2.label, 'acct2 / p2');
  assert.equal(p2.note, 'no linked Application Insights');
  const [alpha, sleepy] = p1.kids;
  assert.deepEqual([alpha.level, alpha.label, alpha.total, alpha.calls], ['Agent', 'Alpha', 1650, 3]);
  assert.deepEqual([sleepy.label, sleepy.total, sleepy.idle], ['Sleepy', 0, true]);
  assert.deepEqual([alpha.kids[0].level, alpha.kids[0].label], ['Model', 'gpt-4o-mini-2024-07-18']);
});

test('filters narrow the records and idle rows disappear when a model is chosen or idle is hidden', () => {
  const p = portal();
  p.context.window.report = report();
  const count = (filter) => Number(p.run(`applyFilters(flatten(window.report), ${JSON.stringify(filter)}).length`));
  assert.equal(count({}), 4); // 2 usage rows + idle agent Sleepy + unlinked project p2
  assert.equal(count({ hideIdle: true }), 2);
  assert.equal(count({ model: 'gpt-4o-mini-2024-07-18' }), 2);
  assert.equal(count({ agent: 'Sleepy' }), 1);
  assert.equal(count({ projectId: PROJECT_B }), 1);
  assert.equal(count({ subscriptionId: SUB2 }), 0);
});

test('cost is a lower bound when some rows have no price, and reported cost wins over the estimate', () => {
  const p = portal();
  const fmt = (rows) => p.run(`fmtCost(totalsOf(${JSON.stringify(rows)}))`);
  const row = (o) => ({ kind: 'usage', total: 100, reported: null, estimated: null, ...o });
  assert.equal(fmt([row({ estimated: 0.5 })]), '$0.500000');
  assert.equal(fmt([row({ estimated: 0.5 }), row({})]), '≥ $0.500000');
  assert.equal(fmt([row({})]), 'n/a');
  assert.equal(fmt([]), '—');
  assert.equal(fmt([row({ reported: 2, estimated: 0.5 })]), '$2.000');
});

test('detail CSV groups by week, sums tokens, blanks partial cost and defuses spreadsheet formulas', () => {
  const p = portal();
  p.context.window.report = report([{
    day: '2026-11-24', scope: 0, agentId: 'x', agentName: '=HYPERLINK("http://evil")', model: 'm', deployment: 'm', sku: '', promptTokens: 10, completionTokens: 1,
    cachedTokens: 0, totalTokens: 11, calls: 1, errors: 0, durationMsSum: 5, costBasis: '',
  }]);
  const csv = p.run('detailCsv(flatten(window.report), "week")');
  const lines = csv.trim().split('\r\n');
  assert.equal(lines[0], 'TenantId,Tenant,SubscriptionId,Subscription,ResourceGroup,FoundryResource,FoundryProject,AppInsights,Agent,AgentId,Model,Deployment,PeriodStart,PromptTokens,CompletionTokens,CachedTokens,UncachedPromptTokens,ReasoningTokens,CacheWriteTokens,TotalTokens,CacheHitPercent,AvgTokensPerCall,Calls,Errors,AvgModelCallLatencyMs,AgentRuns,AvgAgentRunLatencyMs,ReportedCost,EstimatedCostUsd,CostBasis,FirstSeen,LastSeen,ProjectPortalUrl,AppInsightsPortalUrl');
  assert.equal(lines.length, 3); // header, Alpha (two days merged into one week), the unpriced agent
  const header = lines[0].split(',');
  const col = (line, name) => line.split(',')[header.indexOf(name)];
  const alpha = lines.find((l) => l.includes(',Alpha,'));
  assert.equal(col(alpha, 'PeriodStart'), '2026-11-23');
  assert.equal(col(alpha, 'TotalTokens'), '1650');          // across both days
  assert.equal(col(alpha, 'UncachedPromptTokens'), '1300'); // 1500 input − 200 cached
  assert.equal(col(alpha, 'CacheHitPercent'), '13.3');
  assert.equal(col(alpha, 'AvgTokensPerCall'), '550');
  assert.equal(col(alpha, 'Calls'), '3');
  assert.equal(col(alpha, 'AvgModelCallLatencyMs'), '533.3'); // weighted: 1600 ms / 3 calls
  assert.equal(col(alpha, 'AgentRuns'), '0');
  assert.equal(col(alpha, 'AvgAgentRunLatencyMs'), '');       // no run data => blank, never 0
  assert.equal(col(alpha, 'EstimatedCostUsd'), '0.00031');
  assert.match(csv, /"'=HYPERLINK\(""http:\/\/evil""\)"/);
  assert.ok(!lines.some((l) => l.includes('Sleepy')), 'idle agents are not usage rows');
  const unpriced = lines.find((l) => l.includes('HYPERLINK'));
  assert.equal(col(unpriced, 'EstimatedCostUsd'), ''); // no estimate => blank, never 0
});

test('grouped CSV exports every level with its path and share of the total', () => {
  const p = portal();
  p.context.window.report = report();
  const csv = p.run('(() => { const root = buildTree(flatten(window.report), { tenantLabel: "Tenant: Contoso" }); return groupedCsv(root, root.totals.total); })()');
  const lines = csv.trim().split('\r\n');
  assert.equal(lines[0].split(',')[0], 'Level');
  const levels = lines.slice(1).map((l) => l.split(',')[0]);
  assert.deepEqual([...new Set(levels)], ['Tenant', 'Subscription', 'Resource group', 'Foundry project', 'Agent', 'Model']);
  const model = lines.find((l) => l.startsWith('Model,'));
  const f = model.split(',');
  const header = lines[0].split(',');
  assert.deepEqual([f[2], f[3], f[4], f[5], f[6]], ['Tenant: Contoso', 'Prod', 'rg1', 'acct1 / p1', 'Alpha']);
  assert.equal(f[header.indexOf('SharePercent')], '100');
  assert.equal(f[header.indexOf('UncachedInputTokens')], '1300');
});

test('week breakdown adds period rows under each leaf, in chronological order', () => {
  const p = portal();
  p.context.window.report = report([{ day: '2026-11-30', scope: 0, agentId: 'a1', agentName: 'Alpha', model: 'gpt-4o-mini-2024-07-18', deployment: 'gpt-4o-mini', sku: '',
    promptTokens: 5, completionTokens: 5, cachedTokens: 0, totalTokens: 10, calls: 1, errors: 0, durationMsSum: 1, costBasis: '' }]);
  const periods = JSON.parse(p.run(`(() => {
    const root = buildTree(flatten(window.report), { byPeriod: true, grain: 'week' });
    const model = root.children[0].children[0].children[0].children[0].children[0];
    return JSON.stringify(model.children.map((c) => [c.level, c.label, c.totals.total]));
  })()`));
  assert.deepEqual(periods, [['Period', 'Week of 23 Nov', 1650], ['Period', 'Week of 30 Nov', 10]]);
});

// ---------------------------------------------------------------- dashboard behaviour

function dashboardPortal(responses) {
  const p = portal({ '/api/status': { account: { tenantId: 't' } }, '/api/tenants': { tenants: [{ tenantId: 't', displayName: 'Contoso' }] }, ...responses });
  p.context.window.currentTenantId = 't';
  p.run(`dash.allSubs = [{ id: '${SUB1}', name: 'Prod' }]; dash.scope = new Set(['${SUB1}']);`);
  p.get('dashSource').value = 'local';
  p.get('reportStartDate').value = '2026-11-23';
  p.get('reportEndDate').value = '2026-11-29';
  return p;
}

test('running the report sends the scope, dates and tenant and renders KPIs, chart, top agents and the tree', async () => {
  const p = dashboardPortal({ '/api/usage': report() });
  await p.context.runReport();
  const req = p.requests.find((r) => r.url === '/api/usage').body;
  assert.deepEqual(req.subscriptionIds, [SUB1]);
  assert.deepEqual([req.startDate, req.endDate, req.tenantId, req.tenantName], ['2026-11-23', '2026-11-29', 't', 'Contoso']);
  assert.equal(p.get('viewerStatus').className, 'status ok'); // no warnings
  assert.match(p.get('viewerStatus').textContent, /2 usage row\(s\) across 2 Foundry project\(s\)/);
  assert.match(p.get('kpiCards').allText, /1,650/);
  assert.match(p.get('topAgents').allText, /Alpha/);
  assert.match(p.get('topModels').allText, /gpt-4o-mini-2024-07-18/);
  const svg = p.get('trendChart').innerHTML;
  assert.equal((svg.match(/<polyline/g) || []).length, 5, 'one line per token measure (total, input, output, cached, uncached)');
  assert.equal((svg.match(/class="xlab"/g) || []).length, 8, 'one x label per day of the window (23–30 Nov)');
  assert.match(p.get('treeBody').allText, /Tenant: Contoso/);
  assert.match(p.get('treeBody').allText, /Prod/);
  assert.equal(p.get('btnExportDetail').disabled, false);
  assert.match(p.get('priceNotes').textContent, /Azure list price/);
});

test('a window with no usage says so plainly while still showing the discovered hierarchy', async () => {
  const empty = { ...report(), facts: [] };
  const p = dashboardPortal({ '/api/usage': empty });
  await p.context.runReport();
  assert.equal(p.get('viewerStatus').className, 'status err');
  assert.match(p.get('viewerStatus').textContent, /NO USAGE/);
  p.get('btnExpandAll').listeners.click(); // idle agents sit below the project level, which starts collapsed
  assert.match(p.get('treeBody').allText, /Sleepy/);   // the idle agent is still listed
  assert.equal(p.get('btnExportDetail').disabled, true);
});

test('a failed run shows the error and keeps the previous result', async () => {
  let fail = false;
  const p = dashboardPortal({ '/api/usage': () => (fail ? { error: 'Query access denied' } : report()) });
  await p.context.runReport();
  fail = true;
  await p.context.runReport();
  assert.equal(p.get('viewerStatus').className, 'status err');
  assert.match(p.get('viewerStatus').textContent, /Query access denied/);
  assert.match(p.get('kpiCards').allText, /1,650/);
  assert.equal(p.get('btnRunReport').disabled, false);
});

test('warnings are listed with their scope', async () => {
  const withWarnings = { ...report(), warnings: [{ scope: 'ai1', message: 'Could not query this Application Insights.' }] };
  const p = dashboardPortal({ '/api/usage': withWarnings });
  await p.context.runReport();
  assert.equal(p.get('warningsBox').hidden, false);
  assert.match(p.get('warningsTitle').textContent, /\(1\)/);
  assert.match(p.get('warningList').allText, /\[ai1\] Could not query/);
});

test('the deployed-app source calls the app proxy instead of using local credentials', async () => {
  const p = dashboardPortal({ '/api/app-usage': report() });
  p.get('dashSource').value = 'app';
  p.get('dashSource').options = [{ value: 'local' }, { value: 'app', selected: true }];
  await p.context.runReport();
  assert.ok(p.requests.some((r) => r.url === '/api/app-usage'));
  assert.ok(!p.requests.some((r) => r.url === '/api/usage'));
});

test('a run without any subscription in scope is refused before calling Azure', async () => {
  const p = dashboardPortal({});
  p.run('dash.scope = new Set()');
  await p.context.runReport();
  assert.match(p.get('viewerStatus').textContent, /at least one subscription/);
  assert.equal(p.requests.length, 0);
});

test('live mode runs today, never overlaps a run, retries after errors and stops cleanly', async () => {
  let resolve;
  const p = dashboardPortal({ '/api/usage': () => new Promise((r) => { resolve = r; }) });
  p.get('liveMetrics').checked = true;
  const first = p.context.runReport(true, 0);
  await new Promise((r) => setImmediate(r));
  await p.context.runReport(true, 0); // ignored: a run is in flight
  assert.equal(p.requests.filter((r) => r.url === '/api/usage').length, 1);
  p.context.stopLive();
  resolve(report());
  await first;
  assert.equal(p.run('dash.report'), null); // the reply that arrived after stopping must not render
  assert.equal(p.get('kpiCards').allText.includes('1,650'), false);
  assert.equal(p.get('btnRunReport').disabled, false);
});

test('live refresh uses today and waits for the chosen interval before the next run', async () => {
  const p = dashboardPortal({ '/api/usage': report() });
  p.get('liveMetrics').checked = true;
  p.get('liveEvery').value = '2';
  p.get('liveUnit').options = [{ value: '60000', selected: true }, { value: '3600000' }];
  await p.context.refreshLive(0);
  const today = new Date().toISOString().slice(0, 10);
  const req = p.requests.find((r) => r.url === '/api/usage').body;
  assert.deepEqual([req.startDate, req.endDate], [today, today]);
  const [timer] = [...p.timers.values()];
  assert.equal(timer.delay, 2 * 60000);
  assert.match(p.get('liveStatus').textContent, /Next check at .* \(every 2 minutes\)/);
  p.context.stopLive();
  assert.equal(p.timers.size, 0);
});

test('the live interval accepts minutes or hours, never goes below one minute or above a day, and falls back when unusable', () => {
  const p = portal();
  const interval = (every, unit) => {
    p.get('liveEvery').value = every;
    p.get('liveUnit').options = [{ value: unit, selected: true }];
    return p.run('liveIntervalMs()');
  };
  assert.equal(interval('5', '60000'), 5 * 60000);
  assert.equal(interval('2', '3600000'), 2 * 3600000);
  assert.equal(interval('0.4', '60000'), 60000);       // below the minimum is raised to one minute
  assert.equal(interval('999', '3600000'), 86400000);  // capped at 24 hours
  assert.equal(interval('abc', '60000'), 5 * 60000);   // unusable input falls back to the default
  assert.equal(p.run('describeInterval(3600000)'), '1 hour');
  assert.equal(p.run('describeInterval(120000)'), '2 minutes');
});

test('token measures: derived series, optional ones only when reported, and tidy axis maxima', () => {
  const p = portal();
  const t = { prompt: 1000, completion: 100, cached: 300, reasoning: 0, cacheWrite: 0, total: 1100 };
  assert.equal(p.run(`uncachedOf(${JSON.stringify(t)})`), 700);
  assert.equal(p.run('uncachedOf({ prompt: 10, cached: 50 })'), 0); // never negative
  const ids = (totals) => JSON.parse(p.run(`JSON.stringify(availableSeries(${JSON.stringify(totals)}).map((s) => s.id))`));
  assert.deepEqual(ids(t), ['total', 'prompt', 'completion', 'cached', 'uncached']);
  assert.deepEqual(ids({ ...t, reasoning: 40, cacheWrite: 5 }), ['total', 'prompt', 'completion', 'cached', 'uncached', 'reasoning', 'cacheWrite']);
  const nice = (v) => p.run(`niceMax(${v})`);
  assert.deepEqual([nice(0), nice(7), nice(1100), nice(22281), nice(2400)], [1, 10, 2000, 25000, 2500]);
  assert.equal(p.run('fmtCompact(22281)'), '22.3K');
  assert.equal(p.run('fmtMs(13893)'), '13.9 s');
  assert.equal(p.run('fmtMs(506.15)'), '506 ms');
});

test('the trend has one point per period with zeros for gaps and a value for every measure', () => {
  const p = portal();
  p.context.window.report = report();
  const trend = JSON.parse(p.run(`(() => {
    const t = trendSeries(flatten(window.report), 'day', '2026-11-22', '2026-11-25');
    return JSON.stringify({ periods: t.periods, total: t.values.total, prompt: t.values.prompt, cached: t.values.cached, uncached: t.values.uncached });
  })()`));
  assert.deepEqual(trend.periods, ['2026-11-22', '2026-11-23', '2026-11-24', '2026-11-25']);
  assert.deepEqual(trend.total, [0, 1100, 550, 0]);
  assert.deepEqual(trend.prompt, [0, 1000, 500, 0]);
  assert.deepEqual(trend.cached, [0, 200, 0, 0]);
  assert.deepEqual(trend.uncached, [0, 800, 500, 0]);
  const weekly = JSON.parse(p.run(`JSON.stringify(trendSeries(flatten(window.report), 'week', '2026-11-23', '2026-11-30').values.total)`));
  assert.deepEqual(weekly, [1650, 0]);
});

test('top five ranks by total tokens, ignores idle rows and flags unnamed usage', () => {
  const p = portal();
  const row = (agent, total, prompt = total) => ({ kind: 'usage', agent, total, prompt, completion: total - prompt, cached: 0 });
  const rows = [row('A', 10), row('B', 50), row('A', 15), row('C', 5), row('D', 1), row('E', 2), row('F', 3), row('', 40), { kind: 'idle', agent: 'Z', total: 0 }];
  const top = JSON.parse(p.run(`JSON.stringify(topN(${JSON.stringify(rows)}, (r) => r.agent, 5).map((e) => [e.name, e.totals.total, Math.round(e.share * 100)]))`));
  assert.deepEqual(top.map((e) => e[0]), ['B', '', 'A', 'C', 'F']);
  assert.deepEqual(top.map((e) => e[1]), [50, 40, 25, 5, 3]);
  assert.equal(top[0][2], 40); // share of all usage (50 of 126), not just of the five shown
});

test('clicking a top agent or model filters the whole dashboard; clicking again or Clear filters resets it', async () => {
  const other = { day: '2026-11-24', scope: 0, agentId: 'b1', agentName: 'Beta', model: 'gpt-4.1-mini-2025-04-14', deployment: 'gpt-4.1-mini', sku: '',
    promptTokens: 300, completionTokens: 30, cachedTokens: 0, totalTokens: 330, calls: 1, errors: 0, durationMsSum: 400, costBasis: '' };
  const p = dashboardPortal({ '/api/usage': report([other]) });
  await p.context.runReport();
  const totalCard = () => /1,980/.test(p.get('kpiCards').allText);
  assert.ok(totalCard(), '1,650 + 330 across both agents');
  const rowFor = (box, name) => p.get(box).options.find((r) => (r.allText || '').includes(name));

  rowFor('topAgents', 'Beta').listeners.click();
  assert.equal(p.run('dash.filters.agent'), 'Beta');
  assert.match(p.get('kpiCards').allText, /330/);
  assert.ok(!/1,980/.test(p.get('kpiCards').allText));
  assert.match(p.get('activeFilters').textContent, /Agent = /);
  assert.equal(p.get('btnClearFilters').disabled, false);
  // the agent list keeps showing every agent so the selection can be switched; the model list narrows to that agent
  assert.ok(rowFor('topAgents', 'Alpha'));
  assert.ok(!(p.get('topModels').allText.includes('gpt-4o-mini')));

  rowFor('topAgents', 'Beta').listeners.click(); // the selected entry toggles off
  assert.equal(p.run('dash.filters.agent'), '');
  assert.ok(totalCard());

  rowFor('topModels', 'gpt-4.1-mini').listeners.click();
  assert.equal(p.run('dash.filters.model'), 'gpt-4.1-mini-2025-04-14');
  p.get('btnClearFilters').listeners.click();
  assert.equal(p.run('JSON.stringify(Object.values(dash.filters).filter(Boolean))'), '[]');
  assert.equal(p.get('btnClearFilters').disabled, true);
  assert.ok(totalCard());
});

test('series can be hidden from the chart by clicking a token card, and shown again', async () => {
  const p = dashboardPortal({ '/api/usage': report() });
  await p.context.runReport();
  const lines = () => (p.get('trendChart').innerHTML.match(/<polyline/g) || []).length;
  assert.equal(lines(), 5);
  p.get('kpiCards').options.find((c) => c.dataset.series === 'cached').listeners.click();
  assert.equal(lines(), 4);
  assert.ok(p.get('kpiCards').options.find((c) => c.dataset.series === 'cached').classList.contains('off'));
  p.get('trendLegend').options.find((b) => b.allText.includes('Cached input')).listeners.click();
  assert.equal(lines(), 5);
});

test('latency shows the model call and the whole agent run separately', async () => {
  const withRuns = report();
  withRuns.facts = withRuns.facts.map((f) => ({ ...f, runs: f.calls, runDurationMsSum: f.durationMsSum * 10 }));
  const p = dashboardPortal({ '/api/usage': withRuns });
  await p.context.runReport();
  const ops = p.get('kpiOps').allText;
  assert.match(ops, /Avg model latency/);
  assert.match(ops, /533 ms/);    // 1600 ms over 3 model calls
  assert.match(ops, /Avg agent run/);
  assert.match(ops, /5,333 ms/);  // 16000 ms over 3 runs
});

function collectLinks(node, found = []) {
  if (node.href) found.push(node);
  (node.options || []).forEach((c) => collectLinks(c, found));
  return found;
}

test('Azure portal links render only for genuine portal resource URLs', async () => {
  const good = dashboardPortal({ '/api/usage': report() });
  await good.context.runReport();
  good.get('btnExpandAll').listeners.click();
  const links = collectLinks(good.get('treeBody'));
  assert.ok(links.length >= 1);
  assert.ok(links.every((a) => a.href.startsWith('https://portal.azure.com/#resource/subscriptions/') && a.rel === 'noopener noreferrer' && a.target === '_blank'));

  // Telemetry-supplied text must never become a link: only the usage row's URL is in play here.
  const evil = { ...report(), scopes: [{ ...report().scopes[0], portalUrl: 'javascript:alert(1)' }], inventory: { ...inventory(), accounts: [] } };
  const bad = dashboardPortal({ '/api/usage': evil });
  await bad.context.runReport();
  bad.get('btnExpandAll').listeners.click();
  assert.equal(collectLinks(bad.get('treeBody')).length, 0);
});

test('the tree expands and collapses and the exports download the filtered data', async () => {
  const p = dashboardPortal({ '/api/usage': report() });
  await p.context.runReport();
  const downloads = [];
  p.context.download = (name, text) => downloads.push({ name, text });
  p.get('btnExportDetail').listeners.click();
  p.get('btnExportGrouped').listeners.click();
  assert.match(downloads[0].name, /detail-day_2026-11-23_to_2026-11-29\.csv$/);
  assert.match(downloads[1].name, /grouped_/);
  assert.match(downloads[1].text, /^Level,Name,Tenant/);

  const rowsBefore = p.get('treeBody').options.length;
  p.get('btnExpandAll').listeners.click();
  assert.ok(p.get('treeBody').options.length > rowsBefore, 'expand all reveals deeper levels');
  p.get('btnCollapseAll').listeners.click();
  assert.equal(p.get('treeBody').options.length, 2, 'collapse leaves the tenant row and its subscriptions so you can navigate back down');
});

test('changing a filter re-renders from the loaded report without calling Azure again', async () => {
  const p = dashboardPortal({ '/api/usage': report() });
  await p.context.runReport();
  const calls = p.requests.length;
  p.get('fAgent').value = 'Alpha';
  p.get('fAgent').listeners.change();
  assert.equal(p.requests.length, calls);
  assert.match(p.get('kpiCards').allText, /1,650/);
  p.get('hideIdle').checked = true;
  p.get('hideIdle').listeners.change();
  assert.ok(!p.get('treeBody').allText.includes('Sleepy'));
});
