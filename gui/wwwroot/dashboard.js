'use strict';

// True when the deployed app serves this page (it injects window.HOSTED); the local portal never sets it.
const HOSTED = !!window.HOSTED;

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
  return {
    prompt: 0, completion: 0, cached: 0, reasoning: 0, cacheWrite: 0, total: 0, calls: 0, errors: 0, durationMs: 0, runs: 0, runMs: 0,
    cost: 0, priced: 0, unpriced: 0, lastSeen: '',
  };
}

function addTo(t, r) {
  t.prompt += r.prompt || 0;
  t.completion += r.completion || 0;
  t.cached += r.cached || 0;
  t.reasoning += r.reasoning || 0;
  t.cacheWrite += r.cacheWrite || 0;
  t.total += r.total || 0;
  t.calls += r.calls || 0;
  t.errors += r.errors || 0;
  t.durationMs += r.durationMs || 0;
  t.runs += r.runs || 0;
  t.runMs += r.runMs || 0;
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

// Input tokens that were not served from the prompt cache.
const uncachedOf = (t) => Math.max(0, (t.prompt || 0) - (t.cached || 0));

// One record per (day, project, agent, model) of real usage, plus zero-usage records for every discovered project and
// agent so idle agents and unlinked projects stay visible in the hierarchy.
function flatten(report) {
  const tenant = report.tenant || {};
  const scopes = report.scopes || [];
  const tenantName = tenant.name || tenant.id || '';
  const records = [];
  const zero = { prompt: 0, completion: 0, cached: 0, reasoning: 0, cacheWrite: 0, total: 0, calls: 0, errors: 0, durationMs: 0, runs: 0, runMs: 0, reported: null, estimated: null };
  for (const f of report.facts || []) {
    const s = scopes[f.scope] || {};
    records.push({
      kind: 'usage', day: f.day, tenantId: tenant.id || '', tenant: tenantName,
      subscriptionId: s.subscriptionId || '', subscription: s.subscriptionName || s.subscriptionId || '',
      resourceGroup: s.resourceGroup || '', account: s.accountName || '', project: s.projectName || '', projectId: s.projectId || '',
      appInsights: s.appInsightsName || '', portalUrl: s.portalUrl || '', appInsightsPortalUrl: s.appInsightsPortalUrl || '',
      agent: f.agentName || f.agentId || '', agentId: f.agentId || '', model: f.model || '', deployment: f.deployment || '', sku: f.sku || '',
      prompt: f.promptTokens, completion: f.completionTokens, cached: f.cachedTokens, total: f.totalTokens, calls: f.calls,
      reasoning: f.reasoningTokens || 0, cacheWrite: f.cacheWriteTokens || 0, runs: f.runs || 0, runMs: f.runDurationMsSum || 0,
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
  'Agent', 'AgentId', 'Model', 'Deployment', 'PeriodStart', 'PromptTokens', 'CompletionTokens', 'CachedTokens', 'UncachedPromptTokens',
  'ReasoningTokens', 'CacheWriteTokens', 'TotalTokens', 'CacheHitPercent', 'AvgTokensPerCall', 'Calls', 'Errors', 'AvgModelCallLatencyMs',
  'AgentRuns', 'AvgAgentRunLatencyMs', 'ReportedCost', 'EstimatedCostUsd', 'CostBasis', 'FirstSeen', 'LastSeen', 'ProjectPortalUrl', 'AppInsightsPortalUrl'];

const ratio1 = (num, den) => (den ? Number((num / den).toFixed(1)) : '');
const avgModelMs = (t) => (t.calls && t.durationMs ? ratio1(t.durationMs, t.calls) : '');
const avgRunMs = (t) => (t.runs && t.runMs ? ratio1(t.runMs, t.runs) : '');

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
      g.period, t.prompt, t.completion, t.cached, uncachedOf(t), t.reasoning, t.cacheWrite, t.total, t.prompt ? Number(((t.cached / t.prompt) * 100).toFixed(1)) : '',
      ratio1(t.total, t.calls), t.calls, t.errors, avgModelMs(t), t.runs, avgRunMs(t),
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
    'CachedTokens', 'UncachedInputTokens', 'ReasoningTokens', 'TotalTokens', 'Calls', 'AvgTokensPerCall', 'Errors', 'AvgModelCallLatencyMs', 'AgentRuns',
    'AvgAgentRunLatencyMs', 'EstimatedCostUsd', 'SharePercent', 'LastActivity'];
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
      t.prompt, t.completion, t.cached, uncachedOf(t), t.reasoning, t.total, t.calls, ratio1(t.total, t.calls), t.errors, avgModelMs(t), t.runs, avgRunMs(t),
      costForCsv(t), grandTotal ? Number(((t.total / grandTotal) * 100).toFixed(2)) : '', t.lastSeen,
    ].map(csvCell).join(','));
    n.children.forEach((c) => walk(c, next));
  };
  walk(root, {});
  return `${lines.join('\r\n')}\r\n`;
}

