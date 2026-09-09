import axios from 'axios'
import { useAuthStore } from '@/store'

let inflight = null

/** Rotates the refresh token once for all concurrent 401s; bypasses the interceptors to avoid recursion. */
export function refreshAccessToken() {
  if (inflight)
    return inflight
  const authStore = useAuthStore()
  inflight = axios
    .post(`${import.meta.env.VITE_AXIOS_BASE_URL}/auth/refresh`, { refreshToken: authStore.refreshToken }, { timeout: 12000 })
    .then(({ data }) => {
      if (data?.code !== 0 || !data.data?.accessToken)
        throw new Error(data?.message || 'refresh failed')
      authStore.setToken(data.data)
      return data.data.accessToken
    })
    .catch((err) => {
      authStore.resetToken()
      throw err
    })
    .finally(() => {
      inflight = null
    })
  return inflight
}
