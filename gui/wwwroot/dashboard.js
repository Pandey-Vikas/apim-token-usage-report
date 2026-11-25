'use strict';

// ============================================================================
// Pure data helpers (no DOM): flatten the report, filter, group, total.
// ============================================================================
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const PORTAL_LINK = /^https:\/\/portal\.azure\.com\/#resource\/subscriptions\/[0-9a-fA-F-]{36}\/resourceGroups\/[^\s"<>]+$/;
const ARM_ID = /^\/subscriptions\/[0-9a-fA-F-]{36}\/resourceGroups\/[A-Za-z0-9._()-]+\/providers\/[A-Za-z0-9._()/-]+$/;

function periodStart(day, grain) {
  const d = new Date(`${day}T00:00:00Z`);
  if (grain === 'week') d.setUTCDate(d.getUTCDate() - ((d.getUTCDay() + 6) % 7)); // weeks start on Monday
  else if (grain === 'month') d.setUTCDate(1);
  return d.toISOString().slice(0, 10);
}

function periodLabel(start, grain) {
  const d = new Date(`${start}T00:00:00Z`);
  if (grain === 'month') return `${MONTHS[d.getUTCMonth()]} ${d.getUTCFullYear()}`;
  const text = `${d.getUTCDate()} ${MONTHS[d.getUTCMonth()]}`;
  return grain === 'week' ? `Week of ${text}` : text;
}

// Every period that touches [startDay, endDay], so the chart shows gaps as zero instead of hiding them.
function periodsBetween(startDay, endDay, grain) {
  const out = [];
  let cur = periodStart(startDay, grain);
  const last = periodStart(endDay, grain);
  for (let guard = 0; cur <= last && guard < 1000; guard++) {
    out.push(cur);
    const d = new Date(`${cur}T00:00:00Z`);
    if (grain === 'month') d.setUTCMonth(d.getUTCMonth() + 1);
    else d.setUTCDate(d.getUTCDate() + (grain === 'week' ? 7 : 1));
    cur = d.toISOString().slice(0, 10);
  }
  return out;
}

const costOf = (r) => (r.reported ?? r.estimated ?? null);

function newTotals() {
  return { prompt: 0, completion: 0, cached: 0, total: 0, calls: 0, errors: 0, durationMs: 0, cost: 0, priced: 0, unpriced: 0, lastSeen: '' };
}

function addTo(t, r) {
  t.prompt += r.prompt || 0;
  t.completion += r.completion || 0;
  t.cached += r.cached || 0;
  t.total += r.total || 0;
  t.calls += r.calls || 0;
  t.errors += r.errors || 0;
  t.durationMs += r.durationMs || 0;
  if (r.kind === 'usage') {
    const c = costOf(r);
    if (c === null || c === undefined) { if ((r.total || 0) > 0) t.unpriced++; } else { t.cost += c; t.priced++; }
  }
  if (r.lastSeen && r.lastSeen > t.lastSeen) t.lastSeen = r.lastSeen;
}

function totalsOf(records) {
  const t = newTotals();
  records.forEach((r) => addTo(t, r));
  return t;
}

function portalUrlFor(id) {
  return ARM_ID.test(id || '') ? `https://portal.azure.com/#resource${id}/overview` : '';
}

const sameText = (a, b) => (a || '').toLowerCase() === (b || '').toLowerCase();

// One record per (day, project, agent, model) of real usage, plus zero-usage records for every discovered project and
// agent so idle agents and unlinked projects stay visible in the hierarchy.
function flatten(report) {
  const tenant = report.tenant || {};
  const scopes = report.scopes || [];
  const tenantName = tenant.name || tenant.id || '';
  const records = [];
  const zero = { prompt: 0, completion: 0, cached: 0, total: 0, calls: 0, errors: 0, durationMs: 0, reported: null, estimated: null };
  for (const f of report.facts || []) {
    const s = scopes[f.scope] || {};
    records.push({
      kind: 'usage', day: f.day, tenantId: tenant.id || '', tenant: tenantName,
      subscriptionId: s.subscriptionId || '', subscription: s.subscriptionName || s.subscriptionId || '',
      resourceGroup: s.resourceGroup || '', account: s.accountName || '', project: s.projectName || '', projectId: s.projectId || '',
      appInsights: s.appInsightsName || '', portalUrl: s.portalUrl || '', appInsightsPortalUrl: s.appInsightsPortalUrl || '',
      agent: f.agentName || f.agentId || '', agentId: f.agentId || '', model: f.model || '', deployment: f.deployment || '', sku: f.sku || '',
      prompt: f.promptTokens, completion: f.completionTokens, cached: f.cachedTokens, total: f.totalTokens, calls: f.calls,
      errors: f.errors, durationMs: f.durationMsSum, reported: f.reportedCost ?? null, estimated: f.estimatedCost ?? null,
      costBasis: f.costBasis || '', firstSeen: f.firstSeen || '', lastSeen: f.lastSeen || '',
    });
  }
  const inv = report.inventory || {};
  const subNames = new Map((inv.subscriptions || []).map((s) => [s.id.toLowerCase(), s.name]));
  const insightNames = new Map((inv.appInsights || []).map((a) => [a.id.toLowerCase(), a.name]));
  const usedAgents = new Set(records.map((r) => `${r.projectId.toLowerCase()}|${r.agent.toLowerCase()}`));
  const usedProjects = new Set(records.map((r) => r.projectId.toLowerCase()));
  for (const a of inv.accounts || []) {
    for (const p of a.projects || []) {
      const links = (p.appInsightsIds || []).map((id) => insightNames.get(id.toLowerCase()) || id.split('/').pop());
      const base = {
        ...zero, kind: 'idle', day: '', tenantId: tenant.id || '', tenant: tenantName,
        subscriptionId: a.subscriptionId, subscription: subNames.get(a.subscriptionId.toLowerCase()) || a.subscriptionId,
        resourceGroup: a.resourceGroup, account: a.name, project: p.name, projectId: p.id, appInsights: links.join(', '),
        portalUrl: portalUrlFor(p.id), appInsightsPortalUrl: '', model: '', deployment: '', sku: '', agentId: '', costBasis: '', firstSeen: '', lastSeen: '',
      };
      const agents = p.agents || [];
      for (const ag of agents) {
        if (!usedAgents.has(`${p.id.toLowerCase()}|${ag.name.toLowerCase()}`)) records.push({ ...base, agent: ag.name, agentId: ag.id, note: '' });
      }
      if (!usedProjects.has(p.id.toLowerCase()) && agents.length === 0) {
        records.push({ ...base, agent: '', note: links.length ? 'no usage' : 'no linked Application Insights' });
      }
    }
  }
  return records;
}

