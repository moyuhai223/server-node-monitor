<script setup>
import { useClipboard, useStorage } from '@vueuse/core'
import {
  API_BASE_URL_OPTIONS,
  DEFAULT_SETTINGS,
  EXPERIENCE_KEY_MAX_TEXT_LENGTH,
  isExperienceApiKey,
  isSettingsReady,
  MODEL_OPTIONS,
  STORE_KEY,
  translateText,
} from './translate'

defineOptions({ name: 'DemoTranslate' })

const { copy, copied } = useClipboard()

const sourceText = ref('')
const resultText = ref('')
const errorText = ref('')
const loading = ref(false)
const showSettings = ref(false)
const runApiUrl = 'https://runapi.co/register?aff=Da0v'

const settings = useStorage(STORE_KEY, { ...DEFAULT_SETTINGS })
const draftSettings = reactive({ ...settings.value })

const canTranslate = computed(() => Boolean(sourceText.value.trim()) && !loading.value)
const apiReady = computed(() => isSettingsReady(settings.value))
const isUsingExperienceKey = computed(() => isExperienceApiKey(settings.value.apiKey))
const isDraftExperienceKey = computed(() => isExperienceApiKey(draftSettings.apiKey))
const sourceLength = computed(() => sourceText.value.length)
const sourceLimitText = computed(() => `${sourceLength.value}/${EXPERIENCE_KEY_MAX_TEXT_LENGTH}`)
const footerText = computed(() => {
  if (!apiReady.value)
    return '请先完善模型配置'
  if (isUsingExperienceKey.value)
    return '当前是体验 Key，所有人每天共享使用约 1000 次翻译'
  return '已就绪'
})

watch(sourceText, (value) => {
  if (isUsingExperienceKey.value && value.length > EXPERIENCE_KEY_MAX_TEXT_LENGTH)
    sourceText.value = value.slice(0, EXPERIENCE_KEY_MAX_TEXT_LENGTH)
})

watch(copied, (value) => {
  if (value)
    $message.success('已复制译文')
})

onMounted(() => {
  if (!apiReady.value)
    showSettings.value = true
})

function syncDraftSettings() {
  Object.assign(draftSettings, settings.value)
}

function openSettings() {
  syncDraftSettings()
  showSettings.value = true
}

function saveSettings() {
  if (!isSettingsReady(draftSettings))
    return

  settings.value = {
    apiKey: draftSettings.apiKey.trim(),
    apiBaseUrl: draftSettings.apiBaseUrl.trim().replace(/\/+$/, ''),
    model: draftSettings.model.trim(),
  }
  showSettings.value = false
  errorText.value = ''
  $message.success('配置已保存')
}

function selectModel(model) {
  draftSettings.model = model
}

async function handleTranslate() {
  const text = sourceText.value.trim()
  if (!text || loading.value)
    return

  resultText.value = ''
  errorText.value = ''

  if (!apiReady.value) {
    syncDraftSettings()
    showSettings.value = true
    errorText.value = '请先保存模型配置'
    return
  }

  loading.value = true
  try {
    resultText.value = await translateText(text, settings.value)
  }
  catch (error) {
    errorText.value = error?.message || '翻译失败'
  }
  finally {
    loading.value = false
  }
}

async function handleCopy() {
  if (!resultText.value)
    return
  await copy(resultText.value)
}
</script>

