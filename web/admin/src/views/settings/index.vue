<template>
  <CommonPage>
    <n-spin :show="!settings">
      <n-tabs v-if="settings" type="line" animated>
        <n-tab-pane name="site" tab="站点">
          <n-form label-placement="left" label-width="140" class="max-w-720">
            <n-form-item label="后台标题"><n-input v-model:value="settings.site.title" /></n-form-item>
            <n-form-item label="大屏标题"><n-input v-model:value="settings.site.publicTitle" /></n-form-item>
            <n-form-item label="大屏副标题"><n-input v-model:value="settings.site.publicSubtitle" /></n-form-item>
            <n-form-item label="公开地址">
              <n-input v-model:value="settings.site.publicBaseUrl" placeholder="https://m.example.com" />
              <template #feedback>用于生成探针安装脚本的下载地址，须以 http(s):// 开头</template>
            </n-form-item>
            <n-form-item label="时区"><n-select v-model:value="settings.site.timeZone" filterable :options="TIMEZONES" /></n-form-item>
            <n-form-item label="大屏显示规格"><n-switch v-model:value="settings.public.showSpecs" /></n-form-item>
            <n-form-item label="大屏显示流量"><n-switch v-model:value="settings.public.showTraffic" /></n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['site', 'public'])">保存</n-button>
          </n-form>
        </n-tab-pane>

        <n-tab-pane name="agent" tab="探针">
          <n-form label-placement="left" label-width="180" class="max-w-720">
            <n-form-item label="探针下载地址">
              <n-input v-model:value="settings.agent.releaseBaseUrl" placeholder="https://github.com/OWNER/REPO/releases/latest/download" />
              <template #feedback>安装脚本从该地址下载 snm-agent-&lt;rid&gt;.tar.gz / .zip 与 .sha256</template>
            </n-form-item>
            <n-form-item label="默认心跳间隔 (ms)"><n-input-number v-model:value="settings.agent.defaultIntervalMs" :min="1000" :max="60000" :step="500" /></n-form-item>
            <n-form-item label="状态上报间隔 (秒)">
              <n-input-number v-model:value="settings.agent.statusIntervalSec" :min="60" :max="3600" />
              <template #feedback>修改后会即时下发到在线探针</template>
            </n-form-item>
            <n-form-item label="安装令牌有效期 (小时)"><n-input-number v-model:value="settings.agent.installTokenTtlHours" :min="1" :max="168" /></n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['agent'])">保存</n-button>
          </n-form>
        </n-tab-pane>

        <n-tab-pane name="alert" tab="告警阈值">
          <n-form label-placement="left" label-width="180" class="max-w-760">
            <n-form-item label="启用告警"><n-switch v-model:value="settings.alert.enabled" /></n-form-item>
            <n-form-item label="离线超时 (秒)"><n-input-number v-model:value="settings.alert.offlineTimeoutSec" :min="10" :max="600" /><span class="ml-12 text-12 opacity-50">超过该时长未收到心跳视为离线</span></n-form-item>
            <n-form-item label="离线连续判定次数"><n-input-number v-model:value="settings.alert.offlineConsecutive" :min="1" :max="10" /><span class="ml-12 text-12 opacity-50">每 10 秒评估一次</span></n-form-item>
            <n-form-item label="CPU 阈值 (%)"><n-input-number v-model:value="settings.alert.cpuPct" :min="50" :max="100" /></n-form-item>
            <n-form-item label="CPU 持续时间 (分钟)"><n-input-number v-model:value="settings.alert.cpuSustainMin" :min="1" :max="60" /></n-form-item>
            <n-form-item label="流量预警 (%)"><n-input-number v-model:value="settings.alert.trafficWarnPct" :min="50" :max="99" /></n-form-item>
            <n-form-item label="到期提前 (天)"><n-input-number v-model:value="settings.alert.expiryDays" :min="1" :max="60" /></n-form-item>
            <n-form-item label="到期巡检时刻 (时)"><n-input-number v-model:value="settings.alert.expiryCheckHour" :min="0" :max="23" /></n-form-item>
            <n-form-item label="冷却时间 (分钟)"><n-input-number v-model:value="settings.alert.cooldownMin" :min="30" :max="60" /><span class="ml-12 text-12 opacity-50">同一告警在冷却期内不重复通知</span></n-form-item>
            <n-form-item label="持续提醒间隔 (分钟)"><n-input-number v-model:value="settings.alert.repeatMin" :min="0" :max="1440" /><span class="ml-12 text-12 opacity-50">0 = 不重复提醒</span></n-form-item>
            <n-form-item label="磁盘告警"><n-switch v-model:value="settings.alert.diskEnabled" /></n-form-item>
            <n-form-item label="磁盘阈值 (%)"><n-input-number v-model:value="settings.alert.diskPct" :min="50" :max="100" /></n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['alert'])">保存</n-button>
          </n-form>
        </n-tab-pane>

        <n-tab-pane name="channels" tab="通知渠道">
          <div class="mb-12 flex justify-end">
            <n-button type="primary" @click="channelModal.open()">
              <i class="i-fe:plus mr-4" />新增渠道
            </n-button>
          </div>
          <n-data-table :columns="channelColumns" :data="channels" :bordered="false" size="small" />
          <ChannelFormModal ref="channelModal" @saved="loadChannels" />
        </n-tab-pane>

        <n-tab-pane name="finance" tab="财务">
          <n-form label-placement="left" label-width="160" class="max-w-640">
            <n-form-item label="基准货币"><n-select v-model:value="settings.finance.baseCurrency" :options="CURRENCIES" /></n-form-item>
            <n-form-item v-for="cur in ['USD', 'CNY', 'EUR']" :key="cur" :label="`${cur} 汇率`">
              <n-input-number v-model:value="settings.finance.rates[cur]" :min="0.0001" :step="0.1" />
              <span class="ml-12 text-12 opacity-50">1 {{ cur }} = ? 基准单位（各币种相对同一基准）</span>
            </n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['finance'])">保存</n-button>
          </n-form>
        </n-tab-pane>

        <n-tab-pane name="security" tab="安全">
          <n-form label-placement="left" label-width="180" class="max-w-640">
            <n-form-item label="访问令牌有效期 (分钟)"><n-input-number v-model:value="settings.auth.accessTokenMinutes" :min="5" :max="1440" /></n-form-item>
            <n-form-item label="刷新令牌有效期 (天)"><n-input-number v-model:value="settings.auth.refreshTokenDays" :min="1" :max="365" /></n-form-item>
            <n-form-item label="登录失败锁定次数"><n-input-number v-model:value="settings.auth.loginMaxFailures" :min="3" :max="20" /></n-form-item>
            <n-form-item label="锁定时长 (分钟)"><n-input-number v-model:value="settings.auth.loginLockMinutes" :min="1" :max="1440" /></n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['auth'])">保存</n-button>
          </n-form>
          <n-card title="修改密码" size="small" class="mt-16 max-w-640">
            <n-form ref="pwdFormRef" :model="pwd" label-placement="left" label-width="100">
              <n-form-item label="原密码" path="oldPassword" :rule="required"><n-input v-model:value="pwd.oldPassword" type="password" show-password-on="mousedown" /></n-form-item>
              <n-form-item label="新密码" path="newPassword" :rule="pwdRule"><n-input v-model:value="pwd.newPassword" type="password" show-password-on="mousedown" /></n-form-item>
              <n-form-item label="确认新密码" path="confirm" :rule="confirmRule"><n-input v-model:value="pwd.confirm" type="password" show-password-on="mousedown" /></n-form-item>
              <n-space>
                <n-button type="primary" @click="changePassword">修改密码</n-button>
                <n-button type="error" ghost @click="logoutAll">退出所有设备</n-button>
              </n-space>
            </n-form>
          </n-card>
        </n-tab-pane>

        <n-tab-pane name="geoip" tab="GeoIP">
          <n-descriptions :column="2" label-placement="left" bordered size="small" class="max-w-760">
            <n-descriptions-item label="状态">{{ settings.geoip.ready ? '已加载' : '未加载' }}</n-descriptions-item>
            <n-descriptions-item label="最近刷新">{{ formatDateTime(settings.geoip.lastRefreshUtc) }}</n-descriptions-item>
            <n-descriptions-item label="IPv4 / IPv6 段数">{{ settings.geoip.ipv4Rows }} / {{ settings.geoip.ipv6Rows }}</n-descriptions-item>
            <n-descriptions-item label="最近错误">{{ settings.geoip.lastError || '—' }}</n-descriptions-item>
          </n-descriptions>
          <n-space class="mt-12" align="center">
            <span>启用 GeoIP</span><n-switch v-model:value="settings.geoip.enabled" @update:value="save(['geoip'])" />
            <n-button :loading="geoRefreshing" @click="geoRefresh">立即刷新</n-button>
            <span class="text-12 opacity-50">数据源：@ip-location-db/asn-country (jsDelivr)</span>
          </n-space>
        </n-tab-pane>

        <n-tab-pane name="system" tab="系统">
          <n-descriptions v-if="info" :column="2" label-placement="left" bordered size="small" class="max-w-860">
            <n-descriptions-item label="版本">{{ info.version }} (协议 {{ info.protocolVersion }})</n-descriptions-item>
            <n-descriptions-item label="运行时">{{ info.framework }}</n-descriptions-item>
            <n-descriptions-item label="操作系统">{{ info.os }}</n-descriptions-item>
            <n-descriptions-item label="启动时间">{{ formatDateTime(info.startedAt) }} (已运行 {{ formatDuration(info.uptimeSec) }})</n-descriptions-item>
            <n-descriptions-item label="数据目录">{{ info.dataDir }}</n-descriptions-item>
            <n-descriptions-item label="数据库大小">{{ formatBytes(info.dbSizeBytes) }} (WAL {{ formatBytes(info.walSizeBytes) }})</n-descriptions-item>
            <n-descriptions-item label="节点">{{ info.nodes.total }} 个，{{ info.nodes.connected }} 个已连接</n-descriptions-item>
            <n-descriptions-item label="浏览器连接">大屏 {{ info.hubs.publicClients }} · 后台 {{ info.hubs.adminClients }}</n-descriptions-item>
          </n-descriptions>
          <n-form label-placement="left" label-width="200" class="mt-16 max-w-720">
            <n-form-item label="告警记录保留 (天)"><n-input-number v-model:value="settings.retention.alertEventDays" :min="7" :max="3650" /></n-form-item>
            <n-form-item label="投递记录保留 (天)"><n-input-number v-model:value="settings.retention.deliveryDays" :min="7" :max="365" /></n-form-item>
            <n-form-item label="日流量保留 (天)"><n-input-number v-model:value="settings.retention.trafficDailyDays" :min="31" :max="3650" /></n-form-item>
            <n-form-item label="时序保留 (只读)">1 分钟 {{ settings.retention.metrics1mHours }} 小时 · 1 小时 {{ settings.retention.metrics1hDays }} 天 · 1 天 {{ settings.retention.metrics1dDays }} 天</n-form-item>
            <n-button type="primary" :loading="saving" @click="save(['retention'], ['alertEventDays', 'deliveryDays', 'trafficDailyDays'])">保存</n-button>
          </n-form>
        </n-tab-pane>
      </n-tabs>
    </n-spin>
  </CommonPage>
