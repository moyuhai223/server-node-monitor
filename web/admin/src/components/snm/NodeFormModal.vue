<template>
  <MeModal ref="modalRef" width="680px" :title="isEdit ? '编辑节点' : '新建节点'" @ok="save">
    <n-form ref="formRef" :model="form" label-placement="left" label-width="110" :rules="rules">
      <n-tabs type="line" animated>
        <n-tab-pane name="basic" tab="基本">
          <n-form-item label="展示名称" path="publicName">
            <n-input v-model:value="form.publicName" placeholder="前台大屏展示名，如 HK-Node-01" maxlength="64" show-count />
          </n-form-item>
          <n-form-item label="运维备注" path="adminRemark">
            <n-input v-model:value="form.adminRemark" placeholder="仅后台可见，如 核心 DB-勿动" maxlength="256" />
          </n-form-item>
          <n-grid :cols="2" x-gap="12">
            <n-form-item-gi label="启用" path="enabled">
              <n-switch v-model:value="form.enabled" />
            </n-form-item-gi>
            <n-form-item-gi label="大屏可见" path="publicVisible">
              <n-switch v-model:value="form.publicVisible" />
            </n-form-item-gi>
            <n-form-item-gi label="排序" path="sortOrder">
              <n-input-number v-model:value="form.sortOrder" :step="10" class="w-full" />
            </n-form-item-gi>
            <n-form-item-gi label="心跳间隔 (ms)" path="intervalMs">
              <n-input-number v-model:value="form.intervalMs" :min="1000" :max="60000" :step="500" class="w-full" />
            </n-form-item-gi>
            <n-form-item-gi label="国家覆盖" path="countryCodeOverride">
              <n-select v-model:value="form.countryCodeOverride" :options="countryOptions" filterable clearable placeholder="自动识别" />
            </n-form-item-gi>
            <n-form-item-gi label="时区" path="timeZoneId">
              <n-select v-model:value="form.timeZoneId" :options="TIMEZONES" filterable clearable placeholder="跟随全局设置" />
            </n-form-item-gi>
          </n-grid>
          <n-form-item label="备注" path="notes">
            <n-input v-model:value="form.notes" type="textarea" :autosize="{ minRows: 2, maxRows: 5 }" maxlength="2000" />
          </n-form-item>
        </n-tab-pane>

        <n-tab-pane name="traffic" tab="流量">
          <n-form-item label="月流量限额">
            <n-input-number v-model:value="form.limitValue" :min="0" :precision="0" class="w-200" placeholder="0 = 不限" />
            <n-select v-model:value="form.limitUnit" :options="[{ value: 'GB', label: 'GB' }, { value: 'TB', label: 'TB' }]" class="ml-8 w-90" />
            <span class="ml-8 text-12 opacity-60">1 GB = 10⁹ 字节（与 VPS 商家口径一致）</span>
          </n-form-item>
          <n-form-item label="计费方式" path="traffic.countMode">
            <n-radio-group v-model:value="form.traffic.countMode">
              <n-radio v-for="m in COUNT_MODES" :key="m.value" :value="m.value">{{ m.label }}</n-radio>
            </n-radio-group>
          </n-form-item>
          <n-form-item label="账单重置日" path="traffic.resetDay">
            <n-input-number v-model:value="form.traffic.resetDay" :min="1" :max="31" class="w-200" />
            <span class="ml-8 text-12 opacity-60">大于当月天数时按月末计</span>
          </n-form-item>
        </n-tab-pane>

        <n-tab-pane name="finance" tab="财务">
          <n-grid :cols="2" x-gap="12">
            <n-form-item-gi label="供应商" path="finance.vendor">
              <n-input v-model:value="form.finance.vendor" maxlength="64" />
            </n-form-item-gi>
            <n-form-item-gi label="续费价格" path="finance.price">
              <n-input v-model:value="form.finance.price" placeholder="49.99" />
            </n-form-item-gi>
            <n-form-item-gi label="币种" path="finance.currency">
              <n-select v-model:value="form.finance.currency" :options="CURRENCIES" clearable />
            </n-form-item-gi>
            <n-form-item-gi label="付费周期" path="finance.billingCycleMonths">
              <n-select v-model:value="form.finance.billingCycleMonths" :options="BILLING_CYCLES" />
            </n-form-item-gi>
            <n-form-item-gi label="到期日" path="finance.expiresAt">
              <n-date-picker v-model:formatted-value="form.finance.expiresAt" type="date" value-format="yyyy-MM-dd" clearable class="w-full" />
            </n-form-item-gi>
            <n-form-item-gi label="自动续费" path="finance.autoRenew">
              <n-switch v-model:value="form.finance.autoRenew" />
            </n-form-item-gi>
          </n-grid>
          <n-form-item label="续费链接" path="finance.renewUrl">
            <n-input v-model:value="form.finance.renewUrl" placeholder="https://" />
          </n-form-item>
        </n-tab-pane>

        <n-tab-pane name="alerts" tab="告警">
          <n-form-item label="启用告警" path="alerts.alertsEnabled">
            <n-switch v-model:value="form.alerts.alertsEnabled" />
          </n-form-item>
          <n-grid :cols="2" x-gap="12">
            <n-form-item-gi label="CPU 阈值 (%)" path="alerts.cpuAlertPct">
              <n-input-number v-model:value="form.alerts.cpuAlertPct" :min="50" :max="100" clearable class="w-full" :placeholder="`留空 = 全局 (${g.cpuPct ?? 90})`" />
            </n-form-item-gi>
            <n-form-item-gi label="流量预警 (%)" path="alerts.trafficAlertPct">
              <n-input-number v-model:value="form.alerts.trafficAlertPct" :min="50" :max="99" clearable class="w-full" :placeholder="`留空 = 全局 (${g.trafficWarnPct ?? 80})`" />
            </n-form-item-gi>
            <n-form-item-gi label="离线超时 (秒)" path="alerts.offlineAlertSec">
              <n-input-number v-model:value="form.alerts.offlineAlertSec" :min="10" :max="600" clearable class="w-full" :placeholder="`留空 = 全局 (${g.offlineTimeoutSec ?? 30})`" />
            </n-form-item-gi>
            <n-form-item-gi label="磁盘阈值 (%)" path="alerts.diskAlertPct">
              <n-input-number v-model:value="form.alerts.diskAlertPct" :min="50" :max="100" clearable class="w-full" :placeholder="`留空 = 全局 (${g.diskPct ?? 90})`" />
            </n-form-item-gi>
          </n-grid>
        </n-tab-pane>
      </n-tabs>
    </n-form>
  </MeModal>
