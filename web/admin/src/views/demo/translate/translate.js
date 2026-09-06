export const STORE_KEY = 'demo-translate-settings'

export const API_BASE_URL_OPTIONS = [
  'https://runapi.co/v1',
]

export const MODEL_OPTIONS = [
  'gpt-5.4-mini',
  'gpt-5.2',
  'gpt-5.4',
  'gpt-5.5',
  'deepseek-v4-flash',
  'deepseek-v4-pro',
]

export const DEFAULT_SETTINGS = {
  apiKey: 'sk-17UVTOjYEB3M9Erg32yGdEhMVFKEaALuIkjy1CkGrCUdiQqJ',
  apiBaseUrl: API_BASE_URL_OPTIONS[0],
  model: MODEL_OPTIONS[0],
}

export const EXPERIENCE_KEY_MAX_TEXT_LENGTH = 100

const RUNAPI_PROXY_BASE_URL = '/runapi'

const TRANSLATION_RULES = 'Translate directly. Return only the translation. Preserve formatting, markdown, code, URLs, numbers, placeholders, and line breaks.'

export function isSettingsReady(target) {
  return Boolean(target.apiKey?.trim() && target.apiBaseUrl?.trim() && target.model?.trim())
}

export function isExperienceApiKey(apiKey) {
  return apiKey?.trim() === DEFAULT_SETTINGS.apiKey
}

export function validateTranslateText(text, settings) {
  if (isExperienceApiKey(settings.apiKey) && text.length > EXPERIENCE_KEY_MAX_TEXT_LENGTH)
    throw new Error(`体验 Key 最多支持 ${EXPERIENCE_KEY_MAX_TEXT_LENGTH} 字符`)
}

function countMatches(text, pattern) {
  return text.match(pattern)?.length || 0
}

function detectSourceLanguage(text) {
  const cjkCount = countMatches(text, /[\u3400-\u9FFF]/g)
  const latinCount = countMatches(text, /[A-Z]/gi)

  if (!cjkCount && !latinCount)
    return 'auto'
  if (!cjkCount)
    return 'en'
  if (!latinCount)
    return 'zh'

  const cjkRatio = cjkCount / (cjkCount + latinCount)
  if (cjkRatio >= 0.2)
    return 'zh'
  if (cjkRatio <= 0.08)
    return 'en'

  const firstScript = text.match(/[\u3400-\u9FFFA-Z]/i)?.[0]
  return firstScript && /[\u3400-\u9FFF]/.test(firstScript) ? 'zh' : 'en'
}

function createSystemPrompt(sourceLanguage) {
  if (sourceLanguage === 'zh')
    return `${TRANSLATION_RULES} Target: natural English.`
  if (sourceLanguage === 'en')
    return `${TRANSLATION_RULES} Target: natural Simplified Chinese.`
  return `${TRANSLATION_RULES} Target: Simplified Chinese.`
}

function createFastModelOptions(model) {
  const normalized = model.trim().toLowerCase()
  if (!normalized.startsWith('gpt-5'))
    return {}

  return {
    reasoning_effort: /^gpt-5\.[1-9]\d*/.test(normalized) ? 'none' : 'low',
    verbosity: 'low',
  }
}

function createRequestBaseUrl(apiBaseUrl) {
  const baseUrl = apiBaseUrl.trim().replace(/\/+$/, '')
  if (baseUrl === API_BASE_URL_OPTIONS[0])
    return RUNAPI_PROXY_BASE_URL
  return baseUrl
}

export async function translateText(text, settings) {
  validateTranslateText(text, settings)

  const baseUrl = createRequestBaseUrl(settings.apiBaseUrl)
  const model = settings.model.trim() || DEFAULT_SETTINGS.model
  const sourceLanguage = detectSourceLanguage(text)

  const response = await fetch(`${baseUrl}/chat/completions`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${settings.apiKey.trim()}`,
    },
    body: JSON.stringify({
      model,
      temperature: 0,
      ...createFastModelOptions(model),
      messages: [
        {
          role: 'system',
          content: createSystemPrompt(sourceLanguage),
        },
        {
          role: 'user',
          content: text,
        },
      ],
    }),
  })

  const data = await response.json().catch(() => ({}))
  if (!response.ok)
    throw new Error(data?.error?.message || `请求失败：${response.status}`)

  const result = data?.choices?.[0]?.message?.content?.trim() || ''
  if (!result)
    throw new Error('未收到翻译结果')

  return result
}
