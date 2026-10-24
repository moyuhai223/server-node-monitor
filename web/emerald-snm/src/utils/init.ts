import { useAppStore } from '@/stores/app'
import { useNodesStore } from '@/stores/nodes'
import { client, loadThemeConfig } from '@/utils/api'
import { toMonitorNode } from './snm-mapping.mjs'
let initialized = false
export async function initApp(): Promise<void> {
  if (initialized) return
  initialized = true
  const app = useAppStore(), nodes = useNodesStore()
  const applySite = async () => {
    app.publicSettings = { sitename: client.state.site.title || 'Server Node Monitor', authed: false, public: true, history_days: 0, theme_settings: await loadThemeConfig() }
    document.title = app.publicSettings.sitename + ' · Emerald'
    app.updateLoginState(false)
  }
  const applyNodes = () => { nodes.setNodesFromMonitor(client.state.nodes.map((n: any) => toMonitorNode(n, client.state.site))); app.loading = false }
  client.on('site', () => { void applySite(); applyNodes() })
  for (const event of ['snapshot','nodes','update','probes']) client.on(event, applyNodes)
  client.on('connection', ({status}: {status:string}) => {
    const state = ['connecting','connected','reconnecting','disconnected'].includes(status) ? status : 'disconnected'
    nodes.updateWsState(state as 'connecting'|'connected'|'reconnecting'|'disconnected')
    app.connectionError = state === 'reconnecting' || state === 'disconnected'
    app.loading = false
  })
  await applySite()
  app.loading = false
  void client.start()
}
export function destroyInitManager(): void { void client.stop() }
