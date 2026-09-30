import { createClient, drawSparkline, fmt } from "/vendor/snm-client.js";
import { probeStats } from "./probes.js";

const $ = (id) => document.getElementById(id);
const client = createClient({ theme: "panorama" });
const cards = new Map();
let filter = "all",
  query = "",
  received = false,
  scheduled = false;
const pref = (key, value) => {
  try {
    if (value === undefined) return localStorage.getItem(key);
    localStorage.setItem(key, value);
  } catch {
    /* storage disabled */
  }
};
const media = matchMedia("(prefers-color-scheme: dark)");
let manualTheme = pref("panorama-color");
function setTheme() {
  document.documentElement.dataset.theme =
    manualTheme || (media.matches ? "dark" : "light");
  draw();
}
$("theme-toggle").onclick = () => {
  manualTheme =
    document.documentElement.dataset.theme === "dark" ? "light" : "dark";
  pref("panorama-color", manualTheme);
  setTheme();
};
media.addEventListener("change", setTheme);
setTheme();

function createCard(node) {
  const el = document.createElement("article");
  el.className = "node";
  // Only fixed markup is inserted; names and all server values use textContent.
  el.innerHTML = `<div class="node-main"><div class="node-header"><span class="flag"></span><div class="node-title"><h3></h3><span class="node-country"></span></div><span class="status"></span></div><div class="specs"></div><div class="metrics">${["cpu", "mem", "disk"].map((k, i) => `<div><div class="metric-label"><span>${["CPU", "内存", "磁盘"][i]}</span><strong data-value="${k}"></strong></div><div class="bar ${k}"><i data-bar="${k}"></i></div></div>`).join("")}</div><div class="network"><span class="rx"><em>↓</em>下载<b></b></span><span class="tx"><em>↑</em>上传<b></b></span></div><canvas class="spark" role="img" aria-label="最近两分钟 CPU 与网络速率趋势"></canvas><div class="traffic"><span>本月流量</span><span class="traffic-value"></span></div><div class="node-foot"><span class="uptime"></span><span class="seen"></span></div></div><div class="probe-section"><div class="probe-heading">网络质量<span>LATENCY</span></div><div class="probe-list"></div></div>`;
  cards.set(node.id, el);
  return el;
}

function renderCard(node) {
  const el = cards.get(node.id) || createCard(node),
    live = node.live,
    site = client.state.site;
  const text = (selector, value) => {
    el.querySelector(selector).textContent = value;
  };
  text("h3", node.name);
  el.querySelector("h3").title = node.name;
  text(".flag", fmt.flag(node.cc));
  text(".node-country", fmt.country(node.cc) || "地区待识别");
  const status = el.querySelector(".status");
  status.className = `status ${live.online ? "" : live.offline ? "offline" : "unknown"}`;
  status.textContent = live.online
    ? "● 在线"
    : live.offline
      ? "● 离线"
      : "○ 等待上报";
  const specs = el.querySelector(".specs");
  specs.hidden = !site.showSpecs;
  specs.textContent = `${node.cores || "—"} vCPU　·　${fmt.mb(node.memMb)} 内存　·　${fmt.mb(node.diskMb)} 磁盘`;
  for (const k of ["cpu", "mem", "disk"]) {
    text(`[data-value="${k}"]`, live.ts ? fmt.percent(live[k]) : "—");
    const bar = el.querySelector(`[data-bar="${k}"]`);
    bar.style.width = Math.max(0, Math.min(100, live[k] / 10)) + "%";
    bar.className = live[k] >= 900 ? "hot" : "";
  }
  text(".rx b", live.online ? fmt.bps(live.rx) : "—");
  text(".tx b", live.online ? fmt.bps(live.tx) : "—");
  el.querySelector(".traffic").hidden = !(site.showTraffic && node.tLimit > 0);
  text(
    ".traffic-value",
    `${fmt.bytes(live.tUsed)} / ${fmt.bytes(node.tLimit)}`,
  );
  text(".uptime", live.up ? `运行 ${fmt.duration(live.up)}` : "等待首次上报");
  text(".seen", live.ts ? fmt.ago(live.ts) + "上报" : "");
  renderProbes(el, node);
}