// ============================================================================
// Chart and ranking helpers (pure)
// ============================================================================
// Token measures that can be compared on the line chart; the first four are always reported by Foundry telemetry.
// Reasoning and cache-write tokens appear only when the telemetry carries them.
const SERIES = [
  { id: 'total', label: 'Total tokens', color: '#118DFF', dark: '#4DB2FF', value: (t) => t.total, hint: 'Input + output tokens' },
  { id: 'prompt', label: 'Input (prompt)', color: '#12239E', dark: '#7C8CFF', value: (t) => t.prompt, hint: 'Tokens sent to the model' },
  { id: 'completion', label: 'Output (completion)', color: '#E66C37', dark: '#FF9A62', value: (t) => t.completion, hint: 'Tokens generated by the model' },
  { id: 'cached', label: 'Cached input', color: '#6B007B', dark: '#C77DD6', value: (t) => t.cached, hint: 'Input tokens served from the prompt cache' },
  { id: 'uncached', label: 'Uncached input', color: '#E044A7', dark: '#FF7CC8', value: uncachedOf, hint: 'Input tokens that were not cached (input − cached)' },
  { id: 'reasoning', label: 'Reasoning', color: '#D9B300', dark: '#F2D043', value: (t) => t.reasoning, optional: true, hint: 'Reasoning tokens (a subset of output), when the telemetry reports them' },
  { id: 'cacheWrite', label: 'Cache write', color: '#744EC2', dark: '#A98BFF', value: (t) => t.cacheWrite, optional: true, hint: 'Input tokens written to the prompt cache, when reported' },
];

// Line colour for the current theme (lighter shades on the dark background keep enough contrast).
const colorOf = (s) => (document.body.classList.contains('dark') ? s.dark : s.color);

// Series that have data for these totals (optional ones only when non-zero).
function availableSeries(totals) {
  return SERIES.filter((s) => !s.optional || s.value(totals) > 0);
}

// One bucket per period in [startDay, endDay] (empty periods are zeros) with every series' value and the call/cost totals.
function trendSeries(records, grain, startDay, endDay) {
  const buckets = new Map(periodsBetween(startDay, endDay, grain).map((p) => [p, newTotals()]));
  records.filter((r) => r.kind === 'usage').forEach((r) => {
    const b = buckets.get(periodStart(r.day, grain));
    if (b) addTo(b, r);
  });
  const periods = [...buckets.keys()];
  const totals = periods.map((p) => buckets.get(p));
  const values = {};
  SERIES.forEach((s) => { values[s.id] = totals.map((t) => s.value(t)); });
  return { periods, totals, values };
}

// A round axis maximum (1, 2, 2.5, 5 × 10^n) at or above the data maximum, so gridlines land on tidy numbers.
function niceMax(max) {
  if (!(max > 0)) return 1;
  const pow = 10 ** Math.floor(Math.log10(max));
  const f = max / pow;
  const step = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
  return step * pow;
}

// Largest consumers by total tokens for one dimension; rows without a name are kept but flagged so they cannot be used as a filter.
function topN(records, keyFn, n = 5) {
  const groups = new Map();
  records.filter((r) => r.kind === 'usage' && r.total > 0).forEach((r) => {
    const key = keyFn(r) || '';
    let g = groups.get(key);
    if (!g) { g = { name: key, totals: newTotals() }; groups.set(key, g); }
    addTo(g.totals, r);
  });
  const grand = [...groups.values()].reduce((s, g) => s + g.totals.total, 0);
  return [...groups.values()]
    .sort((a, b) => (b.totals.total - a.totals.total) || a.name.localeCompare(b.name))
    .slice(0, n)
    .map((g) => ({ name: g.name, totals: g.totals, share: grand ? g.totals.total / grand : 0 }));
}

// ============================================================================
// Formatting
// ============================================================================
const fmtInt = (n) => Math.round(n || 0).toLocaleString('en-US');

// 1,234 → 1.2K, 2,500,000 → 2.5M (axis labels).
function fmtCompact(n) {
  const a = Math.abs(n);
  if (a >= 1e9) return `${+(n / 1e9).toFixed(2)}B`;
  if (a >= 1e6) return `${+(n / 1e6).toFixed(2)}M`;
  if (a >= 1e3) return `${+(n / 1e3).toFixed(1)}K`;
  return String(Math.round(n));
}

function fmtCost(t) {
  if (t.priced === 0) return t.unpriced > 0 ? 'n/a' : '—';
  const c = t.cost;
  const s = c >= 100 ? c.toFixed(2) : c >= 1 ? c.toFixed(3) : c.toFixed(6);
  return `${t.unpriced > 0 ? '≥ ' : ''}$${s}`;
}