<template>
  <CommonPage>
    <div class="translate-page">
      <div class="translate-toolbar">
        <div class="min-w-0 flex items-center gap-12">
          <div class="tool-icon">
            <i class="i-fe:globe" />
          </div>
          <div class="min-w-0">
            <div class="flex items-center gap-10">
              <h3 class="m-0 text-18 font-600">
                AI 翻译
              </h3>
              <n-tag size="small" round :type="apiReady ? 'success' : 'warning'">
                {{ settings.model || '未配置模型' }}
              </n-tag>
            </div>
            <div class="mt-4 truncate text-13 text-#7a807f">
              {{ footerText }}
            </div>
          </div>
        </div>

        <div class="flex items-center gap-10">
          <a
            v-if="isUsingExperienceKey"
            class="runapi-badge"
            :href="runApiUrl"
            target="_blank"
            rel="noopener noreferrer"
            title="打开 RunAPI"
          >
            <i class="i-fe:zap" />
            <span>默认体验模型由中转站平台 RunAPI 提供支持</span>
            <i class="i-fe:external-link" />
          </a>
          <n-button quaternary circle title="模型配置" @click="openSettings">
            <template #icon>
              <i class="i-fe:settings" />
            </template>
          </n-button>
        </div>
      </div>

      <div class="translate-workspace">
        <section class="translate-panel">
          <div class="panel-head">
            <div>
              <div class="text-14 font-600">
                原文
              </div>
              <div class="mt-4 text-12 text-#9aa0a0">
                Enter 翻译
              </div>
            </div>
            <div class="flex items-center gap-10">
              <n-tag v-if="isUsingExperienceKey" size="small" :bordered="false">
                {{ sourceLimitText }}
              </n-tag>
            </div>
          </div>

          <div class="source-input-wrap">
            <n-input
              v-model:value="sourceText"
              class="translate-input"
              type="textarea"
              :maxlength="isUsingExperienceKey ? EXPERIENCE_KEY_MAX_TEXT_LENGTH : undefined"
              :placeholder="isUsingExperienceKey ? `请输入要翻译的内容（体验 Key 最多 ${EXPERIENCE_KEY_MAX_TEXT_LENGTH} 字符）` : '请输入要翻译的内容'"
              @keydown.enter.exact.prevent="handleTranslate"
            />
            <n-button
              class="translate-action"
              type="primary"
              :loading="loading"
              :disabled="!canTranslate"
              @click="handleTranslate"
            >
              <template #icon>
                <i class="i-fe:send" />
              </template>
              翻译
            </n-button>
          </div>
        </section>

        <section class="translate-panel">
          <div class="panel-head">
            <div>
              <div class="text-14 font-600">
                译文
              </div>
              <div class="mt-4 text-12 text-#9aa0a0">
                自动识别中英方向
              </div>
            </div>
            <n-button quaternary circle :disabled="!resultText" title="复制译文" @click="handleCopy">
              <template #icon>
                <i :class="copied ? 'i-fe:check' : 'i-fe:copy'" />
              </template>
            </n-button>
          </div>

          <div class="result-box">
            <div v-if="errorText" class="result-error">
              <i class="i-fe:alert-circle text-22" />
              <span>{{ errorText }}</span>
            </div>
            <span v-else-if="resultText" class="whitespace-pre-wrap break-words">{{ resultText }}</span>
            <div v-else class="result-empty">
              <i class="i-fe:message-square text-34" />
              <span>翻译结果会显示在这里</span>
            </div>
          </div>
        </section>
      </div>
    </div>

    <n-drawer v-model:show="showSettings" :width="420" placement="right">
      <n-drawer-content title="模型配置" closable>
        <div class="flex flex-col gap-16">
          <n-alert v-if="isDraftExperienceKey" type="warning" :show-icon="false">
            当前是体验 Key，所有人每天共享使用约 1000 次翻译，单次最多支持 {{ EXPERIENCE_KEY_MAX_TEXT_LENGTH }} 字符。
          </n-alert>

          <a class="runapi-card" :href="runApiUrl" target="_blank" rel="noopener noreferrer">
            <span class="min-w-0 flex-1">
              <strong>默认体验模型由中转站平台 RunAPI 提供支持</strong>
              <span>点击获取稳定、快速且实惠的模型 API 服务</span>
            </span>
            <i class="i-fe:external-link" />
          </a>

          <div>
            <div class="mb-8 text-13 text-#5f6665">
              API Key
            </div>
            <n-input
              v-model:value="draftSettings.apiKey"
              type="password"
              show-password-on="click"
              placeholder="sk-..."
            />
          </div>

          <div>
            <div class="mb-8 text-13 text-#5f6665">
              API Base URL
            </div>
            <n-auto-complete
              v-model:value="draftSettings.apiBaseUrl"
              :options="API_BASE_URL_OPTIONS.filter(item => item.includes(draftSettings.apiBaseUrl)).map(value => ({ label: value, value }))"
              :get-show="() => true"
              placeholder="例如：https://runapi.co/v1"
            />
          </div>

          <div>
            <div class="mb-8 text-13 text-#5f6665">
              Model
            </div>
            <n-auto-complete
              v-model:value="draftSettings.model"
              :options="MODEL_OPTIONS.filter(item => item.includes(draftSettings.model)).map(value => ({ label: value, value }))"
              :get-show="() => true"
              placeholder="例如：gpt-5.4-mini"
            />
            <div class="mt-10 flex flex-wrap gap-8">
              <n-tag
                v-for="model in MODEL_OPTIONS"
                :key="model"
                class="cursor-pointer"
                size="small"
                :type="draftSettings.model === model ? 'primary' : 'default'"
                round
                @click="selectModel(model)"
              >
                {{ model }}
              </n-tag>
            </div>
          </div>

          <div class="flex justify-end gap-10">
            <n-button @click="showSettings = false">
              取消
            </n-button>
            <n-button type="primary" :disabled="!isSettingsReady(draftSettings)" @click="saveSettings">
              保存配置
            </n-button>
          </div>
        </div>
      </n-drawer-content>
    </n-drawer>
  </CommonPage>
