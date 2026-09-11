// minimal theme: the smallest useful consumer of the SNM theme SDK (docs/THEMES.md).
import { createClient, fmt } from '/vendor/snm-client.js';

const client = createClient({ theme: 'minimal' });
const $ = (id) => document.getElementById(id);
const rows = $('rows');

function bar(pct) {
  const p = Math.min(100, Math.max(0, pct || 0));
  const cls = p >= 90 ? 'bad' : p >= 75 ? 'warn' : '';
  return `<span class="bar"><i class="${cls}" style="width:${p}%"></i></span>${p.toFixed(1)}%`;
}

function row(node, now) {
  const l = node.live;
  const status = l.online ? '<span class="dot online"></span>在线' : l.offline ? `<span class="dot offline"></span>离线 ${fmt.duration((now - l.ts) / 1000)}` : '<span class="dot"></span>等待上报';
  const traffic = node.tLimit > 0 ? `${fmt.bytes(l.tUsed)} / ${fmt.bytes(node.tLimit)} (${(l.trafficPct || 0).toFixed(0)}%)` : fmt.bytes(l.tUsed);
  return `<tr class="${l.online ? '' : 'offline'}" data-id="${node.id}">
    <td class="name"><span class="cc" title="${fmt.country(node.cc)}">${fmt.flag(node.cc)}</span>${node.name}</td>
    <td>${status}</td>
    <td>${bar(l.cpuPct)}</td>
    <td>${bar(l.memPct)}</td>
    <td>${bar(l.diskPct)}</td>
    <td>↓ ${fmt.bps(l.rx)}</td>
    <td>↑ ${fmt.bps(l.tx)}</td>
    <td>${client.state.site.showTraffic ? traffic : '—'}</td>
    <td>${l.up ? fmt.duration(l.up) : '—'}</td>
    <td>${l.ts ? fmt.ago(l.ts, now) : '—'}</td>
  </tr>`;
}

function renderAll(state) {
  $('title').textContent = state.site.title;
  document.title = state.site.title;
  $('subtitle').textContent = state.site.subtitle;
  $('subtitle').hidden = !state.site.subtitle;
  const now = Date.now();
  rows.innerHTML = state.nodes.length ? state.nodes.map((n) => row(n, now)).join('') : '<tr><td colspan="10" class="empty">暂无节点</td></tr>';
  summary(state);
}

function summary(state) {
  const online = state.nodes.filter((n) => n.live.online).length;
  $('summary').textContent = `在线 ${online} / ${state.nodes.length}`;
}

client.on('snapshot', renderAll);
client.on('nodes', renderAll);
client.on('update', (state, ids) => {
  const now = Date.now();
  for (const id of ids) {
    const tr = rows.querySelector(`tr[data-id="${id}"]`);
    const node = state.byId.get(id);
    if (tr && node) tr.outerHTML = row(node, now);
  }
  summary(state);
});
client.on('tick', (now, state) => {
  // refresh relative times once a minute-ish without re-rendering everything
  if (now % 10000 < 1000) renderAll(state);
});
client.on('connection', ({ status }) => {
  const el = $('conn');
  el.className = 'conn ' + (status === 'connected' ? 'ok' : 'bad');
  el.textContent = { connected: '实时连接正常', connecting: '连接中…', reconnecting: '连接中断,正在重连…', disconnected: '已断开,重试中…' }[status] || status;
});
client.start();
