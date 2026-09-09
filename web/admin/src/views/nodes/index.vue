<template>
  <CommonPage>
    <template #action>
      <n-button type="primary" @click="formModal.open()">
        <i class="i-fe:plus mr-4" />
        新建节点
      </n-button>
    </template>

    <MeCrud ref="$table" v-model:query-items="queryItems" :columns="columns" :get-data="api.list" :scroll-x="1700" :is-pagination="false" :remote="false" @on-data-change="onData">
      <MeQueryItem label="关键词" :label-width="60">
        <n-input v-model:value="queryItems.keyword" clearable placeholder="名称 / 备注 / 主机名 / IP" />
      </MeQueryItem>
      <MeQueryItem label="状态" :label-width="50">
        <n-select v-model:value="queryItems.status" clearable :options="STATUS_OPTIONS" placeholder="全部" />
      </MeQueryItem>
      <MeQueryItem label="启用" :label-width="50">
        <n-select v-model:value="queryItems.enabled" clearable :options="[{ value: true, label: '是' }, { value: false, label: '否' }]" placeholder="全部" />
      </MeQueryItem>
    </MeCrud>

    <NodeFormModal ref="formModal" @saved="onSaved" />
    <InstallScriptModal ref="installModal" />
    <MeModal ref="keyModal" title="节点密钥" width="520px" :show-ok="false" cancel-text="关闭">
      <n-alert type="warning" class="mb-12">密钥可直接连接 Master，请勿泄露。</n-alert>
      <div class="flex items-center">
        <n-input :value="revealedKey" readonly class="flex-1 font-mono" />
        <n-button class="ml-8" type="primary" @click="copy(revealedKey)">复制</n-button>
      </div>
    </MeModal>
  </CommonPage>
</template>

<script setup>
import { useClipboard } from '@vueuse/core'
import { NButton, NDropdown, NTag, NTooltip } from 'naive-ui'
import { MeCrud, MeModal, MeQueryItem } from '@/components'
import { FlagName, InstallScriptModal, IpList, MiniBar, NodeFormModal, NodeStatusTag, TrafficBar } from '@/components/snm'
import { STATUS_OPTIONS } from '@/constants/snm'
import { useLiveStore } from '@/store'
import { daysLeftTag, formatBps, formatDate, formatMb, formatRelative } from '@/utils/format'
import api from './api'

defineOptions({ name: 'Nodes' })

const router = useRouter()
const route = useRoute()
const liveStore = useLiveStore()
const $table = ref(null)
const formModal = ref(null)
const installModal = ref(null)
const keyModal = ref(null)
const revealedKey = ref('')
const { copy: doCopy } = useClipboard()
const queryItems = ref({ keyword: null, status: route.query.status ? Number(route.query.status) : null, enabled: null })
const rows = ref([])

onMounted(() => $table.value?.handleSearch())
watch(() => liveStore.nodesChangedTick, () => $table.value?.handleSearch(true))
function onData(data) {
  rows.value = data
}

// merge live values into the rows without refetching
const live = computed(() => liveStore.nodes)
function L(r) {
  return live.value[r.id]
}

async function copy(text) {
  await doCopy(text)
  $message.success('已复制到剪贴板')
}

function onSaved({ node, created }) {
  $table.value?.handleSearch(true)
  if (created)
    installModal.value?.open(node.id, { created: true })
}

async function more(key, r) {
  switch (key) {
    case 'reveal':
      $dialog.confirm({ title: '查看密钥', type: 'warning', content: `确定显示节点“${r.publicName}”的密钥？此操作会记录审计日志。`, async confirm() {
        const { data } = await api.revealKey(r.id)
        revealedKey.value = data.agentKey
        keyModal.value?.open()
      } })
      break
    case 'rotate':
      $dialog.confirm({ title: '轮换密钥', type: 'warning', content: '轮换后旧密钥立即失效，需要重新安装或更新探针配置，是否继续？', async confirm() {
        const { data } = await api.rotateKey(r.id)
        revealedKey.value = data.agentKey
        $message.success('密钥已轮换')
        keyModal.value?.open()
      } })
      break
    case 'resetTraffic':
      $dialog.confirm({ title: '清零流量', type: 'warning', content: '确定将本账期已用流量清零？', async confirm() {
        await api.resetTraffic(r.id)
        $message.success('已清零')
        $table.value?.handleSearch(true)
      } })
      break
    case 'toggle':
      await api.update(r.id, { enabled: !r.enabled })
      $message.success(r.enabled ? '已禁用' : '已启用')
      $table.value?.handleSearch(true)
      break
    case 'delete':
      $dialog.confirm({ title: '删除节点', type: 'error', content: `确定删除节点“${r.publicName}”？其全部历史数据将被清除。`, async confirm() {
        await api.remove(r.id)
        $message.success('已删除')
        $table.value?.handleSearch(true)
      } })
      break
  }
}

