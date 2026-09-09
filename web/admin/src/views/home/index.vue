<template>
  <AppPage show-footer>
    <n-grid :cols="6" :x-gap="12" :y-gap="12" responsive="screen" item-responsive>
      <n-gi v-for="card in statCards" :key="card.label" span="6 s:3 m:2 l:1">
        <n-card size="small" hoverable class="cursor-pointer" @click="card.to && router.push(card.to)">
          <div class="text-13 opacity-60">{{ card.label }}</div>
          <div class="mt-4 text-26 font-bold tabular-nums" :style="{ color: card.color }">{{ card.value }}</div>
          <div class="mt-2 h-16 text-12 opacity-50">{{ card.sub }}</div>
        </n-card>
      </n-gi>
    </n-grid>

    <n-grid :cols="3" :x-gap="12" :y-gap="12" class="mt-12" responsive="screen" item-responsive>
      <n-gi span="3 l:2">
        <n-card title="节点状态概览" size="small" segmented>
          <template #header-extra>
            <n-button text type="primary" @click="router.push('/nodes')">节点管理 →</n-button>
          </template>
          <n-data-table :columns="nodeColumns" :data="nodeRows" :bordered="false" size="small" :row-props="rowProps" :max-height="520" :row-key="r => r.id" />
        </n-card>
      </n-gi>
      <n-gi span="3 l:1">
        <n-card title="进行中告警" size="small" segmented>
          <template #header-extra>
            <n-button text type="primary" @click="router.push('/alerts?status=1')">全部 →</n-button>
          </template>
          <n-empty v-if="!activeAlerts.length" description="暂无告警" class="py-24" />
          <div v-for="a in activeAlerts" :key="a.id" class="flex items-start border-b py-8 last:border-b-0" border="light_border dark:dark_border">
            <span class="mt-4 mr-8 h-12 w-3 flex-shrink-0 rounded-2" :style="{ background: a.severity === 3 ? '#d03050' : '#f0a020' }" />
            <div class="min-w-0 flex-1">
              <div class="truncate text-13 font-medium">{{ a.title }}</div>
              <div class="text-12 opacity-50">{{ RULE_NAMES[a.rule] }} · {{ formatRelative(a.startedAt) }}</div>
            </div>
            <n-button size="tiny" quaternary @click="ack(a)">确认</n-button>
          </div>
        </n-card>
        <n-card title="即将到期" size="small" segmented class="mt-12">
          <n-empty v-if="!summary?.expiring?.items?.length" description="7 天内没有到期的节点" class="py-16" />
          <div v-for="e in summary?.expiring?.items || []" :key="e.id" class="flex items-center justify-between py-6 text-13">
            <span class="cursor-pointer hover:text-primary" @click="router.push(`/nodes/${e.id}`)">{{ e.publicName }}</span>
            <span class="opacity-60">{{ e.expiresAt }}</span>
            <n-tag size="small" :type="daysLeftTag(e.daysLeft).type" :bordered="false">{{ daysLeftTag(e.daysLeft).text }}</n-tag>
          </div>
        </n-card>
      </n-gi>
    </n-grid>

    <n-card title="最近告警" size="small" segmented class="mt-12">
      <n-empty v-if="!summary?.recentAlerts?.length" description="暂无告警" class="py-16" />
      <n-timeline v-else>
        <n-timeline-item
          v-for="a in summary.recentAlerts" :key="a.id"
          :type="a.status === 2 ? 'success' : a.severity === 3 ? 'error' : 'warning'"
          :title="a.title" :time="formatDateTime(a.startedAt)"
          :content="a.status === 2 ? `已恢复 · ${formatDateTime(a.resolvedAt)}` : RULE_NAMES[a.rule]"
        />
      </n-timeline>
    </n-card>
  </AppPage>
</template>

<script setup>
import { NButton, NTag } from 'naive-ui'
import { FlagName, MiniBar, NodeStatusTag, TrafficBar } from '@/components/snm'
import { RULE_NAMES } from '@/constants/snm'
import { useLiveStore } from '@/store'
import { daysLeftTag, formatBps, formatDateTime, formatRelative } from '@/utils/format'
import nodesApi from '@/views/nodes/api'
import api from './api'

defineOptions({ name: 'Home' })

const router = useRouter()
const liveStore = useLiveStore()
const summary = ref(null)
const nodes = ref([])

