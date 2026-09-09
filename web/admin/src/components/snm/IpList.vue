<template>
  <span v-if="!ips.length" class="opacity-50">—</span>
  <n-popover v-else trigger="hover" placement="bottom-start">
    <template #trigger>
      <span class="cursor-pointer tabular-nums">
        {{ primary.address }}<n-tag v-if="ips.length > 1" size="tiny" class="ml-6" round>+{{ ips.length - 1 }}</n-tag>
      </span>
    </template>
    <div class="max-h-260 overflow-auto">
      <div v-for="ip in ips" :key="ip.address" class="flex items-center py-4 text-13">
        <n-tag size="tiny" :type="ip.isPublic ? 'info' : 'default'" class="mr-8 w-40 justify-center">{{ ip.isPublic ? '公网' : '内网' }}</n-tag>
        <span class="tabular-nums">{{ ip.address }}</span>
        <span class="ml-8 text-11 opacity-50">{{ sourceText(ip.source) }}</span>
        <n-button text size="tiny" class="ml-8" @click="copy(ip.address)">
          <i class="i-fe:copy" />
        </n-button>
      </div>
    </div>
  </n-popover>
</template>

<script setup>
import { useClipboard } from '@vueuse/core'

const props = defineProps({ ips: { type: Array, default: () => [] } })
const primary = computed(() => props.ips.find(ip => ip.isPublic && ip.family === 4) || props.ips.find(ip => ip.isPublic) || props.ips[0])
const { copy: doCopy } = useClipboard()
async function copy(text) {
  await doCopy(text)
  $message.success('已复制到剪贴板')
}
function sourceText(s) {
  return { 1: '探针', 2: '服务端', 3: '探针+服务端' }[s] || ''
}
</script>
