export const RULE_NAMES = { 1: '离线', 2: 'CPU 高负载', 3: '流量预警', 4: '流量超限', 5: '即将到期', 6: '磁盘告急' }
export const RULE_OPTIONS = Object.entries(RULE_NAMES).map(([value, label]) => ({ value: Number(value), label }))

export const SEVERITY = { 1: '提示', 2: '警告', 3: '严重' }
export const SEVERITY_OPTIONS = Object.entries(SEVERITY).map(([value, label]) => ({ value: Number(value), label }))
export const SEVERITY_TYPE = { 1: 'default', 2: 'warning', 3: 'error' }

export const STATUS = { 0: '未知', 1: '在线', 2: '离线' }
export const STATUS_OPTIONS = [{ value: 1, label: '在线' }, { value: 2, label: '离线' }, { value: 0, label: '未知' }]

export const CURRENCIES = ['USD', 'CNY', 'EUR'].map(v => ({ value: v, label: v }))

export const BILLING_CYCLES = [
  { value: 0, label: '无' },
  { value: 1, label: '月付' },
  { value: 3, label: '季付' },
  { value: 6, label: '半年付' },
  { value: 12, label: '年付' },
  { value: 24, label: '两年付' },
  { value: 36, label: '三年付' },
]

export const COUNT_MODES = [
  { value: 0, label: '上行 + 下行' },
  { value: 1, label: '仅上行' },
  { value: 2, label: '仅下行' },
  { value: 3, label: '取较大值' },
]

export const TIMEZONES = (() => {
  try {
    if (typeof Intl.supportedValuesOf === 'function')
      return Intl.supportedValuesOf('timeZone').map(v => ({ value: v, label: v }))
  }
  catch { /* fall through */ }
  return ['UTC', 'Asia/Shanghai', 'Asia/Hong_Kong', 'Asia/Tokyo', 'Asia/Singapore', 'Europe/London', 'Europe/Berlin', 'America/New_York', 'America/Los_Angeles']
    .map(v => ({ value: v, label: v }))
})()