</template>

<script setup>
import { NButton, NSwitch, NTag } from 'naive-ui'
import { CURRENCIES, RULE_NAMES, TIMEZONES } from '@/constants/snm'
import { useAuthStore, useSystemStore } from '@/store'
import { formatBytes, formatDateTime, formatDuration } from '@/utils/format'
import ChannelFormModal from './ChannelFormModal.vue'
import api from './api'

const systemStore = useSystemStore()
const authStore = useAuthStore()
const settings = ref(null)
const info = ref(null)
const channels = ref([])
const saving = ref(false)
const geoRefreshing = ref(false)
const channelModal = ref(null)

onMounted(async () => {
  const { data } = await api.get()
  settings.value = data
  systemStore.setSettings(data)
  loadChannels()
  api.systemInfo().then(r => (info.value = r.data)).catch(() => {})
})

async function save(groups, onlyKeys) {
  saving.value = true
  try {
    const body = {}
    for (const g of groups) {
      const src = settings.value[g]
      body[g] = onlyKeys ? Object.fromEntries(onlyKeys.map(k => [k, src[k]])) : { ...src }
      if (g === 'geoip')
        body[g] = { enabled: src.enabled }
    }
    const { data } = await api.update(body)
    settings.value = data
    systemStore.setSettings(data)
    $message.success('保存成功')
  }
  catch (e) {
    console.error(e)
  }
  finally {
    saving.value = false
  }
}

