import { defineStore } from 'pinia'
import { request } from '@/utils'

/** Cached /api/system/info and /api/settings (site title, thresholds shown as placeholders in forms). */
export const useSystemStore = defineStore('system', {
  state: () => ({
    info: null,
    settings: null,
    loadedAt: 0,
  }),
  actions: {
    async loadInfo(force = false) {
      if (this.info && !force)
        return this.info
      try {
        const { data } = await request.get('/system/info', { needTip: false })
        this.info = data
      }
      catch { /* ignore */ }
      return this.info
    },
    async loadSettings(force = false) {
      if (this.settings && !force && Date.now() - this.loadedAt < 60000)
        return this.settings
      const { data } = await request.get('/settings')
      this.settings = data
      this.loadedAt = Date.now()
      return data
    },
    setSettings(data) {
      this.settings = data
      this.loadedAt = Date.now()
    },
  },
})
