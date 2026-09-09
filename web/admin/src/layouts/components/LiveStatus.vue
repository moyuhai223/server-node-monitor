<template>
  <n-tooltip>
    <template #trigger>
      <div class="flex cursor-pointer items-center text-13" @click="liveStore.reconnect()">
        <span class="mr-6 inline-block h-8 w-8 rounded-full" :class="dotClass" />
        <span class="opacity-70">{{ text }}</span>
      </div>
    </template>
    实时连接状态，点击重连
  </n-tooltip>
</template>

<script setup>
import { useLiveStore } from '@/store'

const liveStore = useLiveStore()
const text = computed(() => ({
  connected: '实时已连接',
  connecting: '连接中…',
  reconnecting: '重连中…',
  disconnected: '未连接',
  idle: '未连接',
})[liveStore.state] || '未连接')
const dotClass = computed(() => ({
  connected: 'bg-#18a058',
  connecting: 'bg-#f0a020 animate-pulse',
  reconnecting: 'bg-#f0a020 animate-pulse',
})[liveStore.state] || 'bg-gray-400')
</script>