const fmtPct = (x) => (Number.isFinite(x) ? `${(x * 100).toFixed(x >= 0.1 || x === 0 ? 1 : 2)}%` : '—');
// Durations: milliseconds under 10 s, seconds above, so a 13,893 ms agent run reads "13.9 s".
function fmtMs(ms) {
  if (!Number.isFinite(ms) || ms <= 0) return '—';
  return ms >= 10000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms).toLocaleString('en-US')} ms`;
}
// Average of one model (LLM) request.
const fmtLatency = (t) => (t.calls && t.durationMs ? fmtMs(t.durationMs / t.calls) : '—');
// Average of a whole agent run (invoke_agent), including tool calls and waiting.
const fmtRunLatency = (t) => (t.runs && t.runMs ? fmtMs(t.runMs / t.runs) : '—');
const fmtLast = (s) => (s ? s.replace('T', ' ').slice(0, 16) : '—');

// ============================================================================
// Dashboard state and rendering
// ============================================================================
const dash = {
  report: null, records: [], grain: 'day', expanded: new Set(), scope: new Set(), allSubs: [], hidden: new Set(), trend: null,
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

const GRAIN_HELP = {
  day: 'Each point is one calendar day (UTC). Use it for short ranges and live monitoring.',
  week: 'Each point is one week, Monday to Sunday (UTC), summing its days. Use it for trends over weeks.',
  month: 'Each point is one calendar month (UTC), summing its days. Use it for monthly totals and cost.',
};

function setGrain(grain) {
  dash.grain = grain;
  document.querySelectorAll('#grainButtons .chip').forEach((b) => b.classList.toggle('active', b.dataset.grain === grain));
  $('grainHint').textContent = `${GRAIN_HELP[grain]} It sets the chart, the "By period" table, the peak card, "Break down by period" and the detail CSV export; totals and cost are the same.`;
  if (dash.report) renderAll();
}
document.querySelectorAll('#grainButtons .chip').forEach((b) => b.addEventListener('click', () => setGrain(b.dataset.grain)));

// Light / dark theme for the report; remembered in the browser.
const THEME_STORE = 'foundryReport.theme';
function applyTheme(theme) {
  const dark = theme === 'dark';
  document.body.classList.toggle('dark', dark);
  $('btnTheme').textContent = dark ? '☀ Light' : '🌙 Dark';
  $('btnTheme').setAttribute('aria-pressed', String(dark));
  try { localStorage.setItem(THEME_STORE, dark ? 'dark' : 'light'); } catch { /* not critical */ }
  if (dash.report && !$('viewer').classList.contains('hidden')) renderAll(false); // series colours differ per theme
}
function loadTheme() {
  let saved = 'light';
  try { saved = localStorage.getItem(THEME_STORE) === 'dark' ? 'dark' : 'light'; } catch { /* default */ }
  applyTheme(saved);
}
$('btnTheme').addEventListener('click', () => applyTheme(document.body.classList.contains('dark') ? 'light' : 'dark'));

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
$('btnClearFilters').addEventListener('click', clearFilters);
$('hideIdle').addEventListener('change', () => { dash.filters.hideIdle = $('hideIdle').checked; renderAll(false); });
$('showPeriods').addEventListener('change', () => { dash.byPeriod = $('showPeriods').checked; renderAll(false); });

function kpi(label, value, sub, opts = {}) {
  const c = el(opts.series ? 'button' : 'div', `kpi${opts.series ? ' kpi-series' : ''}`);
  if (opts.series) {
    c.type = 'button';
    c.dataset.series = opts.series.id;
    c.style.setProperty('--series', colorOf(opts.series));
    const off = dash.hidden.has(opts.series.id);
    c.classList.toggle('off', off);
    c.setAttribute('aria-pressed', String(!off));
    c.title = `${opts.series.hint}. Click to ${off ? 'show' : 'hide'} this line on the chart.`;
    c.addEventListener('click', () => toggleSeries(opts.series.id));
  } else if (opts.title) c.title = opts.title;
  c.appendChild(el('div', 'kpi-k', label));
  c.appendChild(el('div', 'kpi-v', value));
  c.appendChild(el('div', 'kpi-s', sub || ' '));
  return c;
}

function renderKpis(records, t) {
  const usage = records.filter((r) => r.kind === 'usage' && r.total > 0);
  const active = new Set(usage.map((r) => r.agent || '(unattributed)')).size;
  const known = new Set(records.filter((r) => r.agent || r.kind === 'usage').map((r) => r.agent || '(unattributed)')).size;

  const tokenBox = $('kpiCards');
  tokenBox.innerHTML = '';
  const sub = {
    total: `${fmtInt(t.calls)} model call${t.calls === 1 ? '' : 's'}`,
    prompt: t.total ? `${fmtPct(t.prompt / t.total)} of total` : ' ',
    completion: t.total ? `${fmtPct(t.completion / t.total)} of total` : ' ',
    cached: t.prompt ? `${fmtPct(t.cached / t.prompt)} of input` : ' ',
    uncached: t.prompt ? `${fmtPct(uncachedOf(t) / t.prompt)} of input` : ' ',
    reasoning: t.completion ? `${fmtPct(t.reasoning / t.completion)} of output` : ' ',
    cacheWrite: t.prompt ? `${fmtPct(t.cacheWrite / t.prompt)} of input` : ' ',
  };
  const cards = availableSeries(t);
  tokenBox.style.setProperty('--n', String(cards.length));
  cards.forEach((s) => tokenBox.appendChild(kpi(s.label, fmtInt(s.value(t)), sub[s.id], { series: s })));

  const peak = peakPeriod(usage, dash.grain);
  const opsBox = $('kpiOps');
  opsBox.innerHTML = '';
  [
    kpi('Model calls', fmtInt(t.calls), 'LLM requests', { title: 'Number of model (LLM) requests' }),
    kpi('Agent runs', fmtInt(t.runs), 'end-to-end invocations', { title: 'Agent invocations (invoke_agent spans). One run can make several model calls.' }),
    kpi('Tokens per call', t.calls ? fmtInt(t.total / t.calls) : '—', t.calls ? `${fmtInt(t.prompt / t.calls)} in · ${fmtInt(t.completion / t.calls)} out` : ' ', { title: 'Average total tokens per model call' }),
    kpi('Cache hit rate', t.prompt ? fmtPct(t.cached / t.prompt) : '—', `${fmtInt(t.cached)} cached tokens`, { title: 'Share of input tokens served from the prompt cache' }),
    kpi('Est. cost (USD)', fmtCost(t), t.unpriced > 0 ? `${t.unpriced} row(s) unpriced` : 'list price or reported'),
    kpi('Agents', `${active} / ${known}`, 'active / known'),
    kpi('Error rate', t.calls ? fmtPct(t.errors / t.calls) : '—', `${fmtInt(t.errors)} failed`),
    kpi('Avg model latency', fmtLatency(t), 'per model (LLM) call', { title: 'Average duration of a single model request. It does not include tool calls or waiting between steps; see Avg agent run for the full journey.' }),
    kpi('Avg agent run', fmtRunLatency(t), 'whole agent journey', { title: 'Average end-to-end time of an agent run (the invoke_agent span): all model calls, tool calls and waits.' }),
    kpi(`Peak ${dash.grain}`, peak ? periodLabel(peak.start, dash.grain) : '—', peak ? `${fmtInt(peak.total)} tokens` : ' ', { title: `The ${dash.grain} with the most tokens in this window` }),
  ].forEach((c) => opsBox.appendChild(c));
}

// The period (day, week or month) with the most tokens.
function peakPeriod(usageRecords, grain) {
  const sums = new Map();
  usageRecords.forEach((r) => { const k = periodStart(r.day, grain); sums.set(k, (sums.get(k) || 0) + r.total); });
  let best = null;
  sums.forEach((total, start) => { if (total > 0 && (!best || total > best.total || (total === best.total && start > best.start))) best = { start, total }; });
  return best;
}

function toggleSeries(id) {
  if (dash.hidden.has(id)) dash.hidden.delete(id); else dash.hidden.add(id);
  if (dash.report) renderAll(false);
}

const SVG_NS_ESC = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function chartRange(records) {
  const days = records.filter((r) => r.kind === 'usage').map((r) => r.day).sort();
  const start = dash.report?.windowStartUtc ? String(dash.report.windowStartUtc).slice(0, 10) : days[0];
  const end = dash.report?.windowEndUtc ? String(dash.report.windowEndUtc).slice(0, 10) : days[days.length - 1];
  return [start, end];
}

// Multi-series line chart drawn as inline SVG (no library): one coloured line per token measure, a shared axis, gridlines and
// a hover read-out of every visible series for the nearest period.
function renderTrend(records, totals) {
  const grain = dash.grain;
  const [start, end] = chartRange(records);
  $('trendTitle').textContent = `Token consumption by ${grain}`;
  const chart = $('trendChart');
  const legend = $('trendLegend');
  legend.innerHTML = '';
  chart.innerHTML = '';
  dash.trend = null;
  if (!start || !end) { chart.appendChild(el('div', 'hint pad', 'No usage in this window.')); return; }

  const data = trendSeries(records, grain, start, end);
  const avail = availableSeries(totals);
  avail.forEach((s) => {
    const b = el('button', `legend-item${dash.hidden.has(s.id) ? ' off' : ''}`);
    b.type = 'button';
    b.style.setProperty('--series', colorOf(s));
    b.setAttribute('aria-pressed', String(!dash.hidden.has(s.id)));
    b.title = `${s.hint}. Click to ${dash.hidden.has(s.id) ? 'show' : 'hide'}.`;
    b.appendChild(el('span', 'sw', ''));
    b.appendChild(el('span', '', s.label));
    b.addEventListener('click', () => toggleSeries(s.id));
    legend.appendChild(b);
  });
  const shown = avail.filter((s) => !dash.hidden.has(s.id));
  if (shown.length === 0) { chart.appendChild(el('div', 'hint pad', 'All lines are hidden. Pick a token measure above to show it.')); return; }

  const W = Math.max(520, chart.clientWidth || 900);
  const H = 300;
  const m = { l: 58, r: 20, t: 16, b: 36 };
  const pw = W - m.l - m.r;
  const ph = H - m.t - m.b;
  const n = data.periods.length;
  const yMax = niceMax(Math.max(1, ...shown.flatMap((s) => data.values[s.id])));
  const x = (i) => m.l + (n === 1 ? pw / 2 : (i / (n - 1)) * pw);
  const y = (v) => m.t + ph - (v / yMax) * ph;

  let svg = `<svg class="linechart" viewBox="0 0 ${W} ${H}" width="${W}" height="${H}" role="img" aria-label="Token consumption by ${grain}">`;
  for (let g = 0; g <= 5; g++) {
    const v = (yMax / 5) * g;
    svg += `<line class="grid" x1="${m.l}" x2="${W - m.r}" y1="${y(v).toFixed(1)}" y2="${y(v).toFixed(1)}"/>`;
    svg += `<text class="ylab" x="${m.l - 8}" y="${(y(v) + 4).toFixed(1)}" text-anchor="end">${fmtCompact(v)}</text>`;
  }
  const every = Math.ceil(n / Math.max(2, Math.floor(pw / 90)));
  data.periods.forEach((p, i) => {
    if (i % every === 0) svg += `<text class="xlab" x="${x(i).toFixed(1)}" y="${H - 12}" text-anchor="middle">${SVG_NS_ESC(periodLabel(p, grain))}</text>`;
  });
  shown.forEach((s) => {
    const pts = data.values[s.id].map((v, i) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`);
    if (n > 1) svg += `<polyline class="line" fill="none" stroke="${colorOf(s)}" stroke-width="2.25" stroke-linejoin="round" stroke-linecap="round" points="${pts.join(' ')}"/>`;
    data.values[s.id].forEach((v, i) => { svg += `<circle class="dot" cx="${x(i).toFixed(1)}" cy="${y(v).toFixed(1)}" r="${n === 1 ? 5 : 3.2}" fill="${colorOf(s)}"/>`; });
  });
  svg += `<line class="guide hidden" id="trendGuide" y1="${m.t}" y2="${m.t + ph}" x1="0" x2="0"/></svg>`;
  chart.innerHTML = svg;
  dash.trend = { data, shown, grain, geometry: { W, m, pw, n } };
}

