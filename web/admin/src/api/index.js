import { request } from '@/utils'

export default {
  getUser: () => request.get('/user/detail'),
  refreshToken: data => request.post('/auth/refresh', data, { needToken: false, needTip: false }),
  logout: data => request.post('/auth/logout', data ?? {}, { needTip: false }),
  getRolePermissions: () => request.get('/role/permissions/tree'),
  validateMenuPath: path => request.get('/permission/menu/validate', { params: { path } }),
}
