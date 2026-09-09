import { request } from '@/utils'

export default {
  getSummary: () => request.get('/dashboard/summary'),
  ackAlert: id => request.post(`/alerts/${id}/ack`),
}
