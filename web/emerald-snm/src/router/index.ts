import { createRouter, createWebHashHistory } from 'vue-router'

const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    {
      path: '/',
      name: 'home',
      component: () => import('@/views/HomeView.vue'),
    },
    {
      path: '/node/:id',
      alias: ['/instance/:id'],
      name: 'instance-detail',
      component: () => import('@/views/InstanceDetail.vue'),
    },
  ],
})

export default router
