// Public dashboard: one SignalR connection (MessagePack), no REST calls, no framework (docs/FRONTEND.md 7).
import { num, formatBytes, formatMb, formatBps, formatDuration, timeAgo, percent, flagEmoji, clock } from './format.js';
import { drawSparkline, pushPoint } from './sparkline.js';

const STATUS_ONLINE = 1, STATUS_OFFLINE = 2;

const state = {
  site: { title: '节点状态', subtitle: '', showSpecs: true, showTraffic: true, offlineSec: 30 },
  nodes: new Map(),      // id -> { meta, live, hist, el }
  order: [],
  dirty: new Set(),
  lastUpdate: 0,
  connected: false,
};

const $ = (id) => document.getElementById(id);
const grid = $('grid');

// ---------------------------------------------------------------- theme
function applyTheme(theme) {
  if (theme === 'light' || theme === 'dark') document.documentElement.setAttribute('data-theme', theme);
  else document.documentElement.removeAttribute('data-theme');
  scheduleDraw(true);
}
function isDark() {
  const t = document.documentElement.getAttribute('data-theme');
  if (t) return t === 'dark';
  return window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;
}
applyTheme(localStorage.getItem('snm-theme'));
$('theme-toggle').addEventListener('click', () => {
  const next = isDark() ? 'light' : 'dark';
  localStorage.setItem('snm-theme', next);
  applyTheme(next);
});
if (window.matchMedia) window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => scheduleDraw(true));

// ---------------------------------------------------------------- rendering
function cardTemplate() {
  const el = document.createElement('article');
  el.className = 'card';
  el.innerHTML = `
    <div class="card-head">
      <div class="card-name"><span class="flag"></span><span class="name"></span></div>
      <span class="status"></span>
    </div>
    <div class="specs"></div>
    <div class="metric"><span class="label">CPU</span><div class="bar"><i data-k="cpu"></i></div><span class="value" data-v="cpu">–</span></div>
    <div class="metric-row">
      <div class="metric"><span class="label">内存</span><div class="bar"><i data-k="mem"></i></div><span class="value" data-v="mem">–</span></div>
      <div class="metric"><span class="label">磁盘</span><div class="bar"><i data-k="disk"></i></div><span class="value" data-v="disk">–</span></div>
    </div>
    <div class="net"><span class="rx">↓ –</span><span class="tx">↑ –</span></div>
    <div class="traffic"><span class="label">本月流量</span><div class="bar"><i data-k="traffic"></i></div><span class="value" data-v="traffic">–</span></div>
    <canvas class="spark"></canvas>
    <div class="card-foot"><span class="uptime"></span><span class="seen"></span></div>`;
  return el;
}

function ensureCard(node) {
  if (!node.el) {
    node.el = cardTemplate();
    node.el.dataset.id = String(node.meta.id);
  }
  return node.el;
}

function renderMeta(node) {
  const el = ensureCard(node);
  const m = node.meta;
  el.querySelector('.flag').textContent = flagEmoji(m.cc);
  el.querySelector('.name').textContent = m.name || `节点 ${m.id}`;
  const specs = el.querySelector('.specs');
  if (state.site.showSpecs && (m.cores || m.memMb || m.diskMb)) {
    specs.hidden = false;
    specs.dataset.base = `${num(m.cores)} 核 · ${formatMb(m.memMb)} · ${formatMb(m.diskMb)}`;
  } else {
    specs.hidden = true;
    specs.dataset.base = '';
  }
  el.querySelector('.traffic').hidden = !(state.site.showTraffic && num(m.tLimit) > 0);
}

function setBar(el, key, permille) {
  const bar = el.querySelector(`i[data-k="${key}"]`);
  const pct = Math.min(100, Math.max(0, num(permille) / 10));
  bar.style.width = pct + '%';
  bar.className = pct >= 90 ? 'bad' : pct >= 75 ? 'warn' : '';
}