</template>

<script setup>
import { MeModal } from '@/components'
import { BILLING_CYCLES, COUNT_MODES, CURRENCIES, TIMEZONES } from '@/constants/snm'
import { useSystemStore } from '@/store'
import { countryName, flagEmoji } from '@/utils/format'
import api from '@/views/nodes/api'

const emit = defineEmits(['saved'])
const modalRef = ref(null)
const formRef = ref(null)
const isEdit = ref(false)
const editId = ref(null)
const systemStore = useSystemStore()
const g = computed(() => systemStore.settings?.alert || {})

const COUNTRY_CODES = ['US', 'CN', 'HK', 'TW', 'JP', 'KR', 'SG', 'DE', 'GB', 'FR', 'NL', 'RU', 'CA', 'AU', 'IN', 'BR', 'VN', 'TH', 'MY', 'ID', 'PH', 'AE', 'TR', 'IT', 'ES', 'SE', 'FI', 'CH', 'PL', 'UA', 'ZA', 'AR', 'MX', 'CL', 'NZ', 'IE', 'NO', 'DK', 'BE', 'AT', 'CZ', 'PT', 'RO', 'HU', 'IL', 'SA', 'EG', 'NG', 'KZ', 'PK', 'BD', 'LK', 'MO', 'KH', 'LU', 'EE', 'LV', 'LT', 'BG', 'GR', 'RS', 'MD', 'IS']
const countryOptions = COUNTRY_CODES.map(cc => ({ value: cc, label: `${flagEmoji(cc)} ${countryName(cc)} (${cc})` }))

