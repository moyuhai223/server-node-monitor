import { defineStore } from 'pinia'
import api from '@/api'
import { useLiveStore, usePermissionStore, useRouterStore, useTabStore, useUserStore } from '@/store'

export const useAuthStore = defineStore('auth', {
  state: () => ({
    accessToken: undefined,
    refreshToken: undefined,
  }),
  actions: {
    setToken({ accessToken, refreshToken }) {
      this.accessToken = accessToken
      if (refreshToken)
        this.refreshToken = refreshToken
    },
    resetToken() {
      this.$reset()
    },
    toLogin() {
      const { router, route } = useRouterStore()
      router.replace({ path: '/login', query: route.query })
    },
    resetLoginState() {
      const { resetUser } = useUserStore()
      const { resetRouter } = useRouterStore()
      const { resetPermission, accessRoutes } = usePermissionStore()
      const { resetTabs } = useTabStore()
      useLiveStore().disconnect()
      resetRouter(accessRoutes)
      resetUser()
      resetPermission()
      resetTabs()
      this.resetToken()
    },
    async logout() {
      const refreshToken = this.refreshToken
      try {
        if (this.accessToken)
          await api.logout({ refreshToken })
      }
      catch {
        // best effort
      }
      this.resetLoginState()
      this.toLogin()
    },
  },
  persist: {
    key: 'snm_auth',
  },
})