async function loadChannels() {
  const { data } = await api.channels()
  channels.value = data
}
async function toggleChannel(c, enabled) {
  await api.updateChannel(c.id, { enabled })
  loadChannels()
}
async function testChannel(c) {
  try {
    const { data } = await api.testChannel(c.id)
    $message.success(`测试消息已发送 (${data.statusCode}, ${data.elapsedMs} ms)`)
  }
  catch (e) {
    console.error(e)
  }
  loadChannels()
}
function removeChannel(c) {
  $dialog.confirm({ title: '删除渠道', type: 'warning', content: `确定删除渠道“${c.name}”？`, async confirm() {
    await api.removeChannel(c.id)
    $message.success('已删除')
    loadChannels()
  } })
}
const channelColumns = [
  { title: '名称', key: 'name' },
  { title: '类型', key: 'type', width: 100, render: c => h(NTag, { size: 'small', bordered: false }, () => (c.type === 'telegram' ? 'Telegram' : 'Webhook')) },
  { title: '启用', key: 'enabled', width: 80, render: c => h(NSwitch, { value: c.enabled, size: 'small', onUpdateValue: v => toggleChannel(c, v) }) },
  { title: '规则范围', key: 'ruleMask', render: c => (c.ruleMask ? Object.keys(RULE_NAMES).filter(r => c.ruleMask & (1 << r)).map(r => RULE_NAMES[r]).join('、') : '全部') },
  { title: '最低级别', key: 'minSeverity', width: 90, render: c => ({ 1: '提示', 2: '警告', 3: '严重' })[c.minSeverity] },
  { title: '最近成功', key: 'lastSuccessAt', width: 170, render: c => formatDateTime(c.lastSuccessAt) },
  { title: '最近错误', key: 'lastError', ellipsis: { tooltip: true }, render: c => c.lastError || '—' },
  { title: '操作', key: 'actions', width: 200, render: c => h('div', { class: 'flex gap-4' }, [
    h(NButton, { size: 'tiny', onClick: () => testChannel(c) }, () => '测试'),
    h(NButton, { size: 'tiny', onClick: () => channelModal.value?.open(c) }, () => '编辑'),
    h(NButton, { size: 'tiny', type: 'error', quaternary: true, onClick: () => removeChannel(c) }, () => '删除'),
  ]) },
]

