import { defineStore } from 'pinia'
import { createAdminHub } from '@/realtime/adminHub'
import { useAuthStore } from '@/store'
import { RULE_NAMES } from '@/constants/snm'

const HISTORY_POINTS = 60

function num(v) {
  return typeof v === 'bigint' ? Number(v) : (typeof v === 'number' ? v : 0)
}

/** Normalises a live DTO (BigInt -> Number) so templates and charts can use plain arithmetic. */
function normalizeLive(l) {
  return {
    id: num(l.id),
    status: num(l.status),
    connected: !!l.connected,
    cpu: num(l.cpu),
    memUsedMb: num(l.memUsedMb),
    swapUsedMb: num(l.swapUsedMb),
    diskUsedMb: (l.diskUsedMb || []).map(num),
    rx: num(l.rx),
    tx: num(l.tx),
    load1: num(l.load1),
    up: num(l.up),
    lastSeen: num(l.lastSeen),
    remoteIp: l.remoteIp || '',
    tUsed: num(l.tUsed),
    tRx: num(l.tRx),
    tTx: num(l.tTx),
    seq: num(l.seq),
  }
}

export const useLiveStore = defineStore('live', {
  state: () => ({
    state: 'idle',
    nodes: {},          // id -> live dto (plain object keyed by id; shallow updates)
    history: {},        // id -> { ts:[], cpu:[], mem:[], rx:[], tx:[] } (mem = MB)
    lastServerTs: 0,
    alerts: [],
    nodesChangedTick: 0,
    _connection: null,
  }),
  getters: {
    onlineCount: state => Object.values(state.nodes).filter(n => n.status === 1).length,
    offlineCount: state => Object.values(state.nodes).filter(n => n.status === 2).length,
  },
  actions: {
    connect() {
      if (this._connection)
        return
      const authStore = useAuthStore()
      if (!authStore.accessToken)
        return
      this.state = 'connecting'
      const conn = createAdminHub({
        getToken: () => useAuthStore().accessToken || '',
        onSnapshot: s => this.applySnapshot(s),
        onBatch: b => this.applyBatch(b),
        onAlert: a => this.onAlert(a),
        onNodesChanged: () => { this.nodesChangedTick++ },
        onState: s => { this.state = s },
      })
      this._connection = conn
      conn.start()
        .then(() => { this.state = 'connected' })
        .catch((e) => {
          console.warn('admin hub start failed', e)
          this.state = 'disconnected'
          this._connection = null
        })
    },
    async disconnect() {
      const conn = this._connection
      this._connection = null
      this.state = 'idle'
      if (conn) {
        try { await conn.stop() }
        catch { /* ignore */ }
      }
    },
    async reconnect() {
      await this.disconnect()
      this.connect()
    },
    applySnapshot(snapshot) {
      const nodes = {}
      for (const l of snapshot.nodes || []) {
        const live = normalizeLive(l)
        nodes[live.id] = live
      }
      this.nodes = nodes
      this.lastServerTs = num(snapshot.ts)
    },
    applyBatch(batch) {
      const ts = num(batch.ts)
      const nodes = { ...this.nodes }
      for (const l of batch.items || []) {
        const live = normalizeLive(l)
        nodes[live.id] = live
        if (live.status === 1)
          this.pushHistory(live.id, { ts: live.lastSeen || ts, cpu: live.cpu, mem: live.memUsedMb, rx: live.rx, tx: live.tx })
      }
      this.nodes = nodes
      this.lastServerTs = ts
    },
    pushHistory(id, point) {
      const h = this.history[id] || { ts: [], cpu: [], mem: [], rx: [], tx: [] }
      for (const k of ['ts', 'cpu', 'mem', 'rx', 'tx']) {
        h[k].push(point[k])
        if (h[k].length > HISTORY_POINTS)
          h[k].splice(0, h[k].length - HISTORY_POINTS)
      }
      this.history = { ...this.history, [id]: h }
    },
    /** Loads the server-side ring buffer for a node (detail page). */
    async ensureHistory(id) {
      if (!this._connection || this.state !== 'connected')
        return
      try {
        const h = await this._connection.invoke('GetHistory', Number(id))
        if (h) {
          this.history = {
            ...this.history,
            [id]: { ts: (h.ts || []).map(num), cpu: (h.cpu || []).map(num), mem: (h.mem || []).map(num), rx: (h.rx || []).map(num), tx: (h.tx || []).map(num) },
          }
        }
      }
      catch (e) {
        console.warn('GetHistory failed', e)
      }
    },
    onAlert(a) {
      const alert = { ...a, id: num(a.id), nodeId: num(a.nodeId), rule: num(a.rule), status: num(a.status), severity: num(a.severity), ts: num(a.ts) }
      this.alerts = [alert, ...this.alerts].slice(0, 50)
      const resolved = alert.status === 2
      const fn = resolved ? 'success' : (alert.severity === 3 ? 'error' : 'warning')
      window.$notification?.[fn]({
        title: alert.title,
        content: alert.message,
        meta: RULE_NAMES[alert.rule] || '',
        duration: 8000,
      })
    },
  },
})