function showTrendTip(clientX, rect) {
  const t = dash.trend;
  const tip = $('trendTip');
  if (!t) { tip.classList.add('hidden'); return; }
  const { W, m, pw, n } = t.geometry;
  const px = ((clientX - rect.left) / rect.width) * W;
  const i = n === 1 ? 0 : Math.min(n - 1, Math.max(0, Math.round(((px - m.l) / pw) * (n - 1))));
  const cx = m.l + (n === 1 ? pw / 2 : (i / (n - 1)) * pw);
  const guide = document.getElementById('trendGuide');
  if (guide) { guide.setAttribute('x1', cx.toFixed(1)); guide.setAttribute('x2', cx.toFixed(1)); guide.classList.remove('hidden'); }
  const tot = t.data.totals[i];
  tip.innerHTML = '';
  tip.appendChild(el('div', 'tip-h', periodLabel(t.data.periods[i], t.grain)));
  t.shown.forEach((s) => {
    const row = el('div', 'tip-r');
    const sw = el('span', 'sw', '');
    sw.style.background = colorOf(s);
    row.appendChild(sw);
    row.appendChild(el('span', 'tip-k', s.label));
    row.appendChild(el('span', 'tip-v', fmtInt(t.data.values[s.id][i])));
    tip.appendChild(row);
  });
  tip.appendChild(el('div', 'tip-f', `${fmtInt(tot.calls)} call(s) · ${fmtCost(tot)}`));
  tip.classList.remove('hidden');
  const left = (cx / W) * rect.width;
  tip.style.left = `${left > rect.width * 0.62 ? left - tip.offsetWidth - 14 : left + 14}px`;
}