function renderLive(node, now) {
  const el = ensureCard(node);
  const live = node.live || {};
  const status = num(live.status);
  const st = el.querySelector('.status');
  const ts = num(live.ts);
  if (status === STATUS_ONLINE) {
    st.className = 'status online';
    st.textContent = '在线';
    el.classList.remove('offline');
  } else if (status === STATUS_OFFLINE) {
    st.className = 'status offline';
    st.textContent = ts > 0 ? `离线 · ${formatDuration((now - ts) / 1000)}` : '离线';
    el.classList.add('offline');
  } else {
    st.className = 'status';
    st.textContent = '等待首次上报';
    el.classList.add('offline');
  }
  setBar(el, 'cpu', live.cpu);
  el.querySelector('[data-v="cpu"]').textContent = percent(live.cpu);
  setBar(el, 'mem', live.mem);
  el.querySelector('[data-v="mem"]').textContent = percent(live.mem);
  setBar(el, 'disk', live.disk);
  el.querySelector('[data-v="disk"]').textContent = percent(live.disk);
  el.querySelector('.rx').textContent = '↓ ' + formatBps(live.rx);
  el.querySelector('.tx').textContent = '↑ ' + formatBps(live.tx);
  const limit = num(node.meta.tLimit);
  if (limit > 0) {
    const used = num(live.tUsed);
    setBar(el, 'traffic', Math.min(1000, Math.round(used * 1000 / limit)));
    el.querySelector('[data-v="traffic"]').textContent = `${formatBytes(used)} / ${formatBytes(limit)}`;
  }
  const specs = el.querySelector('.specs');
  if (!specs.hidden) specs.textContent = specs.dataset.base + (num(live.up) > 0 ? ` · 已运行 ${formatDuration(live.up)}` : '');
  el.querySelector('.uptime').textContent = '';
  el.querySelector('.seen').textContent = ts > 0 ? `最后上报 ${timeAgo(ts, now)}` : '';
}

function renderGrid() {
  const ids = state.order;
  $('empty').hidden = ids.length > 0;
  const frag = document.createDocumentFragment();
  for (const id of ids) {
    const node = state.nodes.get(id);
    if (node) frag.appendChild(ensureCard(node));
  }
  grid.replaceChildren($('empty'), frag);
  scheduleDraw(true);
}

function renderSummary() {
  let online = 0, total = 0;
  for (const node of state.nodes.values()) {
    total++;
    if (node.live && num(node.live.status) === STATUS_ONLINE) online++;
  }
  $('summary').textContent = `在线 ${online} / ${total}`;
}

let drawAll = false, drawScheduled = false;
function scheduleDraw(all) {
  if (all) drawAll = true;
  if (drawScheduled) return;
  drawScheduled = true;
  requestAnimationFrame(() => {
    drawScheduled = false;
    if (document.hidden) { drawAll = true; return; }
    const dark = isDark();
    const targets = drawAll ? Array.from(state.nodes.values()) : Array.from(state.dirty, (id) => state.nodes.get(id)).filter(Boolean);
    drawAll = false;
    state.dirty.clear();
    for (const node of targets) {
      if (node.el) drawSparkline(node.el.querySelector('canvas'), node.hist, { dark });
    }
  });
}
document.addEventListener('visibilitychange', () => { if (!document.hidden) scheduleDraw(true); });
window.addEventListener('resize', () => scheduleDraw(true));

// ---------------------------------------------------------------- message handlers
function sortOrder() {
  state.order = Array.from(state.nodes.values())
    .sort((a, b) => (num(a.meta.order) - num(b.meta.order)) || (num(a.meta.id) - num(b.meta.id)))
    .map((n) => n.meta.id);
}

function applySite(site) {
  if (!site) return;
  state.site = { ...state.site, ...site };
  $('site-title').textContent = state.site.title || '节点状态';
  document.title = state.site.title || '节点状态';
  const sub = $('site-subtitle');
  sub.textContent = state.site.subtitle || '';
  sub.hidden = !state.site.subtitle;
}

