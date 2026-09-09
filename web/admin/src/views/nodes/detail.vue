<template>
  <CommonPage back :title="node ? `节点详情 · ${node.publicName}` : '节点详情'">
    <template #action>
      <n-space v-if="node">
        <n-button @click="formModal.open(node)">编辑</n-button>
        <n-button type="primary" @click="installModal.open(node.id)">安装脚本</n-button>
      </n-space>
    </template>

    <n-spin :show="!node">
      <template v-if="node">
        <n-card size="small">
          <div class="flex items-center">
            <FlagName :cc="node.countryCode" :name="node.publicName" :remark="node.adminRemark" class="text-18" />
            <NodeStatusTag :status="live?.status ?? node.state.status" class="ml-12" />
            <n-tag v-if="!node.enabled" size="small" class="ml-8" :bordered="false">已禁用</n-tag>
          </div>
          <n-descriptions :column="3" label-placement="left" class="mt-12" size="small">
            <n-descriptions-item label="主机名">{{ node.hardware.hostname || '—' }}</n-descriptions-item>
            <n-descriptions-item label="系统">{{ node.hardware.os || '—' }}</n-descriptions-item>
            <n-descriptions-item label="内核">{{ node.hardware.kernel || '—' }}</n-descriptions-item>
            <n-descriptions-item label="架构 / 虚拟化">{{ node.hardware.arch || '—' }} / {{ node.hardware.virt || '物理机或未知' }}</n-descriptions-item>
            <n-descriptions-item label="CPU">{{ node.hardware.cpuModel || '—' }}<span v-if="node.hardware.cpuCores"> · {{ node.hardware.cpuCores }} 核</span></n-descriptions-item>
            <n-descriptions-item label="内存 / Swap">{{ formatMb(node.hardware.memTotalMb) }} / {{ formatMb(node.hardware.swapTotalMb) }}</n-descriptions-item>
            <n-descriptions-item label="探针版本">{{ node.hardware.agentVersion || '—' }} (协议 {{ node.hardware.protocolVersion }})</n-descriptions-item>
            <n-descriptions-item label="运行时长">{{ live?.up ? formatDuration(live.up) : '—' }}</n-descriptions-item>
            <n-descriptions-item label="开机时间">{{ formatDateTime(node.hardware.bootTimeUtc) }}</n-descriptions-item>
            <n-descriptions-item label="心跳间隔">{{ node.intervalMs }} ms</n-descriptions-item>
            <n-descriptions-item label="时区">{{ node.traffic.timeZoneId }}</n-descriptions-item>
            <n-descriptions-item label="服务端捕获 IP">{{ node.remoteIp || '—' }}</n-descriptions-item>
            <n-descriptions-item label="最后注册">{{ formatDateTime(node.state.lastRegisterAt) }}</n-descriptions-item>
            <n-descriptions-item label="最后上报">{{ formatDateTime(node.state.lastSeenAt) }}</n-descriptions-item>
            <n-descriptions-item label="国家">{{ flagEmoji(node.countryCode) }} {{ countryName(node.countryCode) }} <span class="opacity-50">(自动 {{ node.countryCodeAuto || '—' }} · 覆盖 {{ node.countryCodeOverride || '—' }})</span></n-descriptions-item>
          </n-descriptions>
        </n-card>

        <n-grid :cols="3" :x-gap="12" :y-gap="12" class="mt-12" responsive="screen" item-responsive>
          <n-gi span="3 l:2">
            <n-card title="实时指标" size="small" segmented>
              <div class="flex flex-wrap items-center gap-24">
                <n-progress v-for="p in gauges" :key="p.label" type="circle" :percentage="p.pct" :color="percentColor(p.pct)" :stroke-width="8" style="width: 96px">
                  <div class="text-center text-12"><div class="font-bold">{{ p.pct.toFixed(1) }}%</div><div class="opacity-60">{{ p.label }}</div></div>
                </n-progress>
                <div class="flex-col text-13 leading-24 tabular-nums">
                  <span>↓ {{ formatBps(live?.rx ?? 0) }} &nbsp; ↑ {{ formatBps(live?.tx ?? 0) }}</span>
                  <span>负载 {{ ((live?.load1 ?? 0) / 100).toFixed(2) }}</span>
                  <span>Swap {{ formatMb(live?.swapUsedMb ?? 0) }} / {{ formatMb(node.hardware.swapTotalMb) }}</span>
                  <span>本账期已用 {{ formatBytes(live?.tUsed ?? node.traffic.billedBytes) }}<template v-if="node.traffic.limitBytes"> / {{ formatBytes(node.traffic.limitBytes, 0) }}</template></span>
                </div>
              </div>
              <NodeSparkline :hist="liveStore.history[node.id]" :height="90" class="mt-12" />
            </n-card>
          </n-gi>
          <n-gi span="3 l:1">
            <n-card title="IP 地址" size="small" segmented>
              <n-empty v-if="!node.ips.length" description="尚未上报" />
              <div v-for="ip in node.ips" :key="ip.address" class="flex items-center py-4 text-13">
                <n-tag size="tiny" :type="ip.isPublic ? 'info' : 'default'" class="mr-8 w-40 justify-center">{{ ip.isPublic ? '公网' : '内网' }}</n-tag>
                <span class="tabular-nums">{{ ip.address }}</span>
                <span class="ml-auto text-11 opacity-50">{{ { 1: '探针', 2: '服务端', 3: '探针+服务端' }[ip.source] }}</span>
              </div>
            </n-card>
          </n-gi>
        </n-grid>

        <n-card title="历史图表" size="small" segmented class="mt-12">
          <template #header-extra>
            <n-radio-group v-model:value="range" size="small" @update:value="loadMetrics">
              <n-radio-button value="24h" label="24 小时" />
              <n-radio-button value="7d" label="7 天" />
              <n-radio-button value="30d" label="30 天" />
            </n-radio-group>
          </template>
          <n-tabs v-model:value="metric" type="line" size="small">
            <n-tab-pane v-for="t in metricTabs" :key="t.key" :name="t.key" :tab="t.label" />
          </n-tabs>
          <n-spin :show="metricsLoading">
            <MetricChart :metric="metric" :points="metrics" :range="range" :mem-total-mb="node.hardware.memTotalMb" :cpu-cores="node.hardware.cpuCores" />
          </n-spin>
        </n-card>

        <n-card title="流量账期" size="small" segmented class="mt-12">
          <template #header-extra>
            <n-button size="small" @click="resetTraffic">清零本期流量</n-button>
          </template>
          <template v-if="traffic">
            <div class="flex flex-wrap items-center gap-24">
              <n-statistic label="账期" :value="`${traffic.current.periodStart} ~ ${traffic.current.periodEnd}`" />
              <n-statistic label="下行" :value="formatBytes(traffic.current.rxBytes)" />
              <n-statistic label="上行" :value="formatBytes(traffic.current.txBytes)" />
              <n-statistic label="计费" :value="formatBytes(traffic.current.billedBytes)" />
              <n-statistic label="限额" :value="traffic.current.limitBytes ? formatBytes(traffic.current.limitBytes, 0) : '不限'" />
            </div>
            <n-progress v-if="traffic.current.limitBytes" class="mt-12" type="line" :percentage="Math.min(100, traffic.current.pct || 0)" :color="percentColor(traffic.current.pct || 0)" indicator-placement="inside" />
            <div class="mt-16 h-220">
              <VChart :option="dailyOption" :theme="appStore.isDark ? 'dark' : undefined" autoresize />
            </div>
            <n-data-table v-if="traffic.history.length" class="mt-12" size="small" :columns="historyColumns" :data="traffic.history" :bordered="false" />
          </template>
        </n-card>

        <n-card title="告警记录" size="small" segmented class="mt-12">
          <n-data-table size="small" :columns="alertColumns" :data="alerts" :bordered="false" />
        </n-card>
      </template>
    </n-spin>

    <NodeFormModal ref="formModal" @saved="load" />
    <InstallScriptModal ref="installModal" />
  </CommonPage>