async function load() {
  try {
    const [{ data: s }, { data: n }] = await Promise.all([api.getSummary(), nodesApi.list({ pageSize: 0 })])
    summary.value = s
    nodes.value = n.pageData || []
  }
  catch (e) {
    console.error(e)
  }
}
let timer
onMounted(() => {
  load()
  timer = setInterval(load, 60000)
})
onUnmounted(() => clearInterval(timer))
watch(() => liveStore.nodesChangedTick, load)
watch(() => liveStore.alerts.length, load)

const statCards = computed(() => {
  const s = summary.value
  const online = liveStore.state === 'connected' ? liveStore.onlineCount : (s?.nodes?.online ?? 0)
  const offline = liveStore.state === 'connected' ? liveStore.offlineCount : (s?.nodes?.offline ?? 0)
  const fin = s?.finance
  return [
    { label: '节点总数', value: s?.nodes?.total ?? '—', sub: `${s?.nodes?.disabled ?? 0} 个已禁用`, to: '/nodes' },
    { label: '在线', value: online, color: '#18a058', sub: '实时', to: '/nodes?status=1' },
    { label: '离线', value: offline, color: offline ? '#d03050' : undefined, sub: `${s?.nodes?.unknown ?? 0} 个未知`, to: '/nodes?status=2' },
    { label: '进行中告警', value: s?.alerts?.firing ?? '—', color: s?.alerts?.firing ? '#f0a020' : undefined, sub: `24 小时内 ${s?.alerts?.last24h ?? 0} 条`, to: '/alerts?status=1' },
    { label: `${s?.expiring?.withinDays ?? 7} 天内到期`, value: s?.expiring?.count ?? '—', color: s?.expiring?.count ? '#f0a020' : undefined, sub: `${s?.expiring?.expired ?? 0} 个已过期` },
    { label: '月度支出 (MRR)', value: fin ? `${fin.baseCurrency === 'CNY' ? '¥' : fin.baseCurrency === 'USD' ? '$' : '€'}${fin.mrrBase}` : '—', sub: fin?.byCurrency?.map(c => `${c.currency} ${c.monthly}`).join(' · ') || `${fin?.nodesWithPrice ?? 0} 个节点有价格` },
  ]
})

const activeAlerts = computed(() => (summary.value?.recentAlerts || []).filter(a => a.status === 1))
async function ack(a) {
  await api.ackAlert(a.id)
  $message.success('已确认')
}

const nodeRows = computed(() => {
  const live = liveStore.nodes
  return nodes.value
    .map(n => ({ ...n, l: live[n.id] }))
    .sort((a, b) => ((a.l?.status ?? a.state.status) === 2 ? -1 : 0) - ((b.l?.status ?? b.state.status) === 2 ? -1 : 0) || a.sortOrder - b.sortOrder)
})
const rowProps = row => ({ style: 'cursor:pointer', onClick: () => router.push(`/nodes/${row.id}`) })
const nodeColumns = [
  { title: '节点', key: 'publicName', render: r => h(FlagName, { cc: r.countryCode, name: r.publicName, remark: r.adminRemark }) },
  { title: '状态', key: 'status', width: 90, render: r => h(NodeStatusTag, { status: r.l?.status ?? r.state.status }) },
  { title: 'CPU', key: 'cpu', width: 130, render: r => h(MiniBar, { value: (r.l?.cpu ?? r.live?.cpuPermille ?? 0) / 10, width: 110 }) },
  { title: '内存', key: 'mem', width: 130, render: r => h(MiniBar, { value: r.hardware.memTotalMb ? ((r.l?.memUsedMb ?? r.live?.memUsedMb ?? 0) * 100) / r.hardware.memTotalMb : 0, width: 110 }) },
  { title: '网速', key: 'net', width: 170, render: r => h('span', { class: 'tabular-nums text-12' }, `↓ ${formatBps(r.l?.rx ?? r.live?.rxBps ?? 0)}  ↑ ${formatBps(r.l?.tx ?? r.live?.txBps ?? 0)}`) },
  { title: '流量', key: 'traffic', width: 180, render: r => h(TrafficBar, { used: r.l?.tUsed ?? r.traffic.billedBytes, limit: r.traffic.limitBytes, width: 160 }) },
  { title: '到期', key: 'expires', width: 110, render: r => { const t = daysLeftTag(r.finance.daysLeft); return h(NTag, { size: 'small', type: t.type, bordered: false }, () => t.text) } },
]
</script>