async function geoRefresh() {
  geoRefreshing.value = true
  try {
    await api.geoipRefresh()
    $message.success('GeoIP 数据已刷新')
    const { data } = await api.get()
    settings.value = data
  }
  catch (e) {
    console.error(e)
  }
  finally {
    geoRefreshing.value = false
  }
}

const pwdFormRef = ref(null)
const pwd = ref({ oldPassword: '', newPassword: '', confirm: '' })
const required = { required: true, message: '此为必填项', trigger: ['blur', 'input'] }
const pwdRule = { required: true, validator: (_, v) => !!v && v.length >= 8 && v.length <= 64, message: '长度须为 8–64 位', trigger: ['blur', 'input'] }
const confirmRule = { required: true, validator: (_, v) => v === pwd.value.newPassword, message: '两次输入不一致', trigger: ['blur', 'input'] }
async function changePassword() {
  await pwdFormRef.value?.validate()
  await api.changePassword({ oldPassword: pwd.value.oldPassword, newPassword: pwd.value.newPassword, refreshToken: authStore.refreshToken })
  $message.success('密码已修改')
  pwd.value = { oldPassword: '', newPassword: '', confirm: '' }
}
function logoutAll() {
  $dialog.confirm({ title: '退出所有设备', type: 'warning', content: '将使所有设备上的登录失效（包括当前设备），是否继续？', async confirm() {
    try { await api.logoutAll() }
    catch { /* ignore */ }
    authStore.resetLoginState()
    authStore.toLogin()
  } })
}
</script>