$('trendChart').addEventListener('mousemove', (e) => showTrendTip(e.clientX, $('trendChart').getBoundingClientRect()));
$('trendChart').addEventListener('mouseleave', () => {
  $('trendTip').classList.add('hidden');
  document.getElementById('trendGuide')?.classList.add('hidden');
});
let resizeTimer = null;
window.addEventListener?.('resize', () => {
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => { if (dash.report && !$('viewer').classList.contains('hidden')) renderAll(false); }, 150);
});

// Top 5 consumers of one dimension. The list ignores that dimension's own filter, so the other entries stay visible and
// clicking one swaps the selection; clicking the selected entry clears it.
function renderTop(boxId, field, keyFn, records) {
  const box = $(boxId);
  box.innerHTML = '';
  const top = topN(applyFilters(records, { ...dash.filters, [field]: '' }), keyFn, 5);
  if (top.length === 0) { box.appendChild(el('div', 'hint', 'No usage in this window.')); return; }
  const max = top[0].totals.total || 1;
  const selected = dash.filters[field];
  top.forEach((e, i) => {
    const selectable = !!e.name;
    const row = el(selectable ? 'button' : 'div', `top-row${selected && e.name === selected ? ' selected' : ''}${selected && e.name !== selected ? ' dim' : ''}`);
    if (selectable) {
      row.type = 'button';
      row.title = e.name === selected ? 'Click to remove this filter' : `Click to filter the dashboard to ${e.name}`;
      row.addEventListener('click', () => setFilter(field, e.name === dash.filters[field] ? '' : e.name));
    } else row.title = 'Usage without a name cannot be used as a filter';
    row.appendChild(el('div', 'top-rank', String(i + 1)));
    row.appendChild(el('div', 'top-name', e.name || '(unattributed)'));
    const bar = el('div', 'top-bar');
    const widthPct = (e.totals.total / max) * 100;
    const inp = el('div', 'top-fill in');
    inp.style.width = `${e.totals.total ? (e.totals.prompt / e.totals.total) * widthPct : 0}%`;
    const out = el('div', 'top-fill out');
    out.style.width = `${e.totals.total ? (e.totals.completion / e.totals.total) * widthPct : 0}%`;
    bar.appendChild(inp);
    bar.appendChild(out);
    row.appendChild(bar);
    row.appendChild(el('div', 'top-val', fmtInt(e.totals.total)));
    row.appendChild(el('div', 'top-share', fmtPct(e.share)));
    box.appendChild(row);
  });
}

