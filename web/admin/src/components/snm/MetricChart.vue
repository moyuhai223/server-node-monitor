<template>
  <div :style="{ height: `${height}px` }">
    <n-empty v-if="!points.length" class="h-full justify-center" description="该时间范围内暂无数据（需运行 ≥ 1 分钟）" />
    <VChart v-else :option="option" :theme="appStore.isDark ? 'dark' : undefined" autoresize />
  </div>
</template>

<script setup>
import { useAppStore } from '@/store'
import { VChart } from '@/utils/echarts'
import { formatBps, formatBytes } from '@/utils/format'

const props = defineProps({
  metric: { type: String, default: 'cpu' },   // cpu | mem | net | traffic | load | disk
  points: { type: Array, default: () => [] },
  range: { type: String, default: '24h' },
  memTotalMb: { type: Number, default: 0 },
  cpuCores: { type: Number, default: 0 },
  height: { type: Number, default: 320 },
})
const appStore = useAppStore()

const option = computed(() => {
  const pts = props.points
  const x = pts.map(p => p.ts * 1000)
  const base = {
    backgroundColor: 'transparent',
    tooltip: { trigger: 'axis' },
    legend: { top: 0 },
    grid: { left: 60, right: 24, top: 36, bottom: props.range === '24h' ? 60 : 36 },
    xAxis: { type: 'time' },
    dataZoom: props.range === '24h' ? [{ type: 'inside' }, { type: 'slider', height: 18, bottom: 8 }] : [{ type: 'inside' }],
  }
  const series = (name, key, extra = {}) => ({ name, type: 'line', showSymbol: false, connectNulls: false, data: pts.map((p, i) => [x[i], key(p)]), ...extra })
  switch (props.metric) {
    case 'cpu':
      return { ...base, yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' } }, tooltip: { trigger: 'axis', valueFormatter: v => `${Number(v).toFixed(1)}%` },
        series: [series('平均', p => +(p.cpuAvg / 10).toFixed(1), { areaStyle: { opacity: 0.15 } }), series('峰值', p => +(p.cpuMax / 10).toFixed(1), { lineStyle: { type: 'dashed' } })] }
    case 'mem':
      return { ...base, yAxis: { type: 'value', min: 0, max: props.memTotalMb ? +(props.memTotalMb / 1024).toFixed(1) : undefined, axisLabel: { formatter: '{value} GB' } }, tooltip: { trigger: 'axis', valueFormatter: v => `${Number(v).toFixed(2)} GB` },
        series: [series('已用（均值）', p => +(p.memUsedAvgMb / 1024).toFixed(2), { areaStyle: { opacity: 0.15 } }), series('峰值', p => +(p.memUsedMaxMb / 1024).toFixed(2), { lineStyle: { type: 'dashed' } })] }
    case 'net':
      return { ...base, yAxis: { type: 'value', min: 0, axisLabel: { formatter: v => formatBps(v) } }, tooltip: { trigger: 'axis', valueFormatter: v => formatBps(v) },
        series: [series('下行', p => p.rxBpsAvg), series('上行', p => p.txBpsAvg)] }
    case 'traffic':
      return { ...base, yAxis: { type: 'value', min: 0, axisLabel: { formatter: v => formatBytes(v, 0) } }, tooltip: { trigger: 'axis', valueFormatter: v => formatBytes(v) },
        series: [{ name: '下行', type: 'bar', stack: 't', data: pts.map((p, i) => [x[i], p.rxBytes]) }, { name: '上行', type: 'bar', stack: 't', data: pts.map((p, i) => [x[i], p.txBytes]) }] }
    case 'load':
      return { ...base, yAxis: { type: 'value', min: 0 }, tooltip: { trigger: 'axis', valueFormatter: v => Number(v).toFixed(2) },
        series: [series('1 分钟负载', p => +(p.load1Avg / 100).toFixed(2), { markLine: props.cpuCores ? { silent: true, data: [{ yAxis: props.cpuCores, name: '核心数' }] } : undefined }), series('峰值', p => +(p.load1Max / 100).toFixed(2), { lineStyle: { type: 'dashed' } })] }
    case 'disk':
      return { ...base, yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%' } }, tooltip: { trigger: 'axis', valueFormatter: v => `${Number(v).toFixed(1)}%` },
        series: [series('磁盘使用率', p => p.diskTotalMb ? +((p.diskUsedMb * 100) / p.diskTotalMb).toFixed(1) : null, { areaStyle: { opacity: 0.15 } })] }
    default:
      return base
  }
})
</script>