</template>

<style scoped>
.translate-page {
  height: 100%;
  min-height: 0;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  gap: 16px;
}

.translate-toolbar {
  min-height: 70px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 0 4px 16px;
  border-bottom: 1px solid rgba(239, 239, 245, 0.9);
}

.tool-icon {
  width: 42px;
  height: 42px;
  display: grid;
  place-items: center;
  flex: none;
  border-radius: 8px;
  color: rgb(var(--primary-color));
  background: rgba(var(--primary-color), 0.09);
  font-size: 20px;
}

.runapi-badge {
  height: 34px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 0 12px;
  border: 1px solid rgba(var(--primary-color), 0.22);
  border-radius: 8px;
  color: rgb(var(--primary-color));
  background: rgba(var(--primary-color), 0.08);
  text-decoration: none;
  font-size: 13px;
  font-weight: 600;
  white-space: nowrap;
  transition:
    background 0.2s ease,
    border-color 0.2s ease;
}

.runapi-badge:hover {
  border-color: rgba(var(--primary-color), 0.38);
  background: rgba(var(--primary-color), 0.13);
}

.runapi-card {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 14px;
  border: 1px solid rgba(var(--primary-color), 0.18);
  border-radius: 8px;
  color: #1f2322;
  background: linear-gradient(135deg, rgba(var(--primary-color), 0.1), rgba(255, 255, 255, 0.9));
  text-decoration: none;
}

.runapi-card strong,
.runapi-card span span {
  display: block;
}

.runapi-card strong {
  color: rgb(var(--primary-color));
  font-size: 14px;
}

.runapi-card span span {
  margin-top: 4px;
  color: #7a807f;
  font-size: 12px;
}

.runapi-card-icon {
  width: 34px;
  height: 34px;
  display: grid;
  place-items: center;
  flex: none;
  border-radius: 8px;
  color: rgb(var(--primary-color));
  background: rgba(var(--primary-color), 0.12);
}

.translate-workspace {
  min-height: 0;
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: 16px;
}

.translate-panel {
  min-height: 0;
  display: grid;
  grid-template-rows: auto minmax(0, 1fr);
  border: 1px solid #efeff5;
  border-radius: 8px;
  overflow: hidden;
  background: #fff;
}

.panel-head {
  min-height: 68px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 14px 16px;
  border-bottom: 1px solid #f1f1f4;
  background: #fbfcfc;
}

.translate-input {
  height: 100%;
}

.source-input-wrap {
  position: relative;
  min-height: 0;
}

.translate-input :deep(.n-input),
.translate-input :deep(.n-input-wrapper),
.translate-input :deep(.n-input__textarea),
.translate-input :deep(.n-input__textarea-el) {
  height: 100%;
  resize: none;
}

.translate-input :deep(textarea),
.translate-input :deep(.n-input__textarea-el) {
  resize: none !important;
}

.translate-input :deep(.n-input) {
  border-radius: 0;
}

.translate-input :deep(.n-input__textarea-el) {
  padding-bottom: 68px;
}

.translate-input :deep(.n-input__border),
.translate-input :deep(.n-input__state-border) {
  display: none;
}

.translate-action {
  position: absolute;
  right: 16px;
  bottom: 16px;
  z-index: 1;
}

.result-box {
  min-height: 0;
  overflow: auto;
  padding: 18px;
  color: #1f2322;
  font-size: 15px;
  line-height: 1.8;
  background: #fff;
}

.result-empty {
  height: 100%;
  min-height: 220px;
  display: flex;
  align-items: center;
  justify-content: center;
  flex-direction: column;
  gap: 10px;
  color: #a0a5a4;
}

.result-error {
  min-height: 220px;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 10px;
  color: #d03050;
  font-size: 14px;
}

@media (max-width: 960px) {
  .translate-toolbar {
    align-items: flex-start;
    flex-direction: column;
  }

  .translate-workspace {
    grid-template-columns: 1fr;
  }

  .runapi-badge {
    white-space: normal;
  }
}
</style>
