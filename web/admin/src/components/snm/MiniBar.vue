<template>
  <div class="flex items-center" :style="{ width: `${width}px` }">
    <div class="h-6 flex-1 overflow-hidden rounded-3 bg-gray-200 dark:bg-gray-700">
      <div class="h-full rounded-3 transition-all-300" :style="{ width: `${pct}%`, background: percentColor(pct) }" />
    </div>
    <span class="ml-8 w-44 flex-shrink-0 text-right text-12 tabular-nums">{{ pct.toFixed(1) }}%</span>
  </div>
</template>

<script setup>
import { percentColor } from '@/utils/format'

const props = defineProps({
  value: { type: Number, default: 0 },
  width: { type: Number, default: 110 },
})
const pct = computed(() => Math.min(100, Math.max(0, Number(props.value) || 0)))
</script>