function renderProbes(el, node) {
  const list = el.querySelector(".probe-list");
  list.replaceChildren();
  if (!node.probes?.length) {
    const empty = document.createElement("div");
    empty.className = "probe-empty";
    empty.textContent = "尚未配置探测线路";
    list.append(empty);
    return;
  }
  for (const probe of node.probes) {
    const row = document.createElement("div");
    row.className = "probe-row";
    row.dataset.probe = probe.id;
    row.innerHTML =
      '<div class="probe-name"><span></span><small></small></div><canvas class="probe-chart" role="img"></canvas><div class="probe-value"><span></span><small></small></div>';
    row.querySelector(".probe-name span").textContent = probe.name;
    row.querySelector(".probe-name").title = probe.name;
    row.querySelector(".probe-name small").textContent =
      probe.kind === 0 ? "ICMP · RTT" : "TCP · 建连";
    row
      .querySelector("canvas")
      .setAttribute(
        "aria-label",
        `${probe.name} 最近 ${probe.points.length} 次延时趋势`,
      );
    const stats = probeStats(probe),
      last = stats.last;
    let value = "未测量",
      tone = "muted";
    if (!node.live.online) value = node.live.offline ? "节点离线" : "等待上报";
    else if (stats.stale) value = "数据过期";
    else if (last) {
      value =
        last.state === 0
          ? `${(last.us / 1000).toFixed(1)} ms`
          : ["成功", "超时", "探测错误", "不支持"][last.state] || "未知";
      tone = last.state === 0 ? "" : last.state === 3 ? "muted" : "bad";
    }
    const display = row.querySelector(".probe-value");
    display.className = `probe-value ${tone}`;
    display.querySelector("span").textContent = value;
    display.querySelector("small").textContent =
      stats.failurePct === null
        ? "暂无统计"
        : `${probe.kind === 0 ? "丢包" : "失败"} ${stats.failurePct.toFixed(0)}%`;
    row.title = `最近 ${probe.points.length} 次；有效样本 ${stats.samples} 次；平均 ${stats.averageMs === null ? "—" : stats.averageMs.toFixed(1) + " ms"}${last ? "；最后测量 " + fmt.ago(last.ts) : ""}`;
    list.append(row);
  }
}

