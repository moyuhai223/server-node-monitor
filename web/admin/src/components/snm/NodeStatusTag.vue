<template>
  <n-tag :type="type" size="small" round :bordered="false">
    <span class="mr-4 inline-block h-6 w-6 rounded-full" :class="dot" />{{ label }}
  </n-tag>
</template>

<script setup>
import { STATUS } from '@/constants/snm'

const props = defineProps({ status: { type: Number, default: 0 } })
const label = computed(() => STATUS[props.status] || '未知')
const type = computed(() => ({ 1: 'success', 2: 'error' })[props.status] || 'default')
const dot = computed(() => ({ 1: 'bg-#18a058', 2: 'bg-#d03050' })[props.status] || 'bg-gray-400')
</script>
