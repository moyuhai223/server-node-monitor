// Public data comes exclusively from SNM's theme SDK, not monitor-probe's REST API.
import { createClient } from '/vendor/snm-client.js'
import fields from '../../theme-options.json'
import { toMetricsResponse, toMonitorNode } from './snm-mapping.mjs'
export interface MeResponse {
  site_name?: string
  sitename?: string
  authed: boolean
  public?: boolean
  public_page?: boolean
  history_days?: number
  site?: string
}

export interface PublicSettings {
  sitename: string
  authed: boolean
  public: boolean
  history_days?: number
  theme_settings?: Record<string, unknown>
}

export interface Metrics {
  uptime: number
  cpu: number
  load: [number, number, number]
  mem_total: number
  mem_used: number
  mem_percent?: number
  disk_percent?: number
  swap_total?: number
  swap_used?: number
  disk_total: number
  disk_used: number
  net_rx: number
  net_tx: number
  total_rx: number
  total_tx: number
  month_rx: number
  month_tx: number
  tcp: number
  udp: number
  procs: number
}

export interface MonitorNode {
  id: number
  name: string
  sort: number
  public: boolean
  online: boolean
  country: string
  group?: string
  last_seen: number
  last_seen_ago?: number | null
  metrics: Metrics | null
  os: string
  kernel: string
  arch: string
  virt: string
  cpu_name: string
  cpu_cores: number
  mem_total: number
  swap_total?: number
  disk_total: number
  agent_version: string
  price: number
  currency: string
  billing_cycle: string
  expires_at: string | null
  expires_in?: number | null
  traffic_limit: number
  traffic_mode: string
  traffic_reset_day: number
  total_rx: number
  total_tx: number
  month_rx: number
  month_tx: number
  month_used?: number
  month_start: string
  day_rx: number
  day_tx: number
  hostname?: string
  ip?: string
  ipv4?: string
  ipv6?: string
  addresses?: { address: string, source?: string }[]
  remark?: string
  public_remark?: string
}

export interface NodesResponse {
  nodes: MonitorNode[]
}

export interface MetricsPoint {
  ts: number
  cpu: number
  cpu_max?: number
  mem_used: number
  swap_used?: number
  disk_used: number
  net_rx: number
  net_tx: number
  net_rx_max?: number
  net_tx_max?: number
  tcp?: number
  udp?: number
  procs?: number
  minutes?: number
}

export interface PingPoint {
  task_id: number
  ts: number
  latency: number | null
  band?: [number, number]
  loss?: number
}

export interface NodeMetricsResponse {
  step?: number
  metrics?: MetricsPoint[]
  ping?: PingPoint[]
  probeMeta?: Record<string, { interval: number; stale: boolean; latest: number | null; lastState?: number }>
  probes?: Record<string, string>
  loss?: Record<string, number>
}


export const client = createClient({ theme: document.documentElement.dataset.snmTheme || 'emerald', historyPoints: 60 })
const listeners = new Set<() => void>()
export function subscribeUpdates(fn: () => void): () => void { listeners.add(fn); return () => { listeners.delete(fn) } }
for (const name of ['snapshot', 'nodes', 'update', 'probes']) client.on(name, () => { for (const fn of listeners) fn() })
export function getMe(): Promise<MeResponse> { return Promise.resolve({ sitename: client.state.site.title, authed: false, public: true, history_days: 0 }) }
export function getNodes(): Promise<NodesResponse> { return Promise.resolve({nodes: client.state.nodes.map((n: any) => toMonitorNode(n, client.state.site))}) }
export function getNodeMetrics(id: number | string, _params?: {hours?: number, points?: number, series?: 'metrics' | 'ping'}): Promise<NodeMetricsResponse> {
  const node = client.state.nodes.find((n: any) => String(n.id) === String(id))
  return Promise.resolve(toMetricsResponse(node))
}
export function loadThemeConfig(): Promise<Record<string, unknown>> {
  const saved = client.state.site.options || {}
  const defaults = Object.fromEntries(fields.filter(f => 'key' in f).map(f => [f.key, f.default]))
  return Promise.resolve({...defaults, ...saved, visitorInfoCardEnabled: false, snmShowSpecs: client.state.site.showSpecs !== false, snmShowTraffic: client.state.site.showTraffic !== false})
}