function blank() {
  return {
    publicName: '', adminRemark: '', enabled: true, publicVisible: true, sortOrder: 0, intervalMs: 2000, countryCodeOverride: null, timeZoneId: null, notes: '',
    limitValue: 0, limitUnit: 'TB',
    traffic: { countMode: 0, resetDay: 1 },
    finance: { vendor: '', price: '', currency: null, billingCycleMonths: 0, expiresAt: null, autoRenew: false, renewUrl: '' },
    alerts: { alertsEnabled: true, cpuAlertPct: null, trafficAlertPct: null, offlineAlertSec: null, diskAlertPct: null },
  }
}
const form = ref(blank())

const rules = {
  publicName: { required: true, message: '请输入展示名称', trigger: ['blur', 'input'] },
  'finance.price': { validator: (_, v) => !v || /^\d+(\.\d{1,2})?$/.test(String(v)), message: '价格格式：最多两位小数', trigger: ['blur', 'input'] },
  'finance.renewUrl': { validator: (_, v) => !v || /^https?:\/\//.test(v), message: '须为 http(s) 链接', trigger: ['blur', 'input'] },
}

function toForm(node) {
  const f = blank()
  Object.assign(f, {
    publicName: node.publicName, adminRemark: node.adminRemark || '', enabled: node.enabled, publicVisible: node.publicVisible, sortOrder: node.sortOrder,
    intervalMs: node.intervalMs, countryCodeOverride: node.countryCodeOverride, timeZoneId: node.timeZoneId, notes: node.notes || '',
  })
  const limit = node.traffic?.limitBytes || 0
  if (limit >= 1e12 && limit % 1e12 === 0) {
    f.limitValue = limit / 1e12
    f.limitUnit = 'TB'
  }
  else {
    f.limitValue = Math.round(limit / 1e9)
    f.limitUnit = 'GB'
  }
  f.traffic = { countMode: node.traffic?.countMode ?? 0, resetDay: node.traffic?.resetDay ?? 1 }
  f.finance = {
    vendor: node.finance?.vendor || '', price: node.finance?.price ?? '', currency: node.finance?.currency ?? null,
    billingCycleMonths: node.finance?.billingCycleMonths ?? 0, expiresAt: node.finance?.expiresAt ?? null, autoRenew: !!node.finance?.autoRenew, renewUrl: node.finance?.renewUrl || '',
  }
  f.alerts = {
    alertsEnabled: node.alerts?.alertsEnabled ?? true, cpuAlertPct: node.alerts?.cpuAlertPct ?? null, trafficAlertPct: node.alerts?.trafficAlertPct ?? null,
    offlineAlertSec: node.alerts?.offlineAlertSec ?? null, diskAlertPct: node.alerts?.diskAlertPct ?? null,
  }
  return f
}

function toPayload(f) {
  return {
    publicName: f.publicName?.trim(), adminRemark: f.adminRemark || null, enabled: f.enabled, publicVisible: f.publicVisible, sortOrder: f.sortOrder ?? 0,
    intervalMs: f.intervalMs, countryCodeOverride: f.countryCodeOverride || null, timeZoneId: f.timeZoneId || null, notes: f.notes || null,
    traffic: { limitBytes: Math.round((f.limitValue || 0) * (f.limitUnit === 'TB' ? 1e12 : 1e9)), countMode: f.traffic.countMode, resetDay: f.traffic.resetDay },
    finance: { ...f.finance, price: f.finance.price === '' ? null : String(f.finance.price), vendor: f.finance.vendor || null, renewUrl: f.finance.renewUrl || null },
    alerts: { ...f.alerts },
  }
}

async function save() {
  await formRef.value?.validate()
  const payload = toPayload(form.value)
  const { data } = isEdit.value ? await api.update(editId.value, payload) : await api.create(payload)
  $message.success(isEdit.value ? '保存成功' : '节点已创建')
  emit('saved', { node: data, created: !isEdit.value })
}

function open(node) {
  systemStore.loadSettings()
  isEdit.value = !!node
  editId.value = node?.id ?? null
  form.value = node ? toForm(node) : blank()
  modalRef.value?.open()
}

defineExpose({ open })
</script>
