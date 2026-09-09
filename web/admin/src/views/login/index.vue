<template>
  <div class="wh-full flex-col bg-gradient-to-br from-#1c2a4a via-#2F6FED to-#7fb2ff">
    <div class="m-auto max-w-760 min-w-345 f-c-c rounded-12 bg-white/95 p-12 card-shadow dark:bg-#1f1f23/95">
      <div class="hidden w-360 flex-col justify-center px-28 py-40 md:flex">
        <img src="@/assets/images/logo.svg" class="h-56 w-56" alt="logo">
        <h1 class="mt-20 text-26 font-bold">Server Node Monitor</h1>
        <p class="mt-8 text-15 opacity-70">服务器节点监控 · 资产台账 · 智能告警</p>
        <ul class="mt-24 text-13 leading-24 opacity-60">
          <li>· 2 秒级实时性能看板</li>
          <li>· 流量 Delta 精准计费</li>
          <li>· Telegram / Webhook 告警</li>
        </ul>
      </div>

      <div class="w-320 flex-col px-20 py-32">
        <h2 class="f-c-c text-22 font-normal">
          <img src="@/assets/images/logo.svg" class="mr-12 h-36 md:hidden" alt="logo">
          登录
        </h2>
        <n-input
          v-model:value="loginInfo.username"
          autofocus
          class="mt-32 h-40 items-center"
          placeholder="用户名"
          :maxlength="32"
          @keydown.enter="handleLogin()"
        >
          <template #prefix>
            <i class="i-fe:user mr-12 opacity-20" />
          </template>
        </n-input>
        <n-input
          v-model:value="loginInfo.password"
          class="mt-20 h-40 items-center"
          type="password"
          show-password-on="mousedown"
          placeholder="密码"
          :maxlength="64"
          @keydown.enter="handleLogin()"
        >
          <template #prefix>
            <i class="i-fe:lock mr-12 opacity-20" />
          </template>
        </n-input>

        <n-checkbox
          class="mt-20"
          :checked="isRemember"
          label="记住用户名"
          :on-update:checked="(val) => (isRemember = val)"
        />

        <n-button
          class="mt-20 h-40 w-full rounded-5 text-16"
          type="primary"
          :loading="loading"
          @click="handleLogin()"
        >
          登录
        </n-button>
      </div>
    </div>

    <TheFooter class="py-12 text-white/70" />
  </div>
</template>

<script setup>
import { useStorage } from '@vueuse/core'
import { useAuthStore } from '@/store'
import { lStorage } from '@/utils'
import api from './api'

const authStore = useAuthStore()
const router = useRouter()
const route = useRoute()

const loginInfo = ref({ username: '', password: '' })
const localLoginInfo = lStorage.get('loginInfo')
if (localLoginInfo?.username)
  loginInfo.value.username = localLoginInfo.username

const isRemember = useStorage('isRemember', true)
const loading = ref(false)

async function handleLogin() {
  const { username, password } = loginInfo.value
  if (!username || !password)
    return $message.warning('请输入用户名和密码')
  try {
    loading.value = true
    const { data } = await api.login({ username, password: password.toString() })
    if (isRemember.value)
      lStorage.set('loginInfo', { username })
    else
      lStorage.remove('loginInfo')
    authStore.setToken(data)
    $message.success('登录成功')
    if (route.query.redirect) {
      const path = route.query.redirect
      const query = { ...route.query }
      delete query.redirect
      router.push({ path, query })
    }
    else {
      router.push('/')
    }
  }
  catch (error) {
    console.error(error)
  }
  loading.value = false
}
</script>
