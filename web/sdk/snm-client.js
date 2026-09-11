/*!
 * snm-client.js — Server Node Monitor public dashboard SDK (theme-facing API), SDK version 1.
 *
 * Themes load the SignalR browser bundles and this file from /vendor/ (served by the master) and only
 * render what the client hands them. The client owns: the hub connection (MessagePack), reconnects,
 * snapshot/batch/nodes handling, BigInt normalisation, derived percentages, the 60-point history per
 * node and the "active theme changed → reload" behaviour. See docs/THEMES.md.
 *
 *   import { createClient, fmt, drawSparkline } from '/vendor/snm-client.js'
 *   const client = createClient({ theme: 'my-theme' })
 *   client.on('snapshot', state => renderAll(state))
 *   client.on('update', (state, changedIds) => renderSome(changedIds))
 *   client.start()
 */
export const SDK_VERSION = 1;

// ---------------------------------------------------------------- formatting helpers
export function num(v) {
  if (typeof v === 'bigint') return Number(v);
  return typeof v === 'number' ? v : (Number(v) || 0);
}

function scale(value, units, base) {
  let b = num(value);
  let i = 0;
  while (b >= base && i < units.length - 1) { b /= base; i++; }
  const digits = i === 0 ? 0 : b < 10 ? 2 : b < 100 ? 1 : 0;
  return `${b.toFixed(digits)} ${units[i]}`;
}

export const fmt = {
  num,
  /** Traffic bytes, decimal units (1 TB = 1000 GB, as ISPs bill). */
  bytes: (v) => scale(v, ['B', 'KB', 'MB', 'GB', 'TB', 'PB'], 1000),
  /** Memory / disk sizes given in MiB. */
  mb(mb) {
    const m = num(mb);
    if (m >= 1024 * 1024) return `${(m / 1024 / 1024).toFixed(1)} TB`;
    if (m >= 1024) return `${(m / 1024).toFixed(m >= 10240 ? 0 : 1)} GB`;
    return `${m} MB`;
  },
  bps: (v) => scale(v, ['B/s', 'KB/s', 'MB/s', 'GB/s'], 1000),
  bits: (v) => scale(num(v) * 8, ['bps', 'Kbps', 'Mbps', 'Gbps'], 1000),
  duration(seconds) {
    const s = Math.max(0, Math.floor(num(seconds)));
    const d = Math.floor(s / 86400), h = Math.floor((s % 86400) / 3600), m = Math.floor((s % 3600) / 60);
    if (d > 0) return `${d} 天 ${h} 小时`;
    if (h > 0) return `${h} 小时 ${m} 分`;
    if (m > 0) return `${m} 分 ${s % 60} 秒`;
    return `${s} 秒`;
  },
  ago(ms, now = Date.now()) {
    const sec = Math.max(0, Math.round((now - num(ms)) / 1000));
    if (sec < 60) return `${sec} 秒前`;
    if (sec < 3600) return `${Math.floor(sec / 60)} 分钟前`;
    if (sec < 86400) return `${Math.floor(sec / 3600)} 小时前`;
    return `${Math.floor(sec / 86400)} 天前`;
  },
  /** permille (0-1000) -> "23.7%" */
  percent: (permille, digits = 1) => `${(num(permille) / 10).toFixed(digits)}%`,
  /** ISO 3166-1 alpha-2 -> regional indicator emoji; unknown -> globe. */
  flag(cc) {
    if (!cc || !/^[A-Za-z]{2}$/.test(cc)) return '🌐';
    const up = cc.toUpperCase();
    return String.fromCodePoint(0x1F1E6 + up.charCodeAt(0) - 65, 0x1F1E6 + up.charCodeAt(1) - 65);
  },
  /** Localised country name when Intl.DisplayNames is available. */
  country(cc, locale = 'zh-CN') {
    if (!cc) return '';
    try { return new Intl.DisplayNames([locale], { type: 'region' }).of(cc.toUpperCase()) || cc; } catch { return cc; }
  },
  clock(date = new Date()) {
    const p = (n) => String(n).padStart(2, '0');
    return `${p(date.getHours())}:${p(date.getMinutes())}:${p(date.getSeconds())}`;
  },
};

