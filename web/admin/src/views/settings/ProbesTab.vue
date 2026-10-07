<template>
  <n-space vertical :size="16">
    <n-alert type="info" :show-icon="false">
      节点主动探测指定目标，配置约 30 秒内生效。ICMP 统计往返延时与丢包率，TCP 统计建连耗时与失败率。
      公开页面仅展示线路名称和结果。需要升级 Agent；ICMP 无权限时显示“不支持”，可改用 TCP。
      保存后，所有已启用节点都会参与探测，新增节点自动加入，无需手动选择。
    </n-alert>
    <n-space justify="space-between">
      <span>探测目标 {{ targets.length }} / 16</span>
      <n-space>
        <n-button :disabled="targets.length >= 16 || loading" @click="add">
          新增目标
        </n-button>
        <n-button type="primary" :loading="saving" :disabled="loading" @click="save">
          保存配置
        </n-button>
      </n-space>
    </n-space>
    <n-spin :show="loading">
      <n-empty v-if="!targets.length" description="尚未配置探测目标" />
      <n-card v-for="(target, index) in targets" :key="target.id" :title="target.name || '新目标'" size="small" class="mb-16">
        <template #header-extra>
          <n-space align="center">
            <n-switch v-model:value="target.enabled" />
            <n-button size="small" type="error" quaternary @click="targets.splice(index, 1)">
              移除
            </n-button>
          </n-space>
        </template>
        <n-form label-placement="top">
          <n-grid :cols="12" :x-gap="16" responsive="screen" item-responsive>
            <n-form-item-gi span="12 m:4" label="公开线路名称">
              <n-input v-model:value="target.name" maxlength="48" placeholder="例如：上海电信" />
            </n-form-item-gi>
            <n-form-item-gi span="12 m:5" label="目标 IP / 域名（仅后台）">
              <n-input v-model:value="target.address" placeholder="不包含协议或端口" />
            </n-form-item-gi>
            <n-form-item-gi span="6 m:3" label="探测方式">
              <n-select v-model:value="target.kind" :options="[{ label: 'ICMP', value: 0 }, { label: 'TCP', value: 1 }]" />
            </n-form-item-gi>
            <n-form-item-gi v-if="target.kind === 1" span="6 m:3" label="TCP 端口">
              <n-input-number v-model:value="target.port" :min="1" :max="65535" />
            </n-form-item-gi>
            <n-form-item-gi span="6 m:3" label="间隔（秒）">
              <n-input-number v-model:value="target.intervalSec" :min="10" :max="3600" />
            </n-form-item-gi>
            <n-form-item-gi span="6 m:3" label="超时（毫秒）">
              <n-input-number v-model:value="target.timeoutMs" :min="200" :max="10000" :step="100" />
            </n-form-item-gi>
            <n-form-item-gi span="12" label="参与节点">
              <n-tag type="info" :bordered="false">
                全部已启用节点 · 新增节点自动加入
              </n-tag>
            </n-form-item-gi>
          </n-grid>
        </n-form>
      </n-card>
    </n-spin>
    <n-text depth="3">
      每条线路保留最近 60 次实时结果；停用、删除或修改探测定义会清空对应窗口。探测使用节点直连网络，上报代理不参与测量。
    </n-text>
  </n-space>
</template>

<script setup>
import { request } from '@/utils'

const targets = ref([])
const loading = ref(true)
const saving = ref(false)
async function load() {
  loading.value = true
  try {
    const { data } = await request.get('/settings/probes')
    targets.value = data.targets.map(t => ({ ...t, allNodes: true, nodeIds: [] }))
  }
  finally { loading.value = false }
}
function add() {
  targets.value.push({ id: Math.max(0, ...targets.value.map(t => t.id)) + 1, name: '', address: '', kind: 1, port: 443, intervalSec: 30, timeoutMs: 2000, allNodes: true, nodeIds: [], enabled: true })
}
async function save() {
  if (targets.value.some(t => !t.name.trim() || !t.address.trim())) {
    return $message.warning('请填写线路名称和目标地址')
  }
  saving.value = true
  try {
    const { data } = await request.put('/settings/probes', targets.value.map(t => ({ ...t, name: t.name.trim(), address: t.address.trim(), allNodes: true, nodeIds: [] })))
    targets.value = data.targets
    $message.success('已保存，Agent 将在约 30 秒内应用')
  }
  finally { saving.value = false }
}
onMounted(load)
</script>