function applyFilters(records, f) {
  return records.filter((r) =>
    (!f.subscriptionId || r.subscriptionId === f.subscriptionId)
    && (!f.resourceGroup || r.resourceGroup === f.resourceGroup)
    && (!f.projectId || r.projectId === f.projectId)
    && (!f.agent || r.agent === f.agent)
    && (!f.model || r.model === f.model)
    && !(f.hideIdle && r.kind === 'idle'));
}

const LEVELS = [
  { name: 'Subscription', key: (r) => r.subscriptionId, label: (r) => r.subscription || r.subscriptionId },
  { name: 'Resource group', key: (r) => r.resourceGroup, label: (r) => r.resourceGroup },
  { name: 'Foundry project', key: (r) => r.projectId || `${r.account}/${r.project}`, label: (r) => `${r.account} / ${r.project}` },
  { name: 'Agent', key: (r) => r.agent || '(unattributed)', label: (r) => r.agent || '(unattributed)' },
  { name: 'Model', key: (r) => r.model, label: (r) => r.model },
];

function newNode(id, level, label) {
  return { id, level, label, totals: newTotals(), children: [], index: new Map(), meta: {} };
}

// Tenant > subscription > resource group > Foundry project > agent > model [> period], with totals rolled up at every level.
function buildTree(records, opts = {}) {
  const root = newNode('root', 'Tenant', opts.tenantLabel || 'Tenant');
  for (const r of records) {
    let node = root;
    addTo(root.totals, r);
    for (const lv of LEVELS) {
      const key = lv.key(r);
      if (lv.name === 'Model' && !key) break; // an idle agent has no model row
      let child = node.index.get(key);
      if (!child) {
        child = newNode(`${node.id}/${key}`, lv.name, lv.label(r));
        node.index.set(key, child);
        node.children.push(child);
      }
      addTo(child.totals, r);
      if (lv.name === 'Foundry project') {
        child.meta.portalUrl = child.meta.portalUrl || r.portalUrl;
        child.meta.appInsights = child.meta.appInsights || r.appInsights;
        if (r.note) child.meta.note = r.note;
      }
      if (r.kind === 'idle' && lv.name === 'Agent') child.meta.idle = true;
      if (r.kind === 'usage') child.meta.idle = false;
      node = child;
    }
    if (opts.byPeriod && r.kind === 'usage') {
      const start = periodStart(r.day, opts.grain || 'day');
      let p = node.index.get(`period:${start}`);
      if (!p) {
        p = newNode(`${node.id}/period:${start}`, 'Period', periodLabel(start, opts.grain || 'day'));
        p.meta.start = start;
        node.index.set(`period:${start}`, p);
        node.children.push(p);
      }
      addTo(p.totals, r);
    }
  }
  const sortRec = (n) => {
    n.children.sort((a, b) => {
      const ap = a.level === 'Period';
      const bp = b.level === 'Period';
      if (ap && bp) return a.meta.start < b.meta.start ? -1 : 1; // periods read chronologically
      if (ap !== bp) return ap ? 1 : -1;
      return (b.totals.total - a.totals.total) || a.label.localeCompare(b.label); // biggest consumers first
    });
    n.children.forEach(sortRec);
  };
  sortRec(root);
  return root;
}

const CSV_HEADERS = ['TenantId', 'Tenant', 'SubscriptionId', 'Subscription', 'ResourceGroup', 'FoundryResource', 'FoundryProject', 'AppInsights',
  'Agent', 'AgentId', 'Model', 'Deployment', 'PeriodStart', 'PromptTokens', 'CompletionTokens', 'CachedTokens', 'TotalTokens', 'Calls', 'Errors',
  'AvgLatencyMs', 'ReportedCost', 'EstimatedCostUsd', 'CostBasis', 'FirstSeen', 'LastSeen', 'ProjectPortalUrl', 'AppInsightsPortalUrl'];

