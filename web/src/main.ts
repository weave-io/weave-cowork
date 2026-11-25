import { createApp } from 'vue'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import './assets/main.css'
import './app.css'
import { initTheme } from './theme'

initTheme()
const router = createRouter({
  history: createWebHistory(),
  routes: [{ path: '/:pathMatch(.*)*', component: { render: () => null } }],
})
createApp(App).use(router).mount('#app')
