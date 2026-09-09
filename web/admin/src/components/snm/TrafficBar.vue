<template>
  <div v-if="limit > 0" class="flex-col" :style="{ width: `${width}px` }">
    <div class="flex justify-between text-12 tabular-nums">
      <span>{{ formatBytes(used) }} / {{ formatBytes(limit, 0) }}</span>
      <span>{{ pct.toFixed(0) }}%</span>
    </div>
    <div class="mt-4 h-6 overflow-hidden rounded-3 bg-gray-200 dark:bg-gray-700">
      <div class="h-full rounded-3" :style="{ width: `${Math.min(100, pct)}%`, background: percentColor(pct) }" />
    </div>
  </div>
  <span v-else class="text-12 opacity-60">{{ formatBytes(used) }} · 不限</span>
</template>

<script setup>
import { formatBytes, percentColor } from '@/utils/format'

const props = defineProps({
  used: { type: Number, default: 0 },
  limit: { type: Number, default: 0 },
  width: { type: Number, default: 170 },
})
const pct = computed(() => props.limit > 0 ? (props.used * 100) / props.limit : 0)
</script>
