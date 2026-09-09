import dayjs from 'dayjs'

export function num(v) {
  return typeof v === 'bigint' ? Number(v) : (typeof v === 'number' ? v : Number(v) || 0)
}

/** Traffic bytes, decimal units (1 TB = 1000 GB, as VPS vendors bill). */
export function formatBytes(bytes, digits = 1) {
  let b = num(bytes)
  const units = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
  let i = 0
  while (b >= 1000 && i < units.length - 1) {
    b /= 1000
    i++
  }
  return `${i === 0 ? b.toFixed(0) : b.toFixed(digits)} ${units[i]}`
}

/** Memory / disk sizes given in MiB. */
export function formatMb(mb) {
  const m = num(mb)
  if (m >= 1024 * 1024)
    return `${(m / 1024 / 1024).toFixed(1)} TB`
  if (m >= 1024)
    return `${(m / 1024).toFixed(m >= 10240 ? 0 : 1)} GB`
  return `${m} MB`
}

export function formatBps(bytesPerSec) {
  let b = num(bytesPerSec)
  const units = ['B/s', 'KB/s', 'MB/s', 'GB/s']
  let i = 0
  while (b >= 1000 && i < units.length - 1) {
    b /= 1000
    i++
  }
  return `${i === 0 ? b.toFixed(0) : b.toFixed(1)} ${units[i]}`
}

export function formatPermille(p) {
  return `${(num(p) / 10).toFixed(1)}%`
}

export function formatDuration(seconds) {
  const s = Math.max(0, Math.floor(num(seconds)))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0)
    return `${d}天 ${h}小时`
  if (h > 0)
    return `${h}小时 ${m}分`
  if (m > 0)
    return `${m}分钟`
  return `${s} 秒`
}

export function formatRelative(ts, now = Date.now()) {
  const t = typeof ts === 'string' ? new Date(ts).getTime() : num(ts)
  if (!t)
    return '—'
  const sec = Math.max(0, Math.round((now - t) / 1000))
  if (sec < 60)
    return `${sec} 秒前`
  if (sec < 3600)
    return `${Math.floor(sec / 60)} 分钟前`
  if (sec < 86400)
    return `${Math.floor(sec / 3600)} 小时前`
  return `${Math.floor(sec / 86400)} 天前`
}

export function formatDateTime(iso) {
  if (!iso)
    return '—'
  return dayjs(iso).format('YYYY-MM-DD HH:mm:ss')
}

export function formatDate(iso) {
  if (!iso)
    return '—'
  return dayjs(iso).format('YYYY-MM-DD')
}

export function percentColor(pct) {
  const p = num(pct)
  if (p < 60)
    return '#18a058'
  if (p < 85)
    return '#f0a020'
  return '#d03050'
}

export function flagEmoji(cc) {
  if (!cc || !/^[A-Za-z]{2}$/.test(cc))
    return '🏳️'
  const up = cc.toUpperCase()
  return String.fromCodePoint(0x1F1E6 + up.charCodeAt(0) - 65, 0x1F1E6 + up.charCodeAt(1) - 65)
}

const REGION_NAMES = (() => {
  try {
    return new Intl.DisplayNames(['zh-CN'], { type: 'region' })
  }
  catch {
    return null
  }
})()

export function countryName(cc) {
  if (!cc)
    return '未知'
  try {
    return REGION_NAMES?.of(cc.toUpperCase()) || cc
  }
  catch {
    return cc
  }
}

export function daysLeftTag(daysLeft) {
  if (daysLeft === null || daysLeft === undefined)
    return { text: '—', type: 'default' }
  if (daysLeft < 0)
    return { text: `已过期 ${-daysLeft} 天`, type: 'error' }
  if (daysLeft <= 1)
    return { text: `剩余 ${daysLeft} 天`, type: 'error' }
  if (daysLeft <= 7)
    return { text: `剩余 ${daysLeft} 天`, type: 'warning' }
  return { text: `剩余 ${daysLeft} 天`, type: 'success' }
}