function renderTops(records) {
  renderTop('topAgents', 'agent', (r) => r.agent, records);
  renderTop('topModels', 'model', (r) => r.model, records);
}

const FILTER_LABELS = { subscriptionId: 'Subscription', resourceGroup: 'Resource group', projectId: 'Foundry project', agent: 'Agent', model: 'Model' };
const FILTER_CONTROLS = { subscriptionId: 'fSubscription', resourceGroup: 'fResourceGroup', projectId: 'fProject', agent: 'fAgent', model: 'fModel' };

function setFilter(field, value) {
  dash.filters[field] = value;
  const sel = $(FILTER_CONTROLS[field]);
  if (sel) sel.value = value;
  renderAll(false);
}

function activeFilterCount() {
  return Object.keys(FILTER_LABELS).filter((k) => dash.filters[k]).length;
}

function renderFilterState() {
  const n = activeFilterCount();
  $('btnClearFilters').disabled = n === 0;
  $('activeFilters').textContent = n
    ? `Showing only: ${Object.keys(FILTER_LABELS).filter((k) => dash.filters[k]).map((k) => `${FILTER_LABELS[k]} = ${filterText(k)}`).join(' · ')}`
    : 'Showing everything in the selected scope and date range.';
}

function filterText(field) {
  const sel = $(FILTER_CONTROLS[field]);
  const opt = sel?.selectedOptions?.[0];
  return (opt && opt.textContent) || dash.filters[field];
}

function clearFilters() {
  Object.keys(FILTER_LABELS).forEach((k) => { dash.filters[k] = ''; const sel = $(FILTER_CONTROLS[k]); if (sel) sel.value = ''; });
  if (dash.report) renderAll(false); else renderFilterState();
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
    cell(fmtInt(t.prompt), 'num');
    cell(fmtInt(t.completion), 'num');
    cell(fmtInt(t.cached), 'num');
    cell(fmtInt(t.calls), 'num');
    cell(fmtCost(t), 'num');
    cell(prev && prev.total > 0 ? `${t.total >= prev.total ? '+' : ''}${(((t.total - prev.total) / prev.total) * 100).toFixed(1)}%` : '—', 'num');
    body.appendChild(tr);
  });
  if (keys.length === 0) { const tr = el('tr'); const td = el('td', '', 'No usage in this window.'); td.colSpan = 8; tr.appendChild(td); body.appendChild(tr); }
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
    num(fmtInt(t.prompt)); num(fmtInt(t.completion)); num(fmtInt(t.cached)); num(fmtInt(uncachedOf(t))); num(fmtInt(t.reasoning), 'num col-reasoning');
    num(fmtInt(t.total)); num(fmtInt(t.calls)); num(t.calls ? fmtInt(t.total / t.calls) : '—');
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
    num(fmtLatency(t)); num(fmtRunLatency(t)); num(t.calls ? fmtPct(t.errors / t.calls) : '—'); num(fmtLast(t.lastSeen), 'when');
    body.appendChild(tr);
    if (hasKids && dash.expanded.has(node.id)) node.children.forEach((c) => walk(c, depth + 1));
  };
  walk(root, 0);
  $('treeTable').classList.toggle('no-reasoning', root.totals.reasoning === 0);
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
  const win = dash.report.windowStartUtc ? `${String(dash.report.windowStartUtc).slice(0, 10)} → ${String(dash.report.windowEndUtc).slice(0, 10)} UTC` : '';
  $('reportSubtitle').textContent = [tenantLabel, `${new Set(dash.records.map((r) => r.subscriptionId).filter(Boolean)).size} subscription(s)`, win].filter(Boolean).join('  ·  ');
  renderFilterState();
  renderKpis(records, totals);
  renderTrend(records, totals);
  renderTops(dash.records);
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
  const local = !HOSTED && $('dashSource').value === 'local';
  $('scopeBox').classList.toggle('hidden', !local);
  const cfg = window.savedConfig || {};
  $('viewerAppInfo').innerHTML = '';
  let rows;
  if (HOSTED) rows = [['Source', 'Live report from this app (read-only; the subscriptions were chosen when it was deployed)'], ['Address', window.location?.host || '']];
  else if (local) rows = [['Source', 'Your Azure sign-in (read-only, nothing is deployed)'], ['Signed in as', dash.account || $('whoami').textContent]];
  else rows = [['Source', 'Deployed app (managed identity, scope set at deploy time)'], ['App', cfg.appName], ['App URL', cfg.appUrl], ['Resource group', cfg.resourceGroup]];
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

