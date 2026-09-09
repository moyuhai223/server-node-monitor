import { request } from '@/utils'

export default {
  list: params => request.get('/nodes', { params }),
  get: id => request.get(`/nodes/${id}`),
  create: data => request.post('/nodes', data),
  update: (id, data) => request.patch(`/nodes/${id}`, data),
  remove: id => request.delete(`/nodes/${id}`),
  rotateKey: id => request.post(`/nodes/${id}/rotate-key`),
  revealKey: id => request.post(`/nodes/${id}/reveal-key`),
  reorder: ids => request.post('/nodes/reorder', { ids }),
  installScript: (id, params) => request.get(`/nodes/${id}/install-script`, { params }),
  metrics: (id, range) => request.get(`/nodes/${id}/metrics`, { params: { range } }),
  traffic: (id, periods = 12) => request.get(`/nodes/${id}/traffic`, { params: { periods } }),
  resetTraffic: id => request.post(`/nodes/${id}/traffic/reset`, { scope: 'period' }),
  alerts: (id, params) => request.get(`/nodes/${id}/alerts`, { params }),
}
