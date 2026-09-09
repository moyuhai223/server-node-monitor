// Canvas sparkline: CPU area (left axis, 0-1000 permille) + rx solid / tx dashed (right axis, adaptive).

const POINTS = 60;

export function drawSparkline(canvas, hist, opts) {
  const dark = !!(opts && opts.dark);
  const rect = canvas.getBoundingClientRect();
  const dpr = window.devicePixelRatio || 1;
  const w = Math.max(1, Math.floor(rect.width));
  const h = Math.max(1, Math.floor(rect.height));
  if (canvas.width !== Math.floor(w * dpr) || canvas.height !== Math.floor(h * dpr)) {
    canvas.width = Math.floor(w * dpr);
    canvas.height = Math.floor(h * dpr);
  }
  const ctx = canvas.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const cpu = hist && hist.cpu ? hist.cpu : [];
  const rx = hist && hist.rx ? hist.rx : [];
  const tx = hist && hist.tx ? hist.tx : [];
  const n = Math.max(cpu.length, rx.length, tx.length);
  const pad = 2;
  const innerH = h - pad * 2;
  const step = (w - pad * 2) / (POINTS - 1);
  const x = (i, len) => pad + (POINTS - len + i) * step;   // right-aligned when fewer than 60 points

  const grid = dark ? 'rgba(255,255,255,0.08)' : 'rgba(0,0,0,0.06)';
  ctx.strokeStyle = grid;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(pad, h - pad + 0.5);
  ctx.lineTo(w - pad, h - pad + 0.5);
  ctx.stroke();
  if (n === 0) return;

  // CPU area
  const primary = dark ? '90,162,255' : '47,128,237';
  ctx.beginPath();
  ctx.moveTo(x(0, cpu.length), h - pad);
  for (let i = 0; i < cpu.length; i++) {
    const v = Math.min(1000, Math.max(0, Number(cpu[i]) || 0));
    ctx.lineTo(x(i, cpu.length), h - pad - (v / 1000) * innerH);
  }
  ctx.lineTo(x(cpu.length - 1, cpu.length), h - pad);
  ctx.closePath();
  ctx.fillStyle = `rgba(${primary},0.22)`;
  ctx.fill();
  ctx.beginPath();
  for (let i = 0; i < cpu.length; i++) {
    const v = Math.min(1000, Math.max(0, Number(cpu[i]) || 0));
    const px = x(i, cpu.length), py = h - pad - (v / 1000) * innerH;
    if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
  }
  ctx.strokeStyle = `rgba(${primary},0.9)`;
  ctx.lineWidth = 1.2;
  ctx.stroke();

  // network (right axis): scale to the window maximum, at least 10 KB/s
  let max = 10 * 1000;
  for (let i = 0; i < rx.length; i++) max = Math.max(max, Number(rx[i]) || 0);
  for (let i = 0; i < tx.length; i++) max = Math.max(max, Number(tx[i]) || 0);
  const line = (arr, color, dash) => {
    if (!arr.length) return;
    ctx.beginPath();
    ctx.setLineDash(dash);
    for (let i = 0; i < arr.length; i++) {
      const v = Math.max(0, Number(arr[i]) || 0);
      const px = x(i, arr.length), py = h - pad - (v / max) * innerH * 0.9;
      if (i === 0) ctx.moveTo(px, py); else ctx.lineTo(px, py);
    }
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.4;
    ctx.stroke();
    ctx.setLineDash([]);
  };
  line(rx, dark ? '#5aa2ff' : '#2f80ed', []);
  line(tx, dark ? '#c084fc' : '#a855f7', [4, 3]);
}

/** Keeps the last 60 points per node. */
export function pushPoint(hist, live) {
  if (!hist) hist = { cpu: [], mem: [], rx: [], tx: [] };
  hist.cpu.push(Number(live.cpu) || 0);
  hist.mem.push(Number(live.mem) || 0);
  hist.rx.push(Number(live.rx) || 0);
  hist.tx.push(Number(live.tx) || 0);
  for (const k of ['cpu', 'mem', 'rx', 'tx']) {
    if (hist[k].length > POINTS) hist[k].splice(0, hist[k].length - POINTS);
  }
  return hist;
}
