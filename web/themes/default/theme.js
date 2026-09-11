// Default theme for the Server Node Monitor public dashboard, built on the theme SDK (docs/THEMES.md).
// Rendering only: the connection, state, history buffers and BigInt handling live in /vendor/snm-client.js.
import { createClient, drawSparkline, fmt } from '/vendor/snm-client.js';

const client = createClient({ theme: 'default' });
const $ = (id) => document.getElementById(id);
const grid = $('grid');
const cards = new Map();          // node id -> element
let drawAll = false, drawScheduled = false;
const dirty = new Set();

// ---------------------------------------------------------------- theme options (系统设置 → 大屏主题 → 主题参数)
// { "accent": "#2f80ed", "columns": 340, "showClock": true, "showFooter": true }
function applyOptions(o) {
  const root = document.documentElement;
  if (o.accent) root.style.setProperty('--primary', o.accent);
  if (o.columns) root.style.setProperty('--card-min', `${Number(o.columns) || 340}px`);
  $('clock').hidden = o.showClock === false;
  document.querySelector('.footer').hidden = o.showFooter === false;
}

// ---------------------------------------------------------------- light / dark
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
$('theme-toggle').addEventListener('click', () => { const next = isDark() ? 'light' : 'dark'; localStorage.setItem('snm-theme', next); applyTheme(next); });
if (window.matchMedia) window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => scheduleDraw(true));

// ---------------------------------------------------------------- cards
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
    <div class="card-foot"><span class="seen"></span></div>`;
  return el;
}

function card(node) {
  let el = cards.get(node.id);
  if (!el) { el = cardTemplate(); el.dataset.id = String(node.id); cards.set(node.id, el); }
  return el;
}

function setBar(el, key, pct) {
  const bar = el.querySelector(`i[data-k="${key}"]`);
  const p = Math.min(100, Math.max(0, pct || 0));
  bar.style.width = p + '%';
  bar.className = p >= 90 ? 'bad' : p >= 75 ? 'warn' : '';
}

function renderMeta(node, site) {
  const el = card(node);
  el.querySelector('.flag').textContent = fmt.flag(node.cc);
  el.querySelector('.flag').title = fmt.country(node.cc);
  el.querySelector('.name').textContent = node.name;
  const specs = el.querySelector('.specs');
  if (site.showSpecs && (node.cores || node.memMb || node.diskMb)) { specs.hidden = false; specs.dataset.base = `${node.cores} 核 · ${fmt.mb(node.memMb)} · ${fmt.mb(node.diskMb)}`; }
  else { specs.hidden = true; specs.dataset.base = ''; }
  el.querySelector('.traffic').hidden = !(site.showTraffic && node.tLimit > 0);
}

function renderLive(node, now) {
  const el = card(node);
  const l = node.live;
  const st = el.querySelector('.status');
  if (l.online) { st.className = 'status online'; st.textContent = '在线'; el.classList.remove('offline'); }
  else if (l.offline) { st.className = 'status offline'; st.textContent = l.ts ? `离线 · ${fmt.duration((now - l.ts) / 1000)}` : '离线'; el.classList.add('offline'); }
  else { st.className = 'status'; st.textContent = '等待首次上报'; el.classList.add('offline'); }
  setBar(el, 'cpu', l.cpuPct); el.querySelector('[data-v="cpu"]').textContent = fmt.percent(l.cpu);
  setBar(el, 'mem', l.memPct); el.querySelector('[data-v="mem"]').textContent = fmt.percent(l.mem);
  setBar(el, 'disk', l.diskPct); el.querySelector('[data-v="disk"]').textContent = fmt.percent(l.disk);
  el.querySelector('.rx').textContent = '↓ ' + fmt.bps(l.rx);
  el.querySelector('.tx').textContent = '↑ ' + fmt.bps(l.tx);
  if (node.tLimit > 0) { setBar(el, 'traffic', l.trafficPct); el.querySelector('[data-v="traffic"]').textContent = `${fmt.bytes(l.tUsed)} / ${fmt.bytes(node.tLimit)}`; }
  const specs = el.querySelector('.specs');
  if (!specs.hidden) specs.textContent = specs.dataset.base + (l.up > 0 ? ` · 已运行 ${fmt.duration(l.up)}` : '');
  el.querySelector('.seen').textContent = l.ts ? `最后上报 ${fmt.ago(l.ts, now)}` : '';
}

function renderGrid(state) {
  $('empty').hidden = state.nodes.length > 0;
  const frag = document.createDocumentFragment();
  for (const node of state.nodes) frag.appendChild(card(node));
  for (const id of Array.from(cards.keys())) if (!state.byId.has(id)) cards.delete(id);
  grid.replaceChildren($('empty'), frag);
  scheduleDraw(true);
}

function renderSummary(state) {
  const online = state.nodes.filter((n) => n.live.online).length;
  $('summary').textContent = `在线 ${online} / ${state.nodes.length}`;
}

function scheduleDraw(all) {
  if (all) drawAll = true;
  if (drawScheduled) return;
  drawScheduled = true;
  requestAnimationFrame(() => {
    drawScheduled = false;
    if (document.hidden) { drawAll = true; return; }
    const dark = isDark();
    const ids = drawAll ? Array.from(cards.keys()) : Array.from(dirty);
    drawAll = false; dirty.clear();
    for (const id of ids) {
      const el = cards.get(id), node = client.state.byId.get(id);
      if (el && node) drawSparkline(el.querySelector('canvas'), node.hist, { dark });
    }
  });
}
document.addEventListener('visibilitychange', () => { if (!document.hidden) scheduleDraw(true); });
window.addEventListener('resize', () => scheduleDraw(true));

// ---------------------------------------------------------------- SDK events
function renderAll(state) {
  const now = Date.now();
  for (const node of state.nodes) { renderMeta(node, state.site); renderLive(node, now); }
  renderGrid(state);
  renderSummary(state);
}

client.on('site', (site) => {
  $('site-title').textContent = site.title;
  document.title = site.title;
  const sub = $('site-subtitle');
  sub.textContent = site.subtitle; sub.hidden = !site.subtitle;
  applyOptions(site.options || {});
});
client.on('snapshot', (state) => { renderAll(state); setStatus(state); });
client.on('nodes', renderAll);
client.on('update', (state, ids) => {
  const now = Date.now();
  for (const id of ids) { const node = state.byId.get(id); if (node) { renderLive(node, now); dirty.add(id); } }
  renderSummary(state);
  scheduleDraw(false);
  setStatus(state);
});
client.on('tick', (now, state) => {
  $('clock').textContent = fmt.clock();
  for (const node of state.nodes) {
    const el = cards.get(node.id);
    if (!el) continue;
    if (node.live.offline && node.live.ts) el.querySelector('.status').textContent = `离线 · ${fmt.duration((now - node.live.ts) / 1000)}`;
    el.querySelector('.seen').textContent = node.live.ts ? `最后上报 ${fmt.ago(node.live.ts, now)}` : '';
  }
});
client.on('connection', ({ status }, state) => {
  const banner = $('banner');
  const text = { reconnecting: '连接中断,正在重连…', disconnected: '无法连接到服务器,正在重试…' }[status];
  if (text) { banner.textContent = text; banner.hidden = false; } else banner.hidden = true;
  setStatus(state);
});

function setStatus(state) {
  $('conn-status').textContent = state.connection === 'connected' ? `已连接 · 最后更新 ${fmt.clock(new Date(state.lastUpdate || Date.now()))}` : '连接中…';
}

client.start();