// Quotes a value and defuses spreadsheet formulas: agent and model names come from telemetry.
function csvCell(value) {
  let s = value === null || value === undefined ? '' : String(value);
  if (s.length && '=+-@\t\r'.includes(s[0])) s = `'${s}`;
  return /[",\n\r]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

// A partial sum would understate cost, so a group with any unpriced row has no cost value.
function costForCsv(t) {
  return t.priced > 0 && t.unpriced === 0 ? String(Number(t.cost.toFixed(6))) : '';
}

// Flat rows for Power BI at the chosen grain; mirrors the app's /api/usage.csv.
function detailCsv(records, grain) {
  const groups = new Map();
  records.filter((r) => r.kind === 'usage').forEach((r) => {
    const period = periodStart(r.day, grain);
    const key = [r.projectId, r.appInsights, r.agent, r.agentId, r.model, r.deployment, period].join('|');
    let g = groups.get(key);
    if (!g) { g = { r, period, rows: [], totals: newTotals(), reported: [], first: '' }; groups.set(key, g); }
    g.rows.push(r);
    addTo(g.totals, r);
    if (r.firstSeen && (!g.first || r.firstSeen < g.first)) g.first = r.firstSeen;
  });
  const lines = [CSV_HEADERS.join(',')];
  [...groups.values()].sort((a, b) => (a.period < b.period ? -1 : a.period > b.period ? 1 : 0)).forEach((g) => {
    const r = g.r;
    const t = g.totals;
    const allReported = g.rows.every((x) => x.reported !== null && x.reported !== undefined);
    const bases = [...new Set(g.rows.map((x) => x.costBasis))];
    lines.push([
      r.tenantId, r.tenant, r.subscriptionId, r.subscription, r.resourceGroup, r.account, r.project, r.appInsights, r.agent, r.agentId, r.model, r.deployment,
      g.period, t.prompt, t.completion, t.cached, t.total, t.calls, t.errors, t.calls && t.durationMs ? Number((t.durationMs / t.calls).toFixed(1)) : '',
      allReported ? String(Number(g.rows.reduce((s, x) => s + x.reported, 0).toFixed(6))) : '',
      g.rows.every((x) => x.estimated !== null && x.estimated !== undefined) ? String(Number(g.rows.reduce((s, x) => s + x.estimated, 0).toFixed(6))) : '',
      bases.length === 1 ? bases[0] : 'mixed', g.first, t.lastSeen, r.portalUrl, r.appInsightsPortalUrl,
    ].map(csvCell).join(','));
  });
  return `${lines.join('\r\n')}\r\n`;
}

// The visible hierarchy as rows (every level, fully expanded) with the path in separate columns.
function groupedCsv(root, grandTotal) {
  const header = ['Level', 'Name', 'Tenant', 'Subscription', 'ResourceGroup', 'FoundryProject', 'Agent', 'Model', 'Period', 'InputTokens', 'OutputTokens',
    'CachedTokens', 'TotalTokens', 'Calls', 'Errors', 'AvgLatencyMs', 'EstimatedCostUsd', 'SharePercent', 'LastActivity'];
  const lines = [header.join(',')];
  const walk = (n, ctx) => {
    const next = { ...ctx };
    if (n.level === 'Tenant') next.Tenant = n.label;
    else if (n.level === 'Subscription') next.Subscription = n.label;
    else if (n.level === 'Resource group') next.ResourceGroup = n.label;
    else if (n.level === 'Foundry project') next.FoundryProject = n.label;
    else if (n.level === 'Agent') next.Agent = n.label;
    else if (n.level === 'Model') next.Model = n.label;
    else if (n.level === 'Period') next.Period = n.meta.start;
    const t = n.totals;
    lines.push([
      n.level, n.label, next.Tenant, next.Subscription, next.ResourceGroup, next.FoundryProject, next.Agent, next.Model, next.Period,
      t.prompt, t.completion, t.cached, t.total, t.calls, t.errors, t.calls && t.durationMs ? Number((t.durationMs / t.calls).toFixed(1)) : '',
      costForCsv(t), grandTotal ? Number(((t.total / grandTotal) * 100).toFixed(2)) : '', t.lastSeen,
    ].map(csvCell).join(','));
    n.children.forEach((c) => walk(c, next));
  };
  walk(root, {});
  return `${lines.join('\r\n')}\r\n`;
}

// ============================================================================
// Formatting
// ============================================================================
const fmtInt = (n) => Math.round(n || 0).toLocaleString('en-US');

function fmtCost(t) {
  if (t.priced === 0) return t.unpriced > 0 ? 'n/a' : '—';
  const c = t.cost;
  const s = c >= 100 ? c.toFixed(2) : c >= 1 ? c.toFixed(3) : c.toFixed(6);
  return `${t.unpriced > 0 ? '≥ ' : ''}$${s}`;
}

const fmtPct = (x) => (Number.isFinite(x) ? `${(x * 100).toFixed(x >= 0.1 || x === 0 ? 1 : 2)}%` : '—');
const fmtLatency = (t) => (t.calls && t.durationMs ? `${Math.round(t.durationMs / t.calls).toLocaleString('en-US')} ms` : '—');
const fmtLast = (s) => (s ? s.replace('T', ' ').slice(0, 16) : '—');

// ============================================================================
// Dashboard state and rendering
// ============================================================================
const dash = {
  report: null, records: [], grain: 'day', expanded: new Set(), scope: new Set(), allSubs: [],
  tenantId: '', tenantName: '', account: '', filters: { subscriptionId: '', resourceGroup: '', projectId: '', agent: '', model: '', hideIdle: false }, byPeriod: false,
};
let liveTimer = null;
let liveGeneration = 0;
let reportRunning = false;

const iso = (d) => d.toISOString().slice(0, 10);
const utcDate = (y, m, d) => new Date(Date.UTC(y, m, d));

function updateRangeSummary() {
  const s = $('reportStartDate').value;
  const e = $('reportEndDate').value;
  const box = $('rangeSummary');
  if (!s || !e) { box.textContent = 'Pick a start and end date, or use a quick range.'; return; }
  if (s > e) { box.textContent = 'Start date must be on or before the end date.'; return; }
  const days = Math.round((Date.parse(e) - Date.parse(s)) / 86400000) + 1;
  box.textContent = `Selected: ${s} → ${e} (${days} day${days === 1 ? '' : 's'}, UTC)`;
}

function applyRange(kind) {
  const now = new Date();
  const today = utcDate(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  const yesterday = utcDate(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - 1);
  const y = today.getUTCFullYear(), m = today.getUTCMonth(), d = today.getUTCDate();
  let start, end;
  if (kind === 'yesterday') { start = end = yesterday; }
  else if (kind === 'today') { start = end = today; }
  else if (kind === 'thisMonth') { start = utcDate(y, m, 1); end = today; }
  else if (kind === 'lastMonth') { start = utcDate(y, m - 1, 1); end = utcDate(y, m, 0); }
  else { start = utcDate(y, m, d - (Number(kind) - 1)); end = today; }
  $('reportEndDate').max = iso(today);
  $('reportStartDate').max = iso(today);
  $('reportStartDate').value = iso(start);
  $('reportEndDate').value = iso(end);
  document.querySelectorAll('#rangeChips .chip').forEach((c) => c.classList.toggle('active', c.dataset.range === kind));
  updateRangeSummary();
}

document.querySelectorAll('#rangeChips .chip').forEach((c) => c.addEventListener('click', () => applyRange(c.dataset.range)));
['reportStartDate', 'reportEndDate'].forEach((id) => $(id).addEventListener('input', () => {
  document.querySelectorAll('#rangeChips .chip').forEach((c) => c.classList.remove('active'));
  updateRangeSummary();
}));

function setGrain(grain) {
  dash.grain = grain;
  document.querySelectorAll('#grainButtons .chip').forEach((b) => b.classList.toggle('active', b.dataset.grain === grain));
  if (dash.report) renderAll();
}
document.querySelectorAll('#grainButtons .chip').forEach((b) => b.addEventListener('click', () => setGrain(b.dataset.grain)));

function distinct(records, keyFn, labelFn) {
  const m = new Map();
  records.forEach((r) => { const k = keyFn(r); if (k && !m.has(k)) m.set(k, labelFn(r)); });
  return [...m.entries()].sort((a, b) => a[1].localeCompare(b[1]));
}

function fillFilter(id, entries, current, allLabel) {
  const sel = $(id);
  sel.innerHTML = '';
  const add = (value, text) => { const o = document.createElement('option'); o.value = value; o.textContent = text; sel.appendChild(o); };
  add('', allLabel);
  entries.forEach(([value, text]) => add(value, text));
  sel.value = entries.some(([v]) => v === current) ? current : '';
  return sel.value;
}

function refreshFilters() {
  const rs = dash.records;
  const f = dash.filters;
  f.subscriptionId = fillFilter('fSubscription', distinct(rs, (r) => r.subscriptionId, (r) => r.subscription), f.subscriptionId, 'All subscriptions');
  f.resourceGroup = fillFilter('fResourceGroup', distinct(rs, (r) => r.resourceGroup, (r) => r.resourceGroup), f.resourceGroup, 'All resource groups');
  f.projectId = fillFilter('fProject', distinct(rs, (r) => r.projectId, (r) => `${r.account} / ${r.project}`), f.projectId, 'All Foundry projects');
  f.agent = fillFilter('fAgent', distinct(rs, (r) => r.agent, (r) => r.agent), f.agent, 'All agents');
  f.model = fillFilter('fModel', distinct(rs, (r) => r.model, (r) => r.model), f.model, 'All models');
}

const FILTER_FIELDS = { fSubscription: 'subscriptionId', fResourceGroup: 'resourceGroup', fProject: 'projectId', fAgent: 'agent', fModel: 'model' };
Object.entries(FILTER_FIELDS).forEach(([id, field]) => $(id).addEventListener('change', () => { dash.filters[field] = $(id).value; renderAll(false); }));
$('hideIdle').addEventListener('change', () => { dash.filters.hideIdle = $('hideIdle').checked; renderAll(false); });
$('showPeriods').addEventListener('change', () => { dash.byPeriod = $('showPeriods').checked; renderAll(false); });

function kpi(label, value, sub) {
  const c = el('div', 'kpi');
  c.appendChild(el('div', 'kpi-v', value));
  c.appendChild(el('div', 'kpi-k', label));
  if (sub) c.appendChild(el('div', 'kpi-s', sub));
  return c;
}

function renderKpis(records, t) {
  const usage = records.filter((r) => r.kind === 'usage' && r.total > 0);
  const active = new Set(usage.map((r) => r.agent || '(unattributed)')).size;
  const known = new Set(records.filter((r) => r.agent || r.kind === 'usage').map((r) => r.agent || '(unattributed)')).size;
  const box = $('kpiCards');
  box.innerHTML = '';
  [
    kpi('Total tokens', fmtInt(t.total)),
    kpi('Input tokens', fmtInt(t.prompt)),
    kpi('Output tokens', fmtInt(t.completion)),
    kpi('Cached input', t.prompt ? fmtPct(t.cached / t.prompt) : '—', `${fmtInt(t.cached)} tokens`),
    kpi('Model calls', fmtInt(t.calls)),
    kpi('Est. cost (USD)', fmtCost(t), t.unpriced > 0 ? `${t.unpriced} row(s) unpriced` : 'list price or reported'),
    kpi('Agents', `${active} / ${known}`, 'active / known'),
    kpi('Error rate', t.calls ? fmtPct(t.errors / t.calls) : '—', `${fmtInt(t.errors)} failed`),
    kpi('Avg latency', fmtLatency(t)),
  ].forEach((c) => box.appendChild(c));
}

function renderTrend(records) {
  const grain = dash.grain;
  const usage = records.filter((r) => r.kind === 'usage');
  const days = usage.map((r) => r.day).sort();
  const start = dash.report?.windowStartUtc ? String(dash.report.windowStartUtc).slice(0, 10) : days[0];
  const end = dash.report?.windowEndUtc ? String(dash.report.windowEndUtc).slice(0, 10) : days[days.length - 1];
  $('trendTitle').textContent = `Tokens by ${grain}`;
  const chart = $('trendChart');
  chart.innerHTML = '';
  if (!start || !end) { chart.appendChild(el('div', 'hint', 'No usage in this window.')); return; }
  const buckets = new Map(periodsBetween(start, end, grain).map((p) => [p, { in: 0, out: 0, calls: 0, cost: newTotals() }]));
  usage.forEach((r) => {
    const b = buckets.get(periodStart(r.day, grain));
    if (!b) return;
    b.in += r.prompt; b.out += r.completion; b.calls += r.calls; addTo(b.cost, r);
  });
  const max = Math.max(1, ...[...buckets.values()].map((b) => b.in + b.out));
  const labelEvery = Math.ceil(buckets.size / 12);
  let i = 0;
  buckets.forEach((b, key) => {
    const col = el('div', 'bar-col');
    col.title = `${periodLabel(key, grain)}: ${fmtInt(b.in + b.out)} tokens (${fmtInt(b.in)} in, ${fmtInt(b.out)} out), ${fmtInt(b.calls)} calls, ${fmtCost(b.cost)}`;
    const stack = el('div', 'bar-stack');
    const out = el('div', 'bar out');
    out.style.height = `${(b.out / max) * 100}%`;
    const inp = el('div', 'bar in');
    inp.style.height = `${(b.in / max) * 100}%`;
    stack.appendChild(out);
    stack.appendChild(inp);
    col.appendChild(stack);
    col.appendChild(el('div', 'bar-label', i % labelEvery === 0 ? periodLabel(key, grain) : ''));
    chart.appendChild(col);
    i++;
  });
}

function renderTopAgents(records) {
  const byAgent = new Map();
  records.filter((r) => r.kind === 'usage').forEach((r) => {
    const k = r.agent || '(unattributed)';
    byAgent.set(k, (byAgent.get(k) || 0) + r.total);
  });
  const top = [...byAgent.entries()].sort((a, b) => b[1] - a[1]).slice(0, 5);
  const box = $('topAgents');
  box.innerHTML = '';
  if (top.length === 0) { box.appendChild(el('div', 'hint', 'No usage in this window.')); return; }
  const max = top[0][1] || 1;
  top.forEach(([name, total]) => {
    const row = el('div', 'top-row');
    row.appendChild(el('div', 'top-name', name));
    const bar = el('div', 'top-bar');
    const fill = el('div', 'top-fill');
    fill.style.width = `${(total / max) * 100}%`;
    bar.appendChild(fill);
    row.appendChild(bar);
    row.appendChild(el('div', 'top-val', fmtInt(total)));
    box.appendChild(row);
  });
}

function renderPeriodTable(records) {
  const grain = dash.grain;
  const byPeriod = new Map();
  records.filter((r) => r.kind === 'usage').forEach((r) => {
    const k = periodStart(r.day, grain);
    if (!byPeriod.has(k)) byPeriod.set(k, newTotals());
    addTo(byPeriod.get(k), r);
  });
  const keys = [...byPeriod.keys()].sort();
  $('periodHint').textContent = `— by ${grain}`;
  const body = $('periodBody');
  body.innerHTML = '';
  keys.slice().reverse().forEach((k) => {
    const t = byPeriod.get(k);
    const prev = byPeriod.get(keys[keys.indexOf(k) - 1]);
    const tr = el('tr');
    const cell = (text, cls) => { const td = el('td', cls, text); tr.appendChild(td); };
    cell(periodLabel(k, grain));
    cell(fmtInt(t.total), 'num');
    cell(fmtInt(t.calls), 'num');
    cell(fmtCost(t), 'num');
    cell(prev && prev.total > 0 ? `${t.total >= prev.total ? '+' : ''}${(((t.total - prev.total) / prev.total) * 100).toFixed(1)}%` : '—', 'num');
    body.appendChild(tr);
  });
  if (keys.length === 0) { const tr = el('tr'); const td = el('td', '', 'No usage in this window.'); td.colSpan = 5; tr.appendChild(td); body.appendChild(tr); }
}

function defaultExpanded(root) {
  const set = new Set([root.id]);
  root.children.forEach((c) => set.add(c.id)); // tenant and subscriptions open; deeper levels on demand
  return set;
}

function renderTree(root) {
  const body = $('treeBody');
  body.innerHTML = '';
  const grand = root.totals.total || 0;
  const walk = (node, depth) => {
    const tr = el('tr', `lvl lvl-${node.level.replace(/\s+/g, '-').toLowerCase()}`);
    const first = el('td', 'name');
    first.style.paddingLeft = `${depth * 18 + 8}px`;
    const hasKids = node.children.length > 0;
    if (hasKids) {
      const open = dash.expanded.has(node.id);
      const tgl = el('button', 'toggle', open ? '▾' : '▸');
      tgl.type = 'button';
      tgl.addEventListener('click', () => {
        if (dash.expanded.has(node.id)) dash.expanded.delete(node.id); else dash.expanded.add(node.id);
        renderTree(root);
      });
      first.appendChild(tgl);
    } else first.appendChild(el('span', 'toggle-gap', ''));
    first.appendChild(el('span', 'lvl-name', node.label));
    first.appendChild(el('span', 'lvl-tag', node.level));
    if (node.meta.idle || (node.totals.total === 0 && node.level === 'Agent')) first.appendChild(el('span', 'badge idle', 'no usage'));
    if (node.meta.note) first.appendChild(el('span', 'badge warn', node.meta.note));
    if (node.meta.appInsights) first.appendChild(el('span', 'muted', ` · App Insights: ${node.meta.appInsights}`));
    if (node.meta.portalUrl && PORTAL_LINK.test(node.meta.portalUrl)) {
      const a = el('a', 'portal-link', ' ↗ Azure portal');
      a.href = node.meta.portalUrl; a.target = '_blank'; a.rel = 'noopener noreferrer';
      first.appendChild(a);
    }
    tr.appendChild(first);
    const t = node.totals;
    const num = (text, cls = 'num') => tr.appendChild(el('td', cls, text));
    num(fmtInt(t.prompt)); num(fmtInt(t.completion)); num(fmtInt(t.cached)); num(fmtInt(t.total)); num(fmtInt(t.calls));
    const costCell = el('td', 'num', fmtCost(t));
    if (t.unpriced > 0) costCell.title = `${t.unpriced} row(s) have no price (model has no unambiguous Azure list price); cost is a lower bound.`;
    tr.appendChild(costCell);
    const shareCell = el('td', 'num');
    shareCell.appendChild(el('span', '', grand ? fmtPct(t.total / grand) : '—'));
    const bar = el('div', 'share-bar');
    const fill = el('div', 'share-fill');
    fill.style.width = `${grand ? (t.total / grand) * 100 : 0}%`;
    bar.appendChild(fill);
    shareCell.appendChild(bar);
    tr.appendChild(shareCell);
    num(fmtLatency(t)); num(t.calls ? fmtPct(t.errors / t.calls) : '—'); num(fmtLast(t.lastSeen), 'when');
    body.appendChild(tr);
    if (hasKids && dash.expanded.has(node.id)) node.children.forEach((c) => walk(c, depth + 1));
  };
  walk(root, 0);
  dash.tree = root;
}

function renderWarnings(report) {
  const warnings = report.warnings || [];
  $('warningsBox').classList.toggle('hidden', warnings.length === 0);
  $('warningsTitle').textContent = `Warnings (${warnings.length})`;
  const list = $('warningList');
  list.innerHTML = '';
  warnings.forEach((w) => list.appendChild(el('li', '', `[${w.scope}] ${w.message}`)));
}

function renderPriceNotes(report) {
  const prices = report.prices || [];
  const text = prices.length
    ? `Estimated cost uses Azure list prices in USD (Standard deployments): ${prices.map((p) => `${p.model}${p.sku ? ` [${p.sku}]` : ''} $${p.inputPer1M} in / $${p.outputPer1M} out per 1M tokens (${p.source})`).join('; ')}. `
    : 'No Azure list price matched the models in this window. ';
  $('priceNotes').textContent = `${text}Models without an unambiguous list price show n/a rather than a guess; add your own rates with the REPORT_PRICE_OVERRIDES setting. Reported costs from telemetry take precedence. List prices exclude discounts and are not an invoice.`;
}

function currentFiltered() {
  return applyFilters(dash.records, dash.filters);
}

function renderAll(refresh = true) {
  if (!dash.report) return;
  if (refresh) refreshFilters();
  const records = currentFiltered();
  const totals = totalsOf(records);
  const tenantLabel = dash.report.tenant?.name || dash.report.tenant?.id || 'Tenant';
  const root = buildTree(records, { tenantLabel: `Tenant: ${tenantLabel}`, byPeriod: dash.byPeriod, grain: dash.grain });
  if (dash.expanded.size === 0 || !dash.expanded.has('root')) dash.expanded = defaultExpanded(root);
  $('dashBody').classList.remove('hidden');
  renderKpis(records, totals);
  renderTrend(records);
  renderTopAgents(records);
  renderTree(root);
  renderPeriodTable(records);
  renderWarnings(dash.report);
  renderPriceNotes(dash.report);
  $('btnExportDetail').disabled = records.every((r) => r.kind !== 'usage');
  $('btnExportGrouped').disabled = records.length === 0;
}

$('btnExpandAll').addEventListener('click', () => {
  const all = new Set();
  const walk = (n) => { if (n.children.length) all.add(n.id); n.children.forEach(walk); };
  if (dash.tree) { walk(dash.tree); dash.expanded = all; renderTree(dash.tree); }
});
$('btnCollapseAll').addEventListener('click', () => { if (dash.tree) { dash.expanded = new Set(['root']); renderTree(dash.tree); } });

function download(name, text) {
  const blob = new Blob([text], { type: 'text/csv;charset=utf-8' });
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

function exportName(kind) {
  return `foundry-token-usage_${kind}_${$('reportStartDate').value}_to_${$('reportEndDate').value}.csv`;
}
$('btnExportDetail').addEventListener('click', () => download(exportName(`detail-${dash.grain}`), detailCsv(currentFiltered(), dash.grain)));
$('btnExportGrouped').addEventListener('click', () => {
  const records = currentFiltered();
  const root = buildTree(records, { tenantLabel: `Tenant: ${dash.report?.tenant?.name || dash.report?.tenant?.id || 'Tenant'}`, byPeriod: dash.byPeriod, grain: dash.grain });
  download(exportName('grouped'), groupedCsv(root, root.totals.total));
});

// ============================================================================
// Data source, scope and running the report
// ============================================================================
async function ensureTenant() {
  if (dash.tenantId) return;
  try {
    const s = await api.get('/api/status');
    dash.tenantId = s?.account?.tenantId || window.currentTenantId || '';
    dash.account = s?.account?.user?.name || s?.account?.name || '';
    if (dash.account && !$('whoami').textContent.trim().replace('—', '')) $('whoami').textContent = dash.account;
    let list = window.tenantList;
    if (!list) { const r = await api.get('/api/tenants'); list = r.tenants || []; window.tenantList = list; }
    const t = list.find((x) => (x.tenantId || '').toLowerCase() === dash.tenantId.toLowerCase());
    dash.tenantName = (t && (t.displayName || t.defaultDomain)) || '';
  } catch { /* the report still works without a tenant display name */ }
}

async function loadScope() {
  await ensureTenant();
  setSourceUi(); // the signed-in account is known now
  let subs = [];
  try { subs = await api.get(`/api/subscriptions?tenantId=${encodeURIComponent(dash.tenantId || '')}`); } catch { /* handled below */ }
  dash.allSubs = (Array.isArray(subs) ? subs : []).map((s) => ({ id: s.id, name: s.name }));
  const saved = (window.savedConfig?.subscriptionIds || []).filter((id) => dash.allSubs.some((s) => s.id === id));
  dash.scope = new Set(saved.length ? saved : dash.allSubs.map((s) => s.id));
  renderScope();
  if (dash.allSubs.length === 0) {
    $('viewerStatus').className = 'status err';
    $('viewerStatus').textContent = 'No subscriptions are available. Sign in with the Azure CLI (or use "New setup / change sign-in") and try again.';
  }
}

function renderScope() {
  $('scopeTitle').textContent = `Scope: ${dash.scope.size} of ${dash.allSubs.length} subscription(s)`;
  const list = $('scopeList');
  list.innerHTML = '';
  dash.allSubs.forEach((s) => {
    const row = el('label', 'checkrow');
    const cb = document.createElement('input');
    cb.type = 'checkbox';
    cb.checked = dash.scope.has(s.id);
    cb.addEventListener('change', () => {
      if (cb.checked) dash.scope.add(s.id); else dash.scope.delete(s.id);
      $('scopeTitle').textContent = `Scope: ${dash.scope.size} of ${dash.allSubs.length} subscription(s)`;
    });
    row.appendChild(cb);
    row.appendChild(el('span', '', `${s.name} (${s.id})`));
    list.appendChild(row);
  });
}

function setSourceUi() {
  const local = $('dashSource').value === 'local';
  $('scopeBox').classList.toggle('hidden', !local);
  const cfg = window.savedConfig || {};
  $('viewerAppInfo').innerHTML = '';
  const rows = local
    ? [['Source', 'Your Azure sign-in (read-only, nothing is deployed)'], ['Signed in as', dash.account || $('whoami').textContent]]
    : [['Source', 'Deployed app (managed identity, scope set at deploy time)'], ['App', cfg.appName], ['App URL', cfg.appUrl], ['Resource group', cfg.resourceGroup]];
  rows.filter(([, v]) => v).forEach(([k, v]) => {
    const row = el('div', 'row');
    row.appendChild(el('span', 'k', k));
    row.appendChild(el('span', 'v', String(v)));
    $('viewerAppInfo').appendChild(row);
  });
}

$('dashSource').addEventListener('change', () => { stopLive(); dash.report = null; $('dashBody').classList.add('hidden'); setSourceUi(); });

function stopLive() {
  clearTimeout(liveTimer);
  liveTimer = null;
  liveGeneration++;
  $('liveMetrics').checked = false;
  ['reportStartDate', 'reportEndDate'].forEach((id) => { $(id).disabled = false; });
  document.querySelectorAll('#rangeChips .chip').forEach((c) => { c.disabled = false; });
}

async function refreshLive(generation) {
  if (!$('liveMetrics').checked || generation !== liveGeneration) return;
  if (!reportRunning) {
    applyRange('today');
    await runReport(true, generation);
  }
  if ($('liveMetrics').checked && generation === liveGeneration) liveTimer = setTimeout(() => refreshLive(generation), 30000);
}

$('liveMetrics').addEventListener('change', () => {
  if (!$('liveMetrics').checked) {
    stopLive();
    $('liveStatus').textContent = 'Live updating stopped. The last result remains visible.';
    return;
  }
  ['reportStartDate', 'reportEndDate'].forEach((id) => { $(id).disabled = true; });
  document.querySelectorAll('#rangeChips .chip').forEach((c) => { c.disabled = true; });
  const generation = ++liveGeneration;
  $('liveStatus').className = 'status info';
  $('liveStatus').textContent = 'Waiting for current telemetry. Usage appears once Application Insights has ingested it, which can take several minutes.';
  refreshLive(generation);
});

async function runReport(live = false, generation = liveGeneration) {
  if (reportRunning) return;
  const startDate = $('reportStartDate').value;
  const endDate = $('reportEndDate').value;
  const status = $('viewerStatus');
  if (!startDate || !endDate) { status.className = 'status err'; status.textContent = 'Choose both a start date and an end date.'; return; }
  if (startDate > endDate) { status.className = 'status err'; status.textContent = 'The start date must be on or before the end date.'; return; }
  const source = $('dashSource').value;
  if (source === 'local' && dash.scope.size === 0) { status.className = 'status err'; status.textContent = 'Select at least one subscription in the scope.'; return; }

  reportRunning = true;
  $('btnRunReport').disabled = true;
  status.className = 'status info';
  status.innerHTML = `<span class="spinner"></span>Discovering Foundry resources and querying every linked Application Insights for ${startDate} → ${endDate}… this can take up to a minute.`;
  try {
    await ensureTenant();
    const r = source === 'app'
      ? await api.post('/api/app-usage', { startDate, endDate })
      : await api.post('/api/usage', { subscriptionIds: [...dash.scope], startDate, endDate, tenantId: dash.tenantId, tenantName: dash.tenantName });
    if (live && (generation !== liveGeneration || !$('liveMetrics').checked)) return;
    if (r.error) {
      status.className = 'status err';
      status.textContent = r.error;
      if (live) $('liveStatus').textContent = 'Refresh failed. Last successful values remain visible; retrying in 30 seconds.';
      return;
    }
    dash.report = r;
    dash.records = flatten(r);
    renderAll();
    const fmt = (s) => new Date(s).toISOString().replace('T', ' ').slice(0, 16);
    const window_ = `${fmt(r.windowStartUtc)} → ${fmt(r.windowEndUtc)} UTC`;
    const usageRows = (r.facts || []).length;
    const projects = (r.inventory?.accounts || []).reduce((n, a) => n + (a.projects || []).length, 0);
    if (usageRows === 0) {
      status.className = 'status err';
      status.textContent = `NO USAGE in ${window_}: ${projects} Foundry project(s) found, but no token usage was returned. Check the warnings below, widen the date range, or allow a few minutes for ingestion.`;
    } else {
      status.className = (r.warnings || []).length ? 'status info' : 'status ok';
      status.textContent = `✔ ${live ? 'Live usage updated' : 'Report generated'} — ${usageRows} usage row(s) across ${projects} Foundry project(s), ${window_}.${(r.warnings || []).length ? ` ${(r.warnings || []).length} warning(s) below.` : ''}`;
    }
    if (live) $('liveStatus').textContent = `Last checked: ${new Date().toISOString()}. Next check 30 seconds after this run.`;
  } catch (e) {
    if (live && (generation !== liveGeneration || !$('liveMetrics').checked)) return;
    status.className = 'status err';
    status.textContent = String(e);
    if (live) $('liveStatus').textContent = 'Refresh failed. Last successful values remain visible; retrying in 30 seconds.';
  } finally {
    reportRunning = false;
    $('btnRunReport').disabled = false;
  }
}

$('btnRunReport').addEventListener('click', () => runReport($('liveMetrics').checked));

// ============================================================================
// Opening and closing the dashboard
// ============================================================================
async function openDashboard(from) {
  window.viewerFrom = from || 'step5';
  $('appRoot').classList.add('wide');
  $('landing').classList.add('hidden');
  $('stepsBar').classList.add('hidden');
  for (let i = 1; i <= 5; i++) $(`step${i}`).classList.add('hidden');
  $('viewer').classList.remove('hidden');
  const hasApp = !!(window.savedConfig || {}).appUrl;
  $('optDeployedApp').disabled = !hasApp;
  $('dashSource').value = 'local';
  setSourceUi();
  setGrain('day');
  applyRange('7');
  await loadScope();
  if (dash.allSubs.length) runReport();
}

function closeDashboard() {
  stopLive();
  $('viewer').classList.add('hidden');
  if (window.viewerFrom === 'landing') showLanding(); else showStep(5);
}
$('btnViewerBack').addEventListener('click', closeDashboard);
