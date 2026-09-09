<template>
  <footer class="f-c-c text-14 text-gray-500">
    <p>Server Node Monitor<span v-if="systemStore.info?.version"> · 版本 {{ systemStore.info.version }}</span></p>
  </footer>
</template>

<script setup>
import { useSystemStore } from '@/store'

const systemStore = useSystemStore()
</script>