</template>

<script setup>
import { NTag } from 'naive-ui'
import { FlagName, InstallScriptModal, MetricChart, NodeFormModal, NodeSparkline, NodeStatusTag } from '@/components/snm'
import { RULE_NAMES } from '@/constants/snm'
import { useAppStore, useLiveStore } from '@/store'
import { VChart } from '@/utils/echarts'
import { countryName, flagEmoji, formatBps, formatBytes, formatDateTime, formatDuration, formatMb, percentColor } from '@/utils/format'
import api from './api'

const route = useRoute()
const appStore = useAppStore()
const liveStore = useLiveStore()
const id = computed(() => Number(route.params.id))
const node = ref(null)
const traffic = ref(null)
const alerts = ref([])
const metrics = ref([])
const metricsLoading = ref(false)
const range = ref('24h')
const metric = ref('cpu')
const formModal = ref(null)
const installModal = ref(null)

const live = computed(() => liveStore.nodes[id.value])

async function load() {
  const [{ data: n }, { data: t }, { data: a }] = await Promise.all([api.get(id.value), api.traffic(id.value), api.alerts(id.value, { pageSize: 20 })])
  node.value = n
  traffic.value = t
  alerts.value = a.pageData
}
async function loadMetrics() {
  metricsLoading.value = true
  try {
    const { data } = await api.metrics(id.value, range.value)
    metrics.value = data.points
  }
  finally {
    metricsLoading.value = false
  }
}
onMounted(async () => {
  await load()
  liveStore.ensureHistory(id.value)
  loadMetrics()
})
watch(() => liveStore.nodesChangedTick, load)
watch(() => liveStore.state, s => s === 'connected' && liveStore.ensureHistory(id.value))