function applySnapshot(snapshot) {
  applySite(snapshot.site);
  const now = num(snapshot.ts) || Date.now();
  const seen = new Set();
  for (const n of snapshot.nodes || []) {
    const id = num(n.id);
    seen.add(id);
    let node = state.nodes.get(id);
    if (!node) { node = { meta: n, live: n.live, hist: null, el: null }; state.nodes.set(id, node); }
    node.meta = n;
    node.live = n.live || node.live;
    node.hist = n.hist ? { cpu: [...(n.hist.cpu || [])], mem: [...(n.hist.mem || [])], rx: [...(n.hist.rx || [])], tx: [...(n.hist.tx || [])] } : (node.hist || { cpu: [], mem: [], rx: [], tx: [] });
    renderMeta(node);
    renderLive(node, now);
  }
  for (const id of Array.from(state.nodes.keys())) if (!seen.has(id)) state.nodes.delete(id);
  sortOrder();
  renderGrid();
  renderSummary();
  state.lastUpdate = Date.now();
}

function applyBatch(batch) {
  const now = num(batch.ts) || Date.now();
  for (const live of batch.items || []) {
    const node = state.nodes.get(num(live.id));
    if (!node) continue;
    node.live = live;
    if (num(live.status) === STATUS_ONLINE) node.hist = pushPoint(node.hist, live);
    renderLive(node, now);
    state.dirty.add(num(live.id));
  }
  renderSummary();
  state.lastUpdate = Date.now();
  scheduleDraw(false);
  setStatus();
}

function applyNodes(list) {
  const seen = new Set();
  for (const n of list || []) {
    const id = num(n.id);
    seen.add(id);
    let node = state.nodes.get(id);
    if (!node) { node = { meta: n, live: n.live, hist: { cpu: [], mem: [], rx: [], tx: [] }, el: null }; state.nodes.set(id, node); }
    node.meta = n;
    if (n.live) node.live = n.live;
    renderMeta(node);
    renderLive(node, Date.now());
  }
  for (const id of Array.from(state.nodes.keys())) if (!seen.has(id)) state.nodes.delete(id);
  sortOrder();
  renderGrid();
  renderSummary();
}

// ---------------------------------------------------------------- connection
function setBanner(text) {
  const b = $('banner');
  if (text) { b.textContent = text; b.hidden = false; } else { b.hidden = true; }
}
function setStatus() {
  $('conn-status').textContent = state.connected
    ? `已连接 · 最后更新 ${clock(new Date(state.lastUpdate || Date.now()))}`
    : '连接中…';
}

const conn = new signalR.HubConnectionBuilder()
  .withUrl('/hubs/public', { transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling })
  .withHubProtocol(new signalR.protocols.msgpack.MessagePackHubProtocol())
  .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (c) => [0, 2000, 5000, 10000, 30000][c.previousRetryCount] ?? 60000 })
  .configureLogging(signalR.LogLevel.Warning)
  .build();
conn.serverTimeoutInMilliseconds = 45000;
conn.keepAliveIntervalInMilliseconds = 15000;

conn.on('snapshot', (s) => { applySnapshot(s); state.connected = true; setBanner(''); setStatus(); });
conn.on('batch', applyBatch);
conn.on('nodes', applyNodes);
conn.onreconnecting(() => { state.connected = false; setBanner('连接中断,正在重连…'); setStatus(); });
conn.onreconnected(async () => {
  try { applySnapshot(await conn.invoke('GetSnapshot')); } catch (e) { /* the server pushes a snapshot on connect anyway */ }
  state.connected = true; setBanner(''); setStatus();
});
conn.onclose(() => { state.connected = false; setBanner('连接已关闭,请刷新页面'); setStatus(); });

function start() {
  conn.start().then(() => { state.connected = true; setBanner(''); setStatus(); }).catch(() => {
    setBanner('无法连接到服务器,5 秒后重试…');
    setTimeout(start, 5000);
  });
}
start();

// ---------------------------------------------------------------- clock + relative times
setInterval(() => {
  $('clock').textContent = clock();
  const now = Date.now();
  for (const node of state.nodes.values()) {
    if (!node.el || !node.live) continue;
    const ts = num(node.live.ts);
    if (num(node.live.status) === STATUS_OFFLINE) node.el.querySelector('.status').textContent = ts > 0 ? `离线 · ${formatDuration((now - ts) / 1000)}` : '离线';
    node.el.querySelector('.seen').textContent = ts > 0 ? `最后上报 ${timeAgo(ts, now)}` : '';
  }
}, 1000);