// ---------------------------------------------------------------- sparkline (canvas)
export const HISTORY_POINTS = 60;

/** Draws CPU as an area (0-100%) plus rx (solid) / tx (dashed) lines scaled to the window maximum. */
export function drawSparkline(canvas, hist, opts = {}) {
  const dark = !!opts.dark;
  const points = opts.points || HISTORY_POINTS;
  const rect = canvas.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.floor(rect.width)), h = Math.max(1, Math.floor(rect.height));
  if (canvas.width !== Math.floor(w * dpr) || canvas.height !== Math.floor(h * dpr)) { canvas.width = Math.floor(w * dpr); canvas.height = Math.floor(h * dpr); }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);
  const cpu = hist?.cpu || [], rx = hist?.rx || [], tx = hist?.tx || [];
  const pad = 2, innerH = h - pad * 2, step = (w - pad * 2) / (points - 1);
  const x = (i, len) => pad + (points - len + i) * step;
  ctx.strokeStyle = dark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.06)';
  ctx.lineWidth = 1;
  ctx.beginPath(); ctx.moveTo(pad, h - pad + 0.5); ctx.lineTo(w - pad, h - pad + 0.5); ctx.stroke();
  if (!cpu.length && !rx.length && !tx.length) return;
  const primary = opts.cpuColor || (dark ? '90,162,255' : '47,128,237');
  if (cpu.length) {
    ctx.beginPath();
    ctx.moveTo(x(0, cpu.length), h - pad);
    cpu.forEach((v, i) => ctx.lineTo(x(i, cpu.length), h - pad - (Math.min(1000, Math.max(0, num(v))) / 1000) * innerH));
    ctx.lineTo(x(cpu.length - 1, cpu.length), h - pad);
    ctx.closePath();
    ctx.fillStyle = `rgba(${primary},0.22)`; ctx.fill();
    ctx.beginPath();
    cpu.forEach((v, i) => { const px = x(i, cpu.length), py = h - pad - (Math.min(1000, Math.max(0, num(v))) / 1000) * innerH; i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
    ctx.strokeStyle = `rgba(${primary},0.9)`; ctx.lineWidth = 1.2; ctx.stroke();
  }
  let max = 10 * 1000;
  for (const v of rx) max = Math.max(max, num(v));
  for (const v of tx) max = Math.max(max, num(v));
  const line = (arr, color, dash) => {
    if (!arr.length) return;
    ctx.beginPath(); ctx.setLineDash(dash);
    arr.forEach((v, i) => { const px = x(i, arr.length), py = h - pad - (Math.max(0, num(v)) / max) * innerH * 0.9; i ? ctx.lineTo(px, py) : ctx.moveTo(px, py); });
    ctx.strokeStyle = color; ctx.lineWidth = 1.4; ctx.stroke(); ctx.setLineDash([]);
  };
  line(rx, opts.rxColor || (dark ? '#5aa2ff' : '#2f80ed'), []);
  line(tx, opts.txColor || (dark ? '#c084fc' : '#a855f7'), [4, 3]);
}

function pushPoint(hist, live, max) {
  hist.cpu.push(live.cpu); hist.mem.push(live.mem); hist.rx.push(live.rx); hist.tx.push(live.tx); hist.ts.push(live.ts);
  for (const k of ['cpu', 'mem', 'rx', 'tx', 'ts']) if (hist[k].length > max) hist[k].splice(0, hist[k].length - max);
}

// ---------------------------------------------------------------- client
export const STATUS = { UNKNOWN: 0, ONLINE: 1, OFFLINE: 2 };

const SITE_DEFAULTS = { title: '节点状态', subtitle: '', showSpecs: true, showTraffic: true, offlineSec: 30, theme: 'default', options: {} };

function normalizeLive(l, meta) {
  const cpu = num(l.cpu), mem = num(l.mem), disk = num(l.disk), tUsed = num(l.tUsed), status = num(l.status);
  const tLimit = num(meta?.tLimit);
  return {
    id: num(l.id), status, online: status === STATUS.ONLINE, offline: status === STATUS.OFFLINE, unknown: status === STATUS.UNKNOWN,
    cpu, mem, disk, cpuPct: cpu / 10, memPct: mem / 10, diskPct: disk / 10,
    rx: num(l.rx), tx: num(l.tx), up: num(l.up), tUsed, trafficPct: tLimit > 0 ? (tUsed * 100) / tLimit : null, ts: num(l.ts),
  };
}

function normalizeMeta(n) {
  return { id: num(n.id), name: n.name || `节点 ${num(n.id)}`, cc: n.cc || '', order: num(n.order), cores: num(n.cores), memMb: num(n.memMb), diskMb: num(n.diskMb), tLimit: num(n.tLimit) };
}

function emptyHist() { return { ts: [], cpu: [], mem: [], rx: [], tx: [] }; }

export function createClient(options = {}) {
  const opts = {
    hubUrl: '/hubs/public',
    theme: null,                 // id of the running theme; enables auto-reload when the admin switches themes
    autoReload: true,
    historyPoints: HISTORY_POINTS,
    retryDelays: [0, 2000, 5000, 10000, 30000],
    logLevel: 'warning',
    tick: true,
    ...options,
  };
  const listeners = new Map();
  const state = {
    site: { ...SITE_DEFAULTS },
    nodes: [],                   // sorted by order, id
    byId: new Map(),
    connection: 'idle',          // idle | connecting | connected | reconnecting | disconnected
    error: null,
    lastUpdate: 0,
    serverTs: 0,
    sdkVersion: SDK_VERSION,
  };

  function on(event, fn) { (listeners.get(event) || listeners.set(event, new Set()).get(event)).add(fn); return () => off(event, fn); }
  function off(event, fn) { listeners.get(event)?.delete(fn); }
  function emit(event, ...args) { listeners.get(event)?.forEach((fn) => { try { fn(...args); } catch (e) { console.error(`[snm] ${event} handler failed`, e); } }); }

  function setConnection(status, error = null) {
    state.connection = status; state.error = error;
    emit('connection', { status, error }, state);
  }

  function sortNodes() {
    state.nodes = Array.from(state.byId.values()).sort((a, b) => (a.order - b.order) || (a.id - b.id));
  }

  function applySite(site) {
    if (!site) return;
    let parsed = {};
    if (site.opts) { try { parsed = JSON.parse(site.opts) || {}; } catch { parsed = {}; } }
    const next = {
      title: site.title ?? state.site.title, subtitle: site.subtitle ?? '', showSpecs: site.showSpecs !== false, showTraffic: site.showTraffic !== false,
      offlineSec: num(site.offlineSec) || 30, theme: site.theme || 'default', options: parsed,
    };
    state.site = next;
    emit('site', next, state);
    if (opts.autoReload && opts.theme && next.theme !== opts.theme && location.pathname === '/') {
      console.info(`[snm] active theme changed to "${next.theme}", reloading`);
      setTimeout(() => location.reload(), 500);
    }
  }

  function upsertNode(n) {
    const meta = normalizeMeta(n);
    const existing = state.byId.get(meta.id);
    const node = existing || { ...meta, live: normalizeLive({}, meta), hist: emptyHist() };
    Object.assign(node, meta);
    if (n.live) node.live = normalizeLive(n.live, node);
    if (n.hist) {
      node.hist = { ts: [], cpu: (n.hist.cpu || []).map(num), mem: (n.hist.mem || []).map(num), rx: (n.hist.rx || []).map(num), tx: (n.hist.tx || []).map(num) };
      node.hist.ts = node.hist.cpu.map(() => 0);
    }
    state.byId.set(meta.id, node);
    return node;
  }

  function applySnapshot(snapshot) {
    applySite(snapshot.site);
    const seen = new Set();
    for (const n of snapshot.nodes || []) seen.add(upsertNode(n).id);
    for (const id of Array.from(state.byId.keys())) if (!seen.has(id)) state.byId.delete(id);
    sortNodes();
    state.serverTs = num(snapshot.ts); state.lastUpdate = Date.now();
    emit('snapshot', state);
  }

  function applyBatch(batch) {
    const changed = [];
    for (const l of batch.items || []) {
      const node = state.byId.get(num(l.id));
      if (!node) continue;
      node.live = normalizeLive(l, node);
      if (node.live.online) pushPoint(node.hist, node.live, opts.historyPoints);
      changed.push(node.id);
    }
    state.serverTs = num(batch.ts); state.lastUpdate = Date.now();
    if (changed.length) emit('update', state, changed);
  }

  function applyNodes(list) {
    const seen = new Set();
    for (const n of list || []) seen.add(upsertNode(n).id);
    for (const id of Array.from(state.byId.keys())) if (!seen.has(id)) state.byId.delete(id);
    sortNodes();
    emit('nodes', state);
  }

  let connection = null, tickTimer = null, stopped = false;

  function build() {
    const sr = typeof signalR !== 'undefined' ? signalR : window.signalR;
    if (!sr || !sr.protocols?.msgpack) throw new Error('snm-client: load /vendor/signalr.min.js and /vendor/signalr-protocol-msgpack.min.js before the theme script');
    const c = new sr.HubConnectionBuilder()
      .withUrl(opts.hubUrl, { transport: sr.HttpTransportType.WebSockets | sr.HttpTransportType.LongPolling })
      .withHubProtocol(new sr.protocols.msgpack.MessagePackHubProtocol())
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => opts.retryDelays[ctx.previousRetryCount] ?? 60000 })
      .configureLogging(sr.LogLevel[opts.logLevel === 'debug' ? 'Debug' : opts.logLevel === 'info' ? 'Information' : 'Warning'])
      .build();
    c.serverTimeoutInMilliseconds = 45000;
    c.keepAliveIntervalInMilliseconds = 15000;
    c.on('snapshot', (s) => { applySnapshot(s); setConnection('connected'); });
    c.on('batch', applyBatch);
    c.on('nodes', applyNodes);
    c.onreconnecting((e) => setConnection('reconnecting', e || null));
    c.onreconnected(async () => {
      try { applySnapshot(await c.invoke('GetSnapshot')); } catch { /* the server also pushes a snapshot on connect */ }
      setConnection('connected');
    });
    c.onclose((e) => setConnection('disconnected', e || null));
    return c;
  }

  async function start() {
    stopped = false;
    if (!connection) connection = build();
    if (opts.tick && !tickTimer) tickTimer = setInterval(() => emit('tick', Date.now(), state), 1000);
    setConnection('connecting');
    while (!stopped) {
      try { await connection.start(); setConnection('connected'); return; }
      catch (e) { setConnection('disconnected', e); await new Promise((r) => setTimeout(r, 5000)); }
    }
  }

  async function stop() {
    stopped = true;
    if (tickTimer) { clearInterval(tickTimer); tickTimer = null; }
    if (connection) { try { await connection.stop(); } catch { /* ignore */ } }
    setConnection('idle');
  }

  return { state, start, stop, on, off, fmt, drawSparkline, STATUS, sdkVersion: SDK_VERSION, refresh: async () => { if (connection) applySnapshot(await connection.invoke('GetSnapshot')); } };
}

export const SNM = { SDK_VERSION, STATUS, HISTORY_POINTS, createClient, fmt, drawSparkline, num };
if (typeof window !== 'undefined') window.SNM = SNM;
export default SNM;
