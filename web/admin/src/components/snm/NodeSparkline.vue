<template>
  <canvas ref="canvasRef" class="block w-full" :style="{ height: `${height}px` }" />
</template>

<script setup>
import { useAppStore } from '@/store'
import { drawSparkline } from '@/utils/sparkline'

const props = defineProps({
  hist: { type: Object, default: null },
  height: { type: Number, default: 64 },
})
const appStore = useAppStore()
const canvasRef = ref(null)
let raf = 0
function draw() {
  if (!canvasRef.value)
    return
  cancelAnimationFrame(raf)
  raf = requestAnimationFrame(() => drawSparkline(canvasRef.value, props.hist || { cpu: [], rx: [], tx: [] }, { dark: appStore.isDark }))
}
watch(() => [props.hist, appStore.isDark], draw, { deep: true })
onMounted(() => {
  draw()
  window.addEventListener('resize', draw)
})
onUnmounted(() => window.removeEventListener('resize', draw))
</script>
