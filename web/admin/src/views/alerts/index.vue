<template>
  <CommonPage>
    <template #action>
      <n-button @click="purge">清理 90 天前已恢复记录</n-button>
    </template>
    <n-alert v-if="hasNew" type="info" class="mb-12" closable @close="hasNew = false">
      有新的告警事件，<n-button text type="primary" @click="refresh">点击刷新</n-button>
    </n-alert>
    <MeCrud ref="$table" v-model:query-items="queryItems" :columns="columns" :get-data="api.list" :scroll-x="1400" expand>
      <MeQueryItem label="节点" :label-width="50">
        <n-select v-model:value="queryItems.nodeId" clearable filterable :options="nodeOptions" placeholder="全部" />
      </MeQueryItem>
      <MeQueryItem label="规则" :label-width="50">
        <n-select v-model:value="queryItems.rule" clearable :options="RULE_OPTIONS" placeholder="全部" />
      </MeQueryItem>
      <MeQueryItem label="级别" :label-width="50">
        <n-select v-model:value="queryItems.severity" clearable :options="SEVERITY_OPTIONS" placeholder="全部" />
      </MeQueryItem>
      <MeQueryItem label="状态" :label-width="50">
        <n-select v-model:value="queryItems.status" clearable :options="[{ value: 1, label: '进行中' }, { value: 2, label: '已恢复' }]" placeholder="全部" />
      </MeQueryItem>
      <MeQueryItem label="确认" :label-width="50">
        <n-select v-model:value="queryItems.acknowledged" clearable :options="[{ value: false, label: '未确认' }, { value: true, label: '已确认' }]" placeholder="全部" />
      </MeQueryItem>
    </MeCrud>

    <n-drawer v-model:show="drawer" :width="560">
      <n-drawer-content v-if="current" :title="current.title" closable>
        <n-descriptions :column="1" label-placement="left" size="small" bordered>
          <n-descriptions-item label="节点">{{ current.nodeName }}</n-descriptions-item>
          <n-descriptions-item label="规则 / 级别">{{ RULE_NAMES[current.rule] }} / {{ SEVERITY[current.severity] }}</n-descriptions-item>
          <n-descriptions-item label="内容">{{ current.message }}</n-descriptions-item>
          <n-descriptions-item label="值 / 阈值">{{ current.value }} / {{ current.threshold }}</n-descriptions-item>
          <n-descriptions-item label="开始">{{ formatDateTime(current.startedAt) }}</n-descriptions-item>
          <n-descriptions-item label="恢复">{{ formatDateTime(current.resolvedAt) }}</n-descriptions-item>
          <n-descriptions-item label="确认">{{ formatDateTime(current.acknowledgedAt) }}</n-descriptions-item>
        </n-descriptions>
        <h3 class="mt-16 text-14">投递记录</h3>
        <n-data-table size="small" :columns="deliveryColumns" :data="current.deliveries" :bordered="false" class="mt-8" />
      </n-drawer-content>
    </n-drawer>
  </CommonPage>
</template>

<script setup>
import { NButton, NTag } from 'naive-ui'
import { MeCrud, MeQueryItem } from '@/components'
import { RULE_NAMES, RULE_OPTIONS, SEVERITY, SEVERITY_OPTIONS, SEVERITY_TYPE } from '@/constants/snm'
import { useLiveStore } from '@/store'
import { formatDateTime, formatDuration } from '@/utils/format'
import nodesApi from '@/views/nodes/api'
import api from './api'

const route = useRoute()
const liveStore = useLiveStore()
const $table = ref(null)
const queryItems = ref({ nodeId: null, rule: null, severity: null, status: route.query.status ? Number(route.query.status) : null, acknowledged: null })
const nodeOptions = ref([])
const drawer = ref(false)
const current = ref(null)
const hasNew = ref(false)

onMounted(async () => {
  $table.value?.handleSearch()
  const { data } = await nodesApi.list({ pageSize: 0 })
  nodeOptions.value = (data.pageData || []).map(n => ({ value: n.id, label: n.publicName }))
})
watch(() => liveStore.alerts.length, () => { hasNew.value = true })
function refresh() {
  hasNew.value = false
  $table.value?.handleSearch(true)
}

async function ack(r) {
  await api.ack(r.id)
  $message.success('已确认')
  refresh()
}
function purge() {
  $dialog.confirm({ title: '清理告警', type: 'warning', content: '删除 90 天前已恢复的告警记录？', async confirm() {
    const before = new Date(Date.now() - 90 * 86400000).toISOString()
    const { data } = await api.purge({ before, status: 2 })
    $message.success(`已删除 ${data.deleted} 条`)
    refresh()
  } })
}

const columns = [
  { title: '时间', key: 'startedAt', width: 190, render: r => h('div', { class: 'flex-col text-12' }, [
    h('span', formatDateTime(r.startedAt)),
    r.resolvedAt ? h('span', { class: 'opacity-50' }, `已恢复 · 持续 ${formatDuration((new Date(r.resolvedAt) - new Date(r.startedAt)) / 1000)}`) : null,
  ]) },
  { title: '节点', key: 'nodeName', width: 160 },
  { title: '规则', key: 'rule', width: 110, render: r => h(NTag, { size: 'small', bordered: false }, () => RULE_NAMES[r.rule] || r.rule) },
  { title: '级别', key: 'severity', width: 80, render: r => h(NTag, { size: 'small', bordered: false, type: SEVERITY_TYPE[r.severity] }, () => SEVERITY[r.severity]) },
  { title: '状态', key: 'status', width: 90, render: r => h(NTag, { size: 'small', bordered: false, type: r.status === 1 ? 'error' : 'success' }, () => (r.status === 1 ? '进行中' : '已恢复')) },
  { title: '内容', key: 'message', ellipsis: { tooltip: true }, render: r => `${r.title} — ${r.message}` },
  { title: '通知', key: 'deliveries', width: 90, render: (r) => {
    const ok = r.deliveries.filter(d => d.ok).length
    return h('span', { class: ok < r.deliveries.length ? 'text-#d03050' : '' }, r.deliveries.length ? `${ok}/${r.deliveries.length}` : (r.notified ? '—' : '静默'))
  } },
  { title: '确认', key: 'acknowledgedAt', width: 110, render: r => (r.acknowledgedAt ? h('span', { class: 'text-12 opacity-60' }, formatDateTime(r.acknowledgedAt)) : h(NButton, { size: 'tiny', onClick: () => ack(r) }, () => '确认')) },
  { title: '操作', key: 'actions', width: 80, render: r => h(NButton, { size: 'tiny', quaternary: true, type: 'primary', onClick: () => { current.value = r; drawer.value = true } }, () => '详情') },
]
const deliveryColumns = [
  { title: '渠道', key: 'channelName' },
  { title: '类型', key: 'kind', render: r => ({ 1: '触发', 2: '恢复', 3: '测试' })[r.kind] },
  { title: '尝试', key: 'attempt', width: 60 },
  { title: '结果', key: 'ok', width: 80, render: r => h(NTag, { size: 'tiny', bordered: false, type: r.ok ? 'success' : 'error' }, () => (r.ok ? `成功 ${r.statusCode}` : `失败 ${r.statusCode || ''}`)) },
  { title: '错误', key: 'error', ellipsis: { tooltip: true } },
  { title: '时间', key: 'createdAt', width: 160, render: r => formatDateTime(r.createdAt) },
]
</script>
