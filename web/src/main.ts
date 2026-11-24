import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import './style.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [{ path: '/:pathMatch(.*)*', component: { render: () => null } }],
})
createApp(App).use(router).mount('#app')
