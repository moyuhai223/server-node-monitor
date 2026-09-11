<template>
  <div>
    <n-alert type="info" class="mb-12" :show-icon="false">
      当前大屏使用主题 <b>{{ activeTheme?.name || active }}</b>。切换或上传后大屏会自动重新加载;主题开发指南见仓库 <code>docs/THEMES.md</code>,打包命令 <code>scripts/pack-theme.sh</code>。
    </n-alert>
    <n-grid :cols="4" :x-gap="12" :y-gap="12" responsive="screen" item-responsive>
      <n-gi v-for="t in items" :key="t.id" span="4 s:2 l:1">
        <n-card size="small" :class="{ 'ring-2 ring-primary': t.active }" hoverable>
          <div class="h-140 flex items-center justify-center overflow-hidden rounded-6 bg-gray-100 dark:bg-gray-800">
            <img v-if="t.preview" :src="t.preview" class="h-full w-full object-cover" alt="">
            <i v-else class="i-fe:layout text-40 opacity-30" />
          </div>
          <div class="mt-10 flex items-center justify-between">
            <span class="font-medium">{{ t.name }}</span>
            <n-space :size="4">
              <n-tag v-if="t.active" size="tiny" type="success" :bordered="false">使用中</n-tag>
              <n-tag v-if="t.builtIn" size="tiny" :bordered="false">内置</n-tag>
              <n-tag size="tiny" :bordered="false">v{{ t.version }}</n-tag>
            </n-space>
          </div>
          <div class="mt-4 h-36 text-12 opacity-60 line-clamp-2">{{ t.description || '—' }}</div>
          <div class="mt-4 text-11 opacity-40">{{ t.id }}<span v-if="t.author"> · {{ t.author }}</span> · SDK {{ t.sdk }}</div>
          <div class="mt-10 flex gap-6">
            <n-button size="small" type="primary" :disabled="t.active" @click="activate(t)">{{ t.active ? '使用中' : '启用' }}</n-button>
            <n-button size="small" tag="a" :href="t.url" target="_blank">预览</n-button>
            <n-button v-if="!t.builtIn" size="small" type="error" quaternary @click="remove(t)">删除</n-button>
          </div>
        </n-card>
      </n-gi>
      <n-gi span="4 s:2 l:1">
        <n-upload accept=".zip" :show-file-list="false" :custom-request="upload" class="h-full">
          <n-upload-dragger class="h-full min-h-280 flex-col f-c-c">
            <i class="i-fe:upload-cloud text-36 opacity-40" />
            <div class="mt-8">上传主题包 (.zip)</div>
            <div class="mt-4 text-12 opacity-50">包内须含 theme.json;同 id 会覆盖旧版本</div>
            <n-spin v-if="uploading" size="small" class="mt-8" />
          </n-upload-dragger>
        </n-upload>
      </n-gi>
    </n-grid>

    <n-card title="主题参数" size="small" class="mt-16 max-w-760">
      <template #header-extra><span class="text-12 opacity-50">JSON 对象,由主题自行解释(默认主题支持 accent / columns / showClock / showFooter)</span></template>
      <n-input v-model:value="optionsText" type="textarea" :autosize="{ minRows: 3, maxRows: 10 }" placeholder='{ "accent": "#2f80ed", "columns": 340 }' class="font-mono" />
      <n-space class="mt-12">
        <n-button type="primary" :loading="saving" @click="saveOptions">保存参数</n-button>
        <n-button quaternary @click="optionsText = ''">清空</n-button>
      </n-space>
    </n-card>
  </div>
</template>

<script setup>
import { request } from '@/utils'

const items = ref([])
const active = ref('default')
const optionsText = ref('')
const uploading = ref(false)
const saving = ref(false)
const activeTheme = computed(() => items.value.find(t => t.active))

async function load() {
  const { data } = await request.get('/settings/themes')
  items.value = data.items
  active.value = data.active
  const s = await request.get('/settings')
  optionsText.value = s.data.site.themeOptions || ''
}
onMounted(load)

async function activate(t) {
  await request.patch('/settings', { site: { theme: t.id } })
  $message.success(`已切换到主题「${t.name}」`)
  load()
}
function remove(t) {
  $dialog.confirm({ title: '删除主题', type: 'warning', content: `确定删除主题「${t.name}」?正在使用时会回退到默认主题。`, async confirm() {
    await request.delete(`/settings/themes/${t.id}`)
    $message.success('已删除')
    load()
  } })
}
async function upload({ file, onFinish, onError }) {
  uploading.value = true
  try {
    const fd = new FormData()
    fd.append('file', file.file)
    const { data } = await request.post('/settings/themes', fd, { headers: { 'Content-Type': 'multipart/form-data' }, timeout: 120000 })
    $message.success(`主题「${data.name}」v${data.version} 已安装`)
    onFinish()
    load()
  }
  catch (e) {
    console.error(e)
    onError()
  }
  finally {
    uploading.value = false
  }
}
async function saveOptions() {
  const text = optionsText.value.trim()
  if (text) {
    try { JSON.parse(text) }
    catch { return $message.error('不是合法的 JSON') }
  }
  saving.value = true
  try {
    await request.patch('/settings', { site: { themeOptions: text } })
    $message.success('已保存,大屏会自动应用')
  }
  finally {
    saving.value = false
  }
}
</script>
