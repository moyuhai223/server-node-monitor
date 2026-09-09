// Formatting helpers shared by the public dashboard (docs/FRONTEND.md 7).

/** MessagePack may hand us BigInt for 64-bit counters; normalise to Number. */
export function num(v) {
  if (typeof v === 'bigint') return Number(v);
  return typeof v === 'number' ? v : 0;
}

/** Traffic bytes: decimal units like ISPs bill (1 TB = 1000 GB). */
export function formatBytes(bytes) {
  let b = num(bytes);
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB'];
  let i = 0;
  while (b >= 1000 && i < units.length - 1) { b /= 1000; i++; }
  return (i === 0 ? b.toFixed(0) : b < 10 ? b.toFixed(2) : b < 100 ? b.toFixed(1) : b.toFixed(0)) + ' ' + units[i];
}

/** Memory / disk sizes given in MiB. */
export function formatMb(mb) {
  const m = num(mb);
  if (m >= 1024 * 1024) return (m / 1024 / 1024).toFixed(1) + ' TB';
  if (m >= 1024) return (m / 1024).toFixed(m >= 10240 ? 0 : 1) + ' GB';
  return m + ' MB';
}

export function formatBps(bytesPerSec) {
  let b = num(bytesPerSec);
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s'];
  let i = 0;
  while (b >= 1000 && i < units.length - 1) { b /= 1000; i++; }
  return (i === 0 ? b.toFixed(0) : b < 10 ? b.toFixed(2) : b < 100 ? b.toFixed(1) : b.toFixed(0)) + ' ' + units[i];
}

export function formatDuration(seconds) {
  const s = Math.max(0, Math.floor(num(seconds)));
  const d = Math.floor(s / 86400);
  const h = Math.floor((s % 86400) / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (d > 0) return `${d} 天 ${h} 小时`;
  if (h > 0) return `${h} 小时 ${m} 分`;
  if (m > 0) return `${m} 分 ${s % 60} 秒`;
  return `${s} 秒`;
}

export function timeAgo(ms, now = Date.now()) {
  const sec = Math.max(0, Math.round((now - num(ms)) / 1000));
  if (sec < 60) return `${sec} 秒前`;
  if (sec < 3600) return `${Math.floor(sec / 60)} 分钟前`;
  if (sec < 86400) return `${Math.floor(sec / 3600)} 小时前`;
  return `${Math.floor(sec / 86400)} 天前`;
}

export function percent(permille) {
  return (num(permille) / 10).toFixed(1) + '%';
}

/** ISO 3166-1 alpha-2 -> regional indicator emoji; unknown -> globe. */
export function flagEmoji(cc) {
  if (!cc || cc.length !== 2 || !/^[A-Za-z]{2}$/.test(cc)) return '🌐';
  const base = 0x1F1E6;
  const up = cc.toUpperCase();
  return String.fromCodePoint(base + up.charCodeAt(0) - 65, base + up.charCodeAt(1) - 65);
}

export function clock(date = new Date()) {
  const p = (n) => String(n).padStart(2, '0');
  return `${p(date.getHours())}:${p(date.getMinutes())}:${p(date.getSeconds())}`;
}
