<template>
  <n-dropdown :options="options" @select="handleSelect">
    <div id="user-dropdown" class="flex cursor-pointer items-center">
      <n-avatar round :size="36" :src="userStore.avatar" />
      <div v-if="userStore.userInfo" class="ml-12 flex-col flex-shrink-0 items-center">
        <span class="text-14">{{ userStore.nickName ?? userStore.username }}</span>
        <span class="text-12 opacity-50">管理员</span>
      </div>
    </div>
  </n-dropdown>
</template>

<script setup>
import { useAuthStore, usePermissionStore, useUserStore } from '@/store'

const router = useRouter()
const userStore = useUserStore()
const authStore = useAuthStore()
const permissionStore = usePermissionStore()

const options = reactive([
  {
    label: '个人资料',
    key: 'profile',
    icon: () => h('i', { class: 'i-fe:user text-14' }),
    show: computed(() => permissionStore.accessRoutes?.some(item => item.path === '/profile')),
  },
  {
    label: '退出登录',
    key: 'logout',
    icon: () => h('i', { class: 'i-fe:log-out text-14' }),
  },
])

function handleSelect(key) {
  switch (key) {
    case 'profile':
      router.push('/profile')
      break
    case 'logout':
      $dialog.confirm({
        title: '提示',
        type: 'info',
        content: '确认退出？',
        async confirm() {
          await authStore.logout()
          $message.success('已退出登录')
        },
      })
      break
  }
}
</script>
