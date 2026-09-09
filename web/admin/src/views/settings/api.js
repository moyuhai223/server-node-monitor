import { request } from '@/utils'

export default {
  get: () => request.get('/settings'),
  update: data => request.patch('/settings', data),
  channels: () => request.get('/settings/channels'),
  createChannel: data => request.post('/settings/channels', data),
  updateChannel: (id, data) => request.patch(`/settings/channels/${id}`, data),
  removeChannel: id => request.delete(`/settings/channels/${id}`),
  testChannel: id => request.post(`/settings/channels/${id}/test`),
  testChannelDraft: data => request.post('/settings/channels/test', data),
  geoipRefresh: () => request.post('/settings/geoip/refresh', {}, { timeout: 90000 }),
  geoipStatus: () => request.get('/settings/geoip/status'),
  systemInfo: () => request.get('/system/info'),
  changePassword: data => request.post('/auth/password', data),
  logoutAll: () => request.post('/auth/logout', { all: true }),
}