const columns = [
  { title: '节点', key: 'publicName', width: 220, fixed: 'left', render: r => h(FlagName, { cc: r.countryCode, name: r.publicName, remark: r.adminRemark }) },
  { title: '状态', key: 'status', width: 130, render: (r) => {
    const l = L(r)
    const status = l?.status ?? r.state.status
    const seen = l?.lastSeen || r.state.lastSeenAt
    return h('div', { class: 'flex-col' }, [h(NodeStatusTag, { status }), h('span', { class: 'text-11 opacity-50 mt-2' }, seen ? formatRelative(seen) : (r.enabled ? '等待首次上报' : '已禁用'))])
  } },
  { title: 'IP', key: 'ips', width: 190, render: r => h(IpList, { ips: r.ips }) },
  { title: '系统', key: 'os', width: 200, render: r => h('div', { class: 'flex-col text-12' }, [
    h('span', { class: 'truncate' }, r.hardware.os ? `${r.hardware.os.split(' (')[0]} · ${r.hardware.arch || ''}` : '—'),
    h('span', { class: 'opacity-50' }, r.hardware.cpuCores ? `${r.hardware.cpuCores} C / ${formatMb(r.hardware.memTotalMb)}` : ''),
  ]) },
  { title: 'CPU', key: 'cpu', width: 130, render: r => h(MiniBar, { value: (L(r)?.cpu ?? r.live?.cpuPermille ?? 0) / 10 }) },
  { title: '内存', key: 'mem', width: 130, render: r => h(MiniBar, { value: r.hardware.memTotalMb ? ((L(r)?.memUsedMb ?? r.live?.memUsedMb ?? 0) * 100) / r.hardware.memTotalMb : 0 }) },
  { title: '磁盘', key: 'disk', width: 130, render: (r) => {
    const total = r.hardware.disks.reduce((a, d) => a + d.totalMb, 0)
    const usedArr = L(r)?.diskUsedMb ?? r.live?.diskUsedMb ?? []
    const used = usedArr.reduce((a, b) => a + Number(b), 0)
    const tip = r.hardware.disks.map((d, i) => `${d.mount} ${formatMb(usedArr[i] || 0)} / ${formatMb(d.totalMb)}`).join('\n')
    return h(NTooltip, { disabled: !tip }, { trigger: () => h(MiniBar, { value: total ? (used * 100) / total : 0 }), default: () => h('pre', { class: 'text-12 m-0' }, tip) })
  } },
  { title: '网速', key: 'net', width: 150, render: r => h('div', { class: 'flex-col text-12 tabular-nums' }, [h('span', `↓ ${formatBps(L(r)?.rx ?? r.live?.rxBps ?? 0)}`), h('span', `↑ ${formatBps(L(r)?.tx ?? r.live?.txBps ?? 0)}`)]) },
  { title: '流量', key: 'traffic', width: 190, render: r => h(TrafficBar, { used: L(r)?.tUsed ?? r.traffic.billedBytes, limit: r.traffic.limitBytes }) },
  { title: '到期', key: 'expires', width: 140, render: (r) => {
    if (!r.finance.expiresAt)
      return h('span', { class: 'opacity-40' }, '—')
    const t = daysLeftTag(r.finance.daysLeft)
    return h('div', { class: 'flex-col text-12' }, [h('span', formatDate(r.finance.expiresAt)), h(NTag, { size: 'tiny', type: t.type, bordered: false }, () => t.text)])
  } },
  { title: '操作', key: 'actions', width: 260, fixed: 'right', render: r => h('div', { class: 'flex items-center gap-4' }, [
    h(NButton, { size: 'small', quaternary, type: 'primary', onClick: () => router.push(`/nodes/${r.id}`) }, () => '详情'),
    h(NButton, { size: 'small', quaternary, onClick: () => formModal.value?.open(r) }, () => '编辑'),
    h(NButton, { size: 'small', quaternary, onClick: () => installModal.value?.open(r.id) }, () => '安装脚本'),
    h(NDropdown, {
      options: [
        { label: '查看密钥', key: 'reveal' },
        { label: '轮换密钥', key: 'rotate' },
        { label: '清零本期流量', key: 'resetTraffic' },
        { label: r.enabled ? '禁用' : '启用', key: 'toggle' },
        { type: 'divider', key: 'd' },
        { label: '删除', key: 'delete', props: { style: 'color:#d03050' } },
      ],
      onSelect: key => more(key, r),
    }, () => h(NButton, { size: 'small', quaternary }, () => '更多 ▾')),
  ]) },
]
const quaternary = true
</script>
