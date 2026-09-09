<template>
  <div class="flex items-center">
    <n-tooltip :disabled="!cc">
      <template #trigger>
        <span class="mr-8 text-20 leading-none">{{ flagEmoji(cc) }}</span>
      </template>
      {{ countryName(cc) }} ({{ cc }})
    </n-tooltip>
    <div class="min-w-0 flex-col">
      <span class="truncate text-14 font-medium">{{ name }}</span>
      <span v-if="remark" class="truncate text-12 opacity-50">{{ remark }}</span>
    </div>
  </div>
</template>

<script setup>
import { countryName, flagEmoji } from '@/utils/format'

defineProps({
  cc: { type: String, default: '' },
  name: { type: String, default: '' },
  remark: { type: String, default: '' },
})
</script>