const gauges = computed(() => {
  const n = node.value
  const l = live.value
  const diskTotal = n?.hardware.disks.reduce((a, d) => a + d.totalMb, 0) || 0
  const diskUsed = (l?.diskUsedMb || []).reduce((a, b) => a + b, 0)
  return [
    { label: 'CPU', pct: (l?.cpu ?? 0) / 10 },
    { label: '内存', pct: n?.hardware.memTotalMb ? ((l?.memUsedMb ?? 0) * 100) / n.hardware.memTotalMb : 0 },
    { label: 'Swap', pct: n?.hardware.swapTotalMb ? ((l?.swapUsedMb ?? 0) * 100) / n.hardware.swapTotalMb : 0 },
    { label: '磁盘', pct: diskTotal ? (diskUsed * 100) / diskTotal : 0 },
  ]
})

const metricTabs = [
  { key: 'cpu', label: 'CPU' }, { key: 'mem', label: '内存' }, { key: 'net', label: '网络速率' }, { key: 'traffic', label: '流量' }, { key: 'load', label: '负载' }, { key: 'disk', label: '磁盘' },
]

const dailyOption = computed(() => {
  const d = traffic.value?.current.daily || []
  return {
    backgroundColor: 'transparent',
    tooltip: { trigger: 'axis', valueFormatter: v => formatBytes(v) },
    legend: { top: 0 },
    grid: { left: 60, right: 16, top: 30, bottom: 28 },
    xAxis: { type: 'category', data: d.map(x => x.date.slice(5)) },
    yAxis: { type: 'value', axisLabel: { formatter: v => formatBytes(v, 0) } },
    series: [
      { name: '下行', type: 'bar', stack: 't', data: d.map(x => x.rxBytes) },
      { name: '上行', type: 'bar', stack: 't', data: d.map(x => x.txBytes) },
    ],
  }
})

const historyColumns = [
  { title: '账期', key: 'period', render: r => `${r.periodStart} ~ ${r.periodEnd}` },
  { title: '下行', key: 'rxBytes', render: r => formatBytes(r.rxBytes) },
  { title: '上行', key: 'txBytes', render: r => formatBytes(r.txBytes) },
  { title: '计费', key: 'billedBytes', render: r => formatBytes(r.billedBytes) },
  { title: '限额', key: 'limitBytes', render: r => (r.limitBytes ? formatBytes(r.limitBytes, 0) : '不限') },
  { title: '状态', key: 'closed', render: r => h(NTag, { size: 'small', bordered: false, type: r.closed ? 'default' : 'success' }, () => (r.closed ? '已结算' : '进行中')) },
]
const alertColumns = [
  { title: '时间', key: 'startedAt', width: 170, render: r => formatDateTime(r.startedAt) },
  { title: '规则', key: 'rule', width: 110, render: r => RULE_NAMES[r.rule] },
  { title: '状态', key: 'status', width: 90, render: r => h(NTag, { size: 'small', bordered: false, type: r.status === 1 ? 'error' : 'success' }, () => (r.status === 1 ? '进行中' : '已恢复')) },
  { title: '内容', key: 'message', ellipsis: { tooltip: true } },
]

function resetTraffic() {
  $dialog.confirm({ title: '清零流量', type: 'warning', content: '确定将本账期已用流量清零？', async confirm() {
    await api.resetTraffic(id.value)
    $message.success('已清零')
    load()
  } })
}
</script>
