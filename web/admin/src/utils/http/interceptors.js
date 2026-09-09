import { useAuthStore, useLiveStore } from '@/store'
import { resolveResError } from './helpers'
import { refreshAccessToken } from './refresh'

const SUCCESS_CODES = [0, 200]

export function setupInterceptors(axiosInstance) {
  function resResolve(response) {
    const { data, status, config, statusText, headers } = response
    if (headers['content-type']?.includes('json')) {
      if (SUCCESS_CODES.includes(data?.code))
        return Promise.resolve(data)
      const code = data?.code ?? status
      const needTip = config?.needTip !== false
      const message = resolveResError(code, data?.message ?? statusText, needTip)
      return Promise.reject({ code, message, error: data ?? response })
    }
    return Promise.resolve(data ?? response)
  }

  async function resReject(error) {
    if (!error || !error.response) {
      const code = error?.code
      const message = resolveResError(code, error?.message)
      return Promise.reject({ code, message, error })
    }
    const { data, status, config } = error.response
    const code = data?.code ?? status

    // expired access token: refresh once (single flight) and replay the request
    const isAuthCall = /\/auth\/(login|refresh)$/.test(config?.url || '')
    if (status === 401 && code === 401 && !isAuthCall && !config._retried && useAuthStore().refreshToken) {
      try {
        const token = await refreshAccessToken()
        config._retried = true
        config.headers.Authorization = `Bearer ${token}`
        useLiveStore().reconnect()
        return axiosInstance(config)
      }
      catch {
        // fall through to the dialog below
      }
    }

    const needTip = config?.needTip !== false
    const message = resolveResError(code, data?.message ?? error.message, needTip)
    return Promise.reject({ code, message, error: error.response?.data || error.response })
  }

  axiosInstance.interceptors.request.use(reqResolve, reqReject)
  axiosInstance.interceptors.response.use(resResolve, resReject)
}

function reqResolve(config) {
  if (config.needToken === false)
    return config
  const { accessToken } = useAuthStore()
  if (accessToken)
    config.headers.Authorization = `Bearer ${accessToken}`
  return config
}

function reqReject(error) {
  return Promise.reject(error)
}
