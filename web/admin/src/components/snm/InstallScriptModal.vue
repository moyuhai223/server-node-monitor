<template>
  <MeModal ref="modalRef" width="720px" title="安装脚本" :show-ok="false" cancel-text="关闭">
    <n-spin :show="loading">
      <n-tabs v-model:value="os" type="segment" @update:value="load">
        <n-tab-pane name="linux" tab="Linux (systemd)" />
        <n-tab-pane name="windows" tab="Windows (计划任务)" />
      </n-tabs>
      <template v-if="info">
        <n-alert v-if="showKeyHint" type="success" class="mt-12" title="节点已创建">
          安装脚本内已包含该节点的专属密钥；完整密钥仅在此处展示一次，之后可在“更多 → 查看密钥”中再次查看。
        </n-alert>
        <p class="mt-12 text-14">在目标服务器以管理员身份执行以下命令：</p>
        <div class="mt-8 flex items-center">
          <n-input :value="info.oneLiner" readonly class="flex-1 font-mono" />
          <n-button class="ml-8" type="primary" @click="copy(info.oneLiner)">复制</n-button>
        </div>
        <p class="mt-8 text-12 opacity-60">
          令牌有效期至 {{ formatDateTime(info.expiresAt) }} · 脚本包含该节点专属密钥，请勿泄露
          <n-button text type="primary" size="tiny" class="ml-8" @click="load(os, true)">重新生成令牌</n-button>
        </p>
        <p v-if="os === 'linux'" class="mt-8 text-12 opacity-60">
          经代理安装：<code>{{ info.oneLiner }} -s -- --proxy socks5://10.0.0.1:1080</code>
        </p>
        <p class="mt-4 text-12 opacity-60">卸载：<code>{{ info.uninstallOneLiner }}</code></p>
        <n-collapse class="mt-12">
          <n-collapse-item title="查看完整脚本" name="script">
            <n-scrollbar style="max-height: 320px">
              <pre class="whitespace-pre text-12 font-mono leading-18">{{ info.script }}</pre>
            </n-scrollbar>
          </n-collapse-item>
        </n-collapse>
      </template>
    </n-spin>
  </MeModal>
</template>

<script setup>
import { useClipboard } from '@vueuse/core'
import { MeModal } from '@/components'
import { formatDateTime } from '@/utils/format'
import api from '@/views/nodes/api'

const modalRef = ref(null)
const loading = ref(false)
const info = ref(null)
const os = ref('linux')
const nodeId = ref(null)
const showKeyHint = ref(false)
const { copy: doCopy } = useClipboard()

async function load(target = os.value, renew = false) {
  if (!nodeId.value)
    return
  loading.value = true
  try {
    const { data } = await api.installScript(nodeId.value, { os: target, renew })
    info.value = data
    if (renew)
      $message.success('令牌已重新生成')
  }
  catch (e) {
    console.error(e)
  }
  loading.value = false
}

async function copy(text) {
  await doCopy(text)
  $message.success('已复制到剪贴板')
}

function open(id, { created = false } = {}) {
  nodeId.value = id
  info.value = null
  showKeyHint.value = created
  os.value = 'linux'
  modalRef.value?.open()
  load('linux')
}

defineExpose({ open })
</script>
