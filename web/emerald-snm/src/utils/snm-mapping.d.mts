import type { MonitorNode, NodeMetricsResponse } from './api'
export function toMonitorNode(node: any, site?: any, now?: number): MonitorNode
export function toMetricsResponse(node: any, now?: number): NodeMetricsResponse
export function probeSummary(probe: any, now?: number): { samples: number; last: any; stale: boolean; latest: number | null; failure: number | null; average: number | null }
