<template>
  <MeModal ref="modalRef" width="620px" :title="isEdit ? '编辑渠道' : '新增渠道'" @ok="save">
    <n-form ref="formRef" :model="form" label-placement="left" label-width="110">
      <n-form-item label="类型" path="type" :rule="required">
        <n-radio-group v-model:value="form.type" :disabled="isEdit">
          <n-radio value="telegram">Telegram Bot</n-radio>
          <n-radio value="webhook">Webhook</n-radio>
        </n-radio-group>
      </n-form-item>
      <n-form-item label="名称" path="name" :rule="required">
        <n-input v-model:value="form.name" maxlength="64" />
      </n-form-item>
      <n-form-item label="启用">
        <n-switch v-model:value="form.enabled" />
      </n-form-item>
      <n-form-item label="规则范围">
        <n-checkbox-group v-model:value="form.rules">
          <n-checkbox v-for="r in RULE_OPTIONS" :key="r.value" :value="r.value" :label="r.label" />
        </n-checkbox-group>
        <span class="ml-8 text-12 opacity-50">不选 = 全部</span>
      </n-form-item>
      <n-form-item label="最低级别">
        <n-select v-model:value="form.minSeverity" :options="SEVERITY_OPTIONS" />
      </n-form-item>

      <template v-if="form.type === 'telegram'">
        <n-form-item label="Bot Token" path="config.botToken">
          <n-input v-model:value="form.config.botToken" type="password" show-password-on="click" :placeholder="isEdit ? '留空 (****) 保留原值' : '123456:ABC-DEF...'" />
        </n-form-item>
        <n-form-item label="Chat ID" path="config.chatId" :rule="required">
          <n-input v-model:value="form.config.chatId" placeholder="-1001234567890" />
        </n-form-item>
        <n-form-item label="格式">
          <n-select v-model:value="form.config.parseMode" :options="[{ value: 'HTML', label: 'HTML' }, { value: 'MarkdownV2', label: 'MarkdownV2' }, { value: 'none', label: '纯文本' }]" />
        </n-form-item>
        <n-form-item label="静默通知">
          <n-switch v-model:value="form.config.disableNotification" />
        </n-form-item>
      </template>

      <template v-else>
        <n-form-item label="URL" path="config.url" :rule="required">
          <n-input v-model:value="form.config.url" placeholder="https://hooks.example.com/snm" />
        </n-form-item>
        <n-form-item label="方法">
          <n-select v-model:value="form.config.method" :options="[{ value: 'POST', label: 'POST' }, { value: 'PUT', label: 'PUT' }]" />
        </n-form-item>
        <n-form-item label="签名密钥">
          <n-input v-model:value="form.config.secret" type="password" show-password-on="click" :placeholder="isEdit ? '留空 (****) 保留原值' : 'HMAC-SHA256 签名 (X-SNM-Signature)'" />
        </n-form-item>
        <n-form-item label="超时 (秒)">
          <n-input-number v-model:value="form.config.timeoutSec" :min="3" :max="60" />
        </n-form-item>
        <n-form-item label="自定义请求头">
          <n-dynamic-input v-model:value="form.headerList" :max="10" preset="pair" key-placeholder="Header" value-placeholder="值" />
        </n-form-item>
        <n-form-item label="正文模板">
          <n-input v-model:value="form.config.bodyTemplate" type="textarea" :autosize="{ minRows: 2, maxRows: 6 }" placeholder="留空使用默认 JSON。占位符：{{event}} {{title}} {{message}} {{text}} {{severity}} {{rule}} {{node.name}} {{node.remark}} {{startedAt}} {{site.url}}" />
        </n-form-item>
      </template>
    </n-form>
    <template #footer>
      <div class="flex justify-between">
        <n-button :loading="testing" @click="test">发送测试</n-button>
        <div>
          <n-button @click="modalRef.close()">取消</n-button>
          <n-button type="primary" class="ml-12" @click="modalRef.handleOk()">保存</n-button>
        </div>
      </div>
    </template>
  </MeModal>
</template>

<script setup>
import { MeModal } from '@/components'
import { RULE_OPTIONS, SEVERITY_OPTIONS } from '@/constants/snm'
import api from '@/views/settings/api'

const emit = defineEmits(['saved'])
const modalRef = ref(null)
const formRef = ref(null)
const isEdit = ref(false)
const editId = ref(null)
const testing = ref(false)
const required = { required: true, message: '此为必填项', trigger: ['blur', 'input'] }

function blank() {
  return { type: 'telegram', name: '', enabled: true, rules: [], minSeverity: 1, headerList: [], config: { botToken: '', chatId: '', parseMode: 'HTML', disableNotification: false, url: '', method: 'POST', secret: '', timeoutSec: 10, bodyTemplate: '' } }
}
const form = ref(blank())

function toPayload() {
  const f = form.value
  const ruleMask = f.rules.reduce((m, r) => m | (1 << r), 0)
  const config = f.type === 'telegram'
    ? { botToken: f.config.botToken || undefined, chatId: f.config.chatId, parseMode: f.config.parseMode, disableNotification: f.config.disableNotification }
    : { url: f.config.url, method: f.config.method, secret: f.config.secret || undefined, timeoutSec: f.config.timeoutSec, bodyTemplate: f.config.bodyTemplate || null, headers: Object.fromEntries(f.headerList.filter(h => h.key).map(h => [h.key, h.value])) }
  if (config.botToken === '****' || config.botToken === '')
    delete config.botToken
  if (config.secret === '****' || config.secret === '')
    delete config.secret
  return { type: f.type, name: f.name, enabled: f.enabled, ruleMask, minSeverity: f.minSeverity, config }
}

async function save() {
  await formRef.value?.validate()
  const payload = toPayload()
  if (isEdit.value)
    await api.updateChannel(editId.value, payload)
  else
    await api.createChannel(payload)
  $message.success('保存成功')
  emit('saved')
}

async function test() {
  try {
    await formRef.value?.validate()
    testing.value = true
    const { data } = isEdit.value && !form.value.config.botToken && !form.value.config.secret
      ? await api.testChannel(editId.value)
      : await api.testChannelDraft(toPayload())
    $message.success(`测试消息已发送 (${data.statusCode}, ${data.elapsedMs} ms)`)
  }
  catch (e) {
    console.error(e)
  }
  finally {
    testing.value = false
  }
}

function open(channel) {
  isEdit.value = !!channel
  editId.value = channel?.id ?? null
  const f = blank()
  if (channel) {
    f.type = channel.type
    f.name = channel.name
    f.enabled = channel.enabled
    f.minSeverity = channel.minSeverity
    f.rules = RULE_OPTIONS.map(r => r.value).filter(v => channel.ruleMask & (1 << v))
    Object.assign(f.config, channel.config || {})
    f.headerList = Object.entries(channel.config?.headers || {}).map(([key, value]) => ({ key, value }))
  }
  form.value = f
  modalRef.value?.open()
}
defineExpose({ open })
</script>