// Live refresh interval: a number plus a minutes/hours unit, never below one minute (each refresh re-scans Azure and queries
// every linked Application Insights) and never above one day.
const LIVE_MIN_MS = 60 * 1000;
const LIVE_MAX_MS = 24 * 60 * 60 * 1000;
const LIVE_STORE = 'foundryReport.liveInterval';

function liveIntervalMs() {
  const raw = Number($('liveEvery').value);
  const n = Number.isFinite(raw) && raw > 0 ? Math.max(1, Math.floor(raw)) : 5;
  const unit = Number($('liveUnit').value) || LIVE_MIN_MS;
  return Math.min(LIVE_MAX_MS, Math.max(LIVE_MIN_MS, n * unit));
}

function describeInterval(ms) {
  return ms % 3600000 === 0 ? `${ms / 3600000} hour${ms === 3600000 ? '' : 's'}` : `${Math.round(ms / 60000)} minute${ms === 60000 ? '' : 's'}`;
}

function loadLiveInterval() {
  let saved = null;
  try { saved = JSON.parse(localStorage.getItem(LIVE_STORE) || 'null'); } catch { /* storage unavailable or corrupt: use the default */ }
  const unit = saved && ['60000', '3600000'].includes(String(saved.unit)) ? String(saved.unit) : '60000';
  const every = saved && Number.isInteger(saved.every) && saved.every > 0 ? saved.every : 5;
  $('liveUnit').value = unit;
  $('liveEvery').value = String(every);
  normalizeLiveInterval();
}

// Keeps the typed value inside the allowed range and remembers it.
function normalizeLiveInterval() {
  const ms = liveIntervalMs();
  const unit = $('liveUnit').value === '3600000' ? 3600000 : 60000;
  $('liveEvery').value = String(Math.max(1, Math.round(ms / unit)));
  try { localStorage.setItem(LIVE_STORE, JSON.stringify({ every: Number($('liveEvery').value), unit })); } catch { /* not critical */ }
  $('liveHint').textContent = `Next check ${describeInterval(ms)} after each run.`;
}

function scheduleNextLive(generation) {
  clearTimeout(liveTimer);
  if (!$('liveMetrics').checked || generation !== liveGeneration) return;
  const ms = liveIntervalMs();
  liveNextAt = Date.now() + ms;
  liveTimer = setTimeout(() => refreshLive(generation), ms);
}

let liveNextAt = 0;
const clock = (ms) => new Date(ms).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });

async function refreshLive(generation) {
  if (!$('liveMetrics').checked || generation !== liveGeneration) return;
  if (!reportRunning) {
    applyRange('today');
    await runReport(true, generation);
  }
  scheduleNextLive(generation);
  if ($('liveMetrics').checked && generation === liveGeneration && liveNote) $('liveStatus').textContent = liveStatusText(liveNote);
}

let liveNote = '';

function liveStatusText(prefix) {
  return `${prefix} Next check at ${clock(liveNextAt)} (every ${describeInterval(liveIntervalMs())}).`;
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
  $('liveStatus').textContent = `Waiting for current telemetry. Usage appears once Application Insights has ingested it, which can take several minutes. Refreshing every ${describeInterval(liveIntervalMs())}.`;
  refreshLive(generation);
});

// Changing the interval while live is on takes effect from the next scheduled check.
['liveEvery', 'liveUnit'].forEach((id) => $(id).addEventListener('change', () => {
  normalizeLiveInterval();
  if ($('liveMetrics').checked && !reportRunning) scheduleNextLive(liveGeneration);
}));

