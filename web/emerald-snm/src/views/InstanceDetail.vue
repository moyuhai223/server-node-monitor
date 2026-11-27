<script setup lang="ts">
import { Icon } from '@iconify/vue'
import { computed, defineAsyncComponent, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { CardX } from '@/components/ui/card-x'
import { Empty } from '@/components/ui/empty'
import { useBackgroundSurface } from '@/composables/useBackgroundSurface'
import { useNodeFormatters } from '@/composables/useNodeFormatters'
import { useAppStore } from '@/stores/app'
import { useNodesStore } from '@/stores/nodes'
import { formatDateTime } from '@/utils/helper'
import { message } from '@/utils/message'
import { formatOfflineTime, getTrafficUsed, getMemPercentage, getDiskPercentage } from '@/utils/nodeHelpers'
import { getOSImage, getOSName } from '@/utils/osImageHelper'
import { getFlagSrc, getRegionDisplayName } from '@/utils/regionHelper'

const LoadChart = defineAsyncComponent(() => import('@/components/LoadChart.vue'))
const PingChart = defineAsyncComponent(() => import('@/components/PingChart.vue'))

const route = useRoute()
const router = useRouter()

const appStore = useAppStore()
const { pickSurfaceClass } = useBackgroundSurface()
const nodesStore = useNodesStore()
const { formatBytes, formatBytesPerSecond, formatUptime } = useNodeFormatters()
onMounted(() => { window.scrollTo({ top: 0, behavior: 'instant' }) })

const data = computed(() => nodesStore.nodes.find(node => node.uuid === route.params.id))

interface InfoItem {
  label: string
  value: string | undefined
  icon?: string
}

interface MetricCard {
  label: string
  value: string
  unit?: string
  icon: string
  valueClass?: string
}

const metricCards = computed<MetricCard[]>(() => data.value ? [
  {label:'CPU',value:data.value.cpu.toFixed(1),unit:'%',icon:'icon-park-outline:cpu'},
  {label:'内存',value:getMemPercentage(data.value).toFixed(1),unit:'%',icon:'icon-park-outline:memory'},
  {label:'硬盘',value:getDiskPercentage(data.value).toFixed(1),unit:'%',icon:'icon-park-outline:hard-disk'},
  {label:'运行时间',value:formatUptime(data.value.uptime, 'minute'),icon:'icon-park-outline:timer'},
] : [])

const hardwareInfo = computed<InfoItem[]>(() => [
 ...(appStore.publicSettings?.theme_settings?.snmShowSpecs === false ? [] : [{label:'CPU 核心',value:data.value?.cpu_cores ? String(data.value.cpu_cores) + ' vCPU' : '未公开',icon:'icon-park-outline:cpu'}]),
 {label:'国家 / 地区',value:getRegionDisplayName(data.value?.region || ''),icon:'tabler:world'},
])
const systemInfo = computed<InfoItem[]>(() => [
 {label:'运行时间',value:formatUptime(data.value?.uptime ?? 0,'minute'),icon:'icon-park-outline:timer'},
 {label:'最后上报',value:data.value?.time ? formatDateTime(data.value.time) : '等待上报',icon:'icon-park-outline:time'},
])
const storageInfo = computed<InfoItem[]>(() => [
 {label:'内存',value:formatBytes(data.value?.mem_total ?? 0),icon:'icon-park-outline:memory'},
 ...(data.value?.swap != null && data.value.swap_total != null
   ? [{label:'内存交换',value:`${formatBytes(data.value.swap)} / ${formatBytes(data.value.swap_total)}`,icon:'icon-park-outline:switch'}]
   : []),
 {label:'硬盘',value:formatBytes(data.value?.disk_total ?? 0),icon:'icon-park-outline:hard-disk'},
])

const trafficUsed = computed(() => {
  if (!data.value)
    return 0
  return getTrafficUsed(data.value)
})

const hasTrafficLimit = computed(() => (data.value?.traffic_limit ?? 0) > 0)

const trafficUsedPercentage = computed(() => {
  const trafficLimit = data.value?.traffic_limit ?? 0
  if (trafficLimit <= 0)
    return 0

  return Math.min((trafficUsed.value / trafficLimit) * 100, 100)
})

const trafficUsageText = computed(() => {
  if (!hasTrafficLimit.value)
    return `已用 ${formatBytes(trafficUsed.value)}（未设置限额）`

  return `${formatBytes(trafficUsed.value)} / ${formatBytes(data.value?.traffic_limit ?? 0)}`
})

async function copyText(text?: string, label: string = '内容') {
  if (!text)
    return
  try {
    await navigator.clipboard.writeText(text)
    message.success(`${label}已复制到剪贴板`)
  }
  catch {
    message.error('复制失败')
  }
}

const trafficProgressStyle = computed(() => ({
  width: `${trafficUsedPercentage.value}%`,
}))
</script>

<template>
  <div class="instance-detail space-y-4">
    <div v-if="!data" class="p-4">
      <CardX
        class="border-none transition-all rounded-md"
        :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
      >
        <Empty description="节点不存在或已被删除">
          <template #extra>
            <Button @click="router.push('/')">
              返回首页
            </Button>
          </template>
        </Empty>
      </CardX>
    </div>

    <template v-else>
      <div class="px-4 flex gap-4 items-center">
        <Button variant="ghost" size="icon-sm" class="bg-background/50 hover:bg-background" @click="router.push('/')">
          <Icon icon="tabler:arrow-left" :width="16" :height="16" />
        </Button>
        <div class="text-lg font-bold flex gap-2 items-center">
          <img
            :src="getFlagSrc(data.region)" :alt="getRegionDisplayName(data.region)"
            class="size-6"
          >
          <span>{{ data.name }}</span>
        </div>
        <Badge :variant="data.online ? 'default' : 'destructive'" class="text-xs !rounded">
          {{ data.online ? '在线' : formatOfflineTime(data) }}
        </Badge>
      </div>

      <div v-if="data.public_remark" class="px-4 text-sm text-muted-foreground -mt-2">
        {{ data.public_remark }}
      </div>

      <div class="px-4 grid grid-cols-2 gap-4 lg:grid-cols-4">
        <CardX
          v-for="item in metricCards" :key="item.label" hoverable size="small"
          class="group h-full border-none transition-all rounded-md"
          :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
          content-class="h-full !p-3"
        >
          <div class="flex h-full min-h-10 md:min-h-18 flex-col justify-between gap-3">
            <div class="flex items-center justify-between gap-2">
              <span class="text-xs font-medium tracking-wider text-muted-foreground">{{ item.label }}</span>
              <Icon
                :icon="item.icon" :width="20" :height="20"
                class="text-slate-500/25 transition-colors group-hover:text-slate-500"
              />
            </div>
            <div class="min-w-0 space-y-1">
              <div
                class="flex min-w-0 items-baseline gap-1 truncate font-semibold leading-none"
                :class="item.valueClass"
              >
                <span class="truncate text-base sm:text-2xl">{{ item.value }}</span>
                <span v-if="item.unit" class="shrink-0 text-[11px] font-medium text-muted-foreground sm:text-xs">
                  {{ item.unit }}
                </span>
              </div>
            </div>
          </div>
        </CardX>
      </div>

      <div class="px-4 gap-4 grid grid-cols-1 lg:grid-cols-2">
        <CardX
          :title="appStore.publicSettings?.theme_settings?.snmShowSpecs === false ? '地区信息' : '硬件信息'" size="small"
          class="group h-full border-none transition-all rounded-md"
          :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
        >
          <div class="gap-3 grid grid-cols-3">
            <div
              v-for="(item, index) in hardwareInfo" :key="item.label"
              class="min-w-0 flex flex-col gap-1 rounded-sm bg-slate-500/5 p-2" :class="!index && 'col-span-3'"
            >
              <div class="flex gap-1 items-center text-muted-foreground">
                <Icon v-if="item.icon" :icon="item.icon" :width="14" :height="14" />
                <span class="text-xs sm:text-sm">{{ item.label }}</span>
              </div>
              <span class="text-xs sm:text-sm break-all">{{ item.value }}</span>
            </div>
          </div>
        </CardX>

        <CardX
          title="系统信息" size="small"
          class="group h-full border-none transition-all rounded-md"
          :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
        >
          <div class="gap-3 grid grid-cols-1 sm:grid-cols-2">
            <div
              v-for="(item, index) in systemInfo" :key="item.label"
              class="min-w-0 flex flex-col gap-1 rounded-sm bg-slate-500/5 p-2"
              :class="!index && 'sm:col-span-2'"
            >
              <div class="flex gap-1 items-center text-muted-foreground">
                <Icon v-if="item.icon" :icon="item.icon" :width="14" :height="14" />
                <span class="text-xs sm:text-sm">{{ item.label }}</span>
              </div>
              <div class="flex min-w-0 gap-2 items-center">
                <img
                  v-if="item.label === '操作系统'" :src="getOSImage(data.os)" :alt="getOSName(data.os)"
                  class="size-5 shrink-0"
                >
                <span class="text-xs sm:text-sm break-all">
                  {{ item.value }}
                </span>
              </div>
            </div>
          </div>
        </CardX>

        <CardX
          v-if="appStore.publicSettings?.theme_settings?.snmShowSpecs !== false" title="存储信息" size="small"
          class="group h-full border-none transition-all rounded-md"
          :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
        >
          <div class="gap-3 grid grid-cols-3">
            <div
              v-for="item in storageInfo" :key="item.label"
              class="min-w-0 flex flex-col gap-1 rounded-sm bg-slate-500/5 p-2"
            >
              <div class="flex gap-1 items-center text-muted-foreground">
                <Icon v-if="item.icon" :icon="item.icon" :width="14" :height="14" />
                <span class="text-xs sm:text-sm">{{ item.label }}</span>
              </div>
              <span class="text-xs sm:text-sm break-all">{{ item.value }}</span>
            </div>
          </div>
        </CardX>

        <CardX
          title="网络信息" size="small"
          class="group h-full border-none transition-all rounded-md"
          :class="pickSurfaceClass('bg-background/60 hover:bg-background', 'bg-background/50 hover:bg-background backdrop-blur-xs')"
          content-class="pt-0"
        >
          <div class="gap-3 grid grid-cols-1 sm:grid-cols-2">
            <!-- 总流量 -->
            <div v-if="appStore.publicSettings?.theme_settings?.snmShowTraffic !== false" class="relative min-w-0 overflow-hidden rounded-sm bg-slate-500/5 p-2">
              <div
                v-if="hasTrafficLimit"
                class="absolute inset-y-0 left-0 rounded-sm bg-primary/10 pointer-events-none transition-[width] duration-300 ease-out"
                :style="trafficProgressStyle"
              />
              <div class="relative flex flex-col gap-1.5">
                <div class="flex gap-1 items-center text-muted-foreground">
                  <Icon icon="icon-park-outline:transfer-data" :width="14" :height="14" />
                  <span class="text-xs sm:text-sm">总流量</span>
                  <div class="flex-1" />
                </div>
                <span class="text-xs sm:text-sm font-medium break-all">
                  {{ trafficUsageText }}
                </span>
              </div>
            </div>

            <!-- 网络速率 -->
            <div class="min-w-0 flex flex-col gap-1 rounded-sm bg-slate-500/5 p-2 justify-between">
              <div class="flex gap-1 items-center text-muted-foreground">
                <Icon icon="icon-park-outline:dashboard-one" :width="14" :height="14" />
                <span class="text-xs sm:text-sm">网络速率</span>
              </div>
              <span class="text-xs sm:text-sm font-medium break-all flex flex-row flex-wrap items-center gap-1.5">
                <span class="inline-flex items-center gap-0.5 text-emerald-600 dark:text-emerald-400">
                  <Icon icon="tabler:chevron-up" width="13" height="13" />
                  {{ formatBytesPerSecond(data?.net_out ?? 0) }}
                </span>
                <span class="text-muted-foreground/40">·</span>
                <span class="inline-flex items-center gap-0.5 text-sky-600 dark:text-sky-400">
                  <Icon icon="tabler:chevron-down" width="13" height="13" />
                  {{ formatBytesPerSecond(data?.net_in ?? 0) }}
                </span>
              </span>
            </div>
          </div>
        </CardX>
      </div>

      <LoadChart :uuid="data.uuid" class="px-4" />
      <PingChart :uuid="data.uuid" class="px-4" />
    </template>
  </div>
</template>