function visible(node) {
  return (
    (filter === "all" ||
      (filter === "online" ? node.live.online : !node.live.online)) &&
    `${node.name} ${node.cc} ${fmt.country(node.cc)}`
      .toLocaleLowerCase()
      .includes(query)
  );
}
function layout() {
  const nodes = client.state.nodes,
    fragment = document.createDocumentFragment();
  for (const n of nodes)
    if (visible(n)) {
      const el = cards.get(n.id) || createCard(n);
      fragment.append(el);
    }
  $("nodes").replaceChildren(fragment);
  for (const id of cards.keys())
    if (!client.state.byId.has(id)) cards.delete(id);
  $("empty").hidden = $("nodes").childElementCount > 0;
  $("empty-title").textContent = received
    ? nodes.length
      ? "没有匹配的节点"
      : "暂无公开节点"
    : "正在连接监控中心";
  $("empty-description").textContent = received
    ? nodes.length
      ? "试试其他名称、地区或状态。"
      : "管理员公开节点后会自动显示在这里。"
    : "节点实时数据将在连接成功后显示。";
  draw();
}
function summary() {
  const nodes = client.state.nodes,
    online = nodes.filter((n) => n.live.online);
  $("online").textContent = online.length;
  $("total").textContent = nodes.length;
  $("count").textContent = nodes.length;
  $("regions").textContent = new Set(
    nodes.map((n) => n.cc).filter(Boolean),
  ).size;
  $("download").textContent = fmt.bps(
    online.reduce((sum, n) => sum + n.live.rx, 0),
  );
  $("upload").textContent = fmt.bps(
    online.reduce((sum, n) => sum + n.live.tx, 0),
  );
  $("health-text").textContent = nodes.length
    ? online.length === nodes.length
      ? "所有节点运行正常"
      : `${nodes.length - online.length} 个节点离线或等待上报`
    : "暂无公开节点";
}
function all(state) {
  received = true;
  state.nodes.forEach(renderCard);
  summary();
  layout();
}
client.on("snapshot", all);
client.on("nodes", all);
client.on("update", (state, ids) => {
  ids.forEach((id) => renderCard(state.byId.get(id)));
  summary();
  layout();
});
client.on("probes", (state, id) => {
  const el = cards.get(id);
  if (el) renderProbes(el, state.byId.get(id));
  draw();
});
client.on("site", (site) => {
  $("site-title").textContent = site.title;
  document.title = site.title + " · 全景监控";
  $("subtitle").textContent = site.subtitle || "实时性能与网络质量，一目了然。";
  const color = site.options?.accent;
  if (typeof color === "string" && /^#[0-9a-f]{6}$/i.test(color))
    document.documentElement.style.setProperty("--accent", color);
  else document.documentElement.style.removeProperty("--accent");
});
client.on("connection", ({ status }) => {
  $("connection-dot").className = status;
  $("connection-text").textContent =
    {
      connected: "实时连接",
      reconnecting: "正在重连",
      disconnected: "连接中断",
      connecting: "正在连接",
    }[status] || "未连接";
  $("banner").hidden = status === "connected" || status === "connecting";
  $("banner").textContent =
    "与监控中心的连接已中断，正在自动重试。以下为最后收到的数据。";
});
client.on("tick", () => {
  $("clock").textContent = fmt.clock();
  for (const n of client.state.nodes) {
    const el = cards.get(n.id);
    if (!el?.isConnected) continue;
    el.querySelector(".seen").textContent = n.live.ts
      ? fmt.ago(n.live.ts) + "上报"
      : "";
    renderProbes(el, n);
  }
  draw();
});
document.querySelectorAll("[data-filter]").forEach(
  (button) =>
    (button.onclick = () => {
      filter = button.dataset.filter;
      document.querySelectorAll("[data-filter]").forEach((b) => {
        b.classList.toggle("active", b === button);
        b.setAttribute("aria-pressed", String(b === button));
      });
      layout();
    }),
);
$("search").addEventListener("input", (event) => {
  query = event.target.value.trim().toLocaleLowerCase();
  layout();
});
function setView(view) {
  $("nodes").classList.toggle("list", view === "list");
  document.querySelectorAll("[data-view]").forEach((b) => {
    const active = b.dataset.view === view;
    b.classList.toggle("active", active);
    b.setAttribute("aria-pressed", String(active));
  });
  draw();
}
document.querySelectorAll("[data-view]").forEach(
  (b) =>
    (b.onclick = () => {
      pref("panorama-view", b.dataset.view);
      setView(b.dataset.view);
    }),
);
setView(pref("panorama-view") === "list" ? "list" : "grid");

function drawProbe(canvas, points) {
  const r = canvas.getBoundingClientRect(),
    dpr = devicePixelRatio || 1,
    w = r.width,
    h = r.height;
  canvas.width = Math.max(1, Math.round(w * dpr));
  canvas.height = Math.max(1, Math.round(h * dpr));
  const ctx = canvas.getContext("2d");
  ctx.scale(dpr, dpr);
  const accent = getComputedStyle(document.documentElement)
    .getPropertyValue("--accent")
    .trim();
  const max = Math.max(
    1000,
    ...points.filter((p) => p.state === 0).map((p) => p.us),
  );
  const x = (i) => 2 + (i / Math.max(1, points.length - 1)) * (w - 4);
  ctx.strokeStyle = accent;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  let line = false;
  points.forEach((p, i) => {
    if (p.state !== 0 || p.us < 0) {
      line = false;
      return;
    }
    const y = h - 3 - (p.us / max) * (h - 6);
    if (line) ctx.lineTo(x(i), y);
    else ctx.moveTo(x(i), y);
    line = true;
  });
  ctx.stroke();
  points.forEach((p, i) => {
    ctx.fillStyle =
      p.state === 0 ? accent : p.state === 3 ? "#8996aa" : "#d85466";
    const y = p.state === 0 ? h - 3 - (p.us / max) * (h - 6) : h / 2;
    ctx.beginPath();
    ctx.arc(
      x(i),
      y,
      points.length < 4 || p.state !== 0 ? 2 : 0.8,
      0,
      Math.PI * 2,
    );
    ctx.fill();
  });
}
function draw() {
  if (scheduled) return;
  scheduled = true;
  requestAnimationFrame(() => {
    scheduled = false;
    if (document.hidden) return;
    for (const n of client.state.nodes) {
      const el = cards.get(n.id);
      if (!el?.isConnected) continue;
      drawSparkline(el.querySelector(".spark"), n.hist, {
        dark: document.documentElement.dataset.theme === "dark",
      });
      for (const p of n.probes || []) {
        const canvas = el.querySelector(`[data-probe="${p.id}"] canvas`);
        if (canvas) drawProbe(canvas, p.points);
      }
    }
  });
}
window.addEventListener("resize", draw);
document.addEventListener("visibilitychange", draw);
client.start();