async function runReport(live = false, generation = liveGeneration) {
  if (reportRunning) return;
  const startDate = $('reportStartDate').value;
  const endDate = $('reportEndDate').value;
  const status = $('viewerStatus');
  if (!startDate || !endDate) { status.className = 'status err'; status.textContent = 'Choose both a start date and an end date.'; return; }
  if (startDate > endDate) { status.className = 'status err'; status.textContent = 'The start date must be on or before the end date.'; return; }
  const source = HOSTED ? 'hosted' : $('dashSource').value;
  if (source === 'local' && dash.scope.size === 0) { status.className = 'status err'; status.textContent = 'Select at least one subscription in the scope.'; return; }

  reportRunning = true;
  $('btnRunReport').disabled = true;
  status.className = 'status info';
  status.innerHTML = `<span class="spinner"></span>Discovering Foundry resources and querying every linked Application Insights for ${startDate} → ${endDate}… this can take up to a minute.`;
  try {
    if (!HOSTED) await ensureTenant();
    let r;
    if (source === 'hosted') r = await api.get(`/api/usage?startDate=${encodeURIComponent(startDate)}&endDate=${encodeURIComponent(endDate)}`);
    else if (source === 'app') r = await api.post('/api/app-usage', { startDate, endDate });
    else r = await api.post('/api/usage', { subscriptionIds: [...dash.scope], startDate, endDate, tenantId: dash.tenantId, tenantName: dash.tenantName });
    if (live && (generation !== liveGeneration || !$('liveMetrics').checked)) return;
    if (HOSTED && isKeyRejected(r)) { stopLive(); showKeyGate('That API key was not accepted. Check it and try again.'); return; }
    if (r.error) {
      status.className = 'status err';
      status.textContent = r.error;
      if (live) liveNote = 'Refresh failed. Last successful values remain visible.';
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
    if (live) liveNote = `Last checked: ${new Date().toISOString()}.`;
  } catch (e) {
    if (live && (generation !== liveGeneration || !$('liveMetrics').checked)) return;
    status.className = 'status err';
    status.textContent = String(e);
    if (live) liveNote = 'Refresh failed. Last successful values remain visible.';
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
  document.body.classList.add('pbi');
  loadTheme();
  $('appRoot').classList.add('wide');
  $('landing').classList.add('hidden');
  $('stepsBar').classList.add('hidden');
  for (let i = 1; i <= 5; i++) $(`step${i}`).classList.add('hidden');
  $('viewer').classList.remove('hidden');
  $('keyGate').classList.add('hidden');
  if (HOSTED) {
    setGrain('day');
    applyRange('7');
    loadLiveInterval();
    clearFilters();
    setSourceUi();
    runReport();
    return;
  }
  const hasApp = !!(window.savedConfig || {}).appUrl;
  $('optDeployedApp').disabled = !hasApp;
  $('dashSource').value = 'local';
  setSourceUi();
  setGrain('day');
  applyRange('7');
  loadLiveInterval();
  clearFilters();
  await loadScope();
  if (dash.allSubs.length) runReport();
}

function closeDashboard() {
  stopLive();
  document.body.classList.remove('pbi');
  document.body.classList.remove('dark');
  $('viewer').classList.add('hidden');
  if (window.viewerFrom === 'landing') showLanding(); else showStep(5);
}
$('btnViewerBack').addEventListener('click', closeDashboard);

// ============================================================================
// Hosted mode: the deployed app serves this page; the API key protects the data calls.
// ============================================================================
const KEY_STORE = 'foundryReport.apiKey';

// The app answers 401 (wrong/missing key) with { error: 'Invalid or missing X-Api-Key.' }.
const isKeyRejected = (r) => !!r && typeof r.error === 'string' && /x-api-key/i.test(r.error);

function showKeyGate(message) {
  stopLive();
  try { sessionStorage.removeItem(KEY_STORE); } catch { /* not critical */ }
  window.HOSTED_KEY = '';
  $('viewer').classList.add('hidden');
  $('keyGate').classList.remove('hidden');
  $('keyGateStatus').className = message ? 'status err' : 'status';
  $('keyGateStatus').textContent = message || '';
  $('apiKeyInput').value = '';
}

async function submitApiKey() {
  const key = $('apiKeyInput').value.trim();
  if (!key) { $('keyGateStatus').className = 'status err'; $('keyGateStatus').textContent = 'Enter the API key.'; return; }
  window.HOSTED_KEY = key;
  try { sessionStorage.setItem(KEY_STORE, key); } catch { /* not critical */ }
  $('keyGate').classList.add('hidden');
  await openDashboard('hosted');
}
$('btnApiKey').addEventListener('click', submitApiKey);
$('btnLock').addEventListener('click', () => showKeyGate(''));
$('apiKeyInput').addEventListener('keydown', (e) => { if (e.key === 'Enter') submitApiKey(); });

// A link like https://<app>/#key=<api key> opens the report directly. The fragment never reaches the server and is
// removed from the address bar once read; the key is kept for this browser tab only.
function startHosted() {
  document.body.classList.add('hosted', 'pbi');
  loadTheme();
  $('landing').classList.add('hidden');
  $('stepsBar').classList.add('hidden');
  for (let i = 1; i <= 5; i++) $(`step${i}`).classList.add('hidden');
  const fromLink = /[#&]key=([^&]+)/.exec(window.location?.hash || '');
  let key = '';
  if (fromLink) {
    try { key = decodeURIComponent(fromLink[1]); } catch { key = ''; }
    try { window.history.replaceState(null, '', window.location.pathname + window.location.search); } catch { /* not critical */ }
  }
  if (!key) { try { key = sessionStorage.getItem(KEY_STORE) || ''; } catch { key = ''; } }
  if (!key) { showKeyGate(''); return; }
  window.HOSTED_KEY = key;
  try { sessionStorage.setItem(KEY_STORE, key); } catch { /* not critical */ }
  openDashboard('hosted');
}
if (HOSTED) {
  startHosted();
  // Pasting a #key= link into a tab where the page is already open only changes the fragment (no reload).
  window.addEventListener?.('hashchange', () => { if (/[#&]key=/.test(window.location?.hash || '')) { stopLive(); startHosted(); } });
}
