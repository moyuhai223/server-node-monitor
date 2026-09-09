import { request } from '@/utils'

export default {
  list: params => request.get('/alerts', { params }),
  active: () => request.get('/alerts/active'),
  ack: id => request.post(`/alerts/${id}/ack`),
  purge: params => request.delete('/alerts', { params }),
}
