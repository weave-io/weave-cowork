<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { store, init } from './store'
import AuthCard from './views/AuthCard.vue'
import Shell from './views/Shell.vue'

const route = useRoute()
const inviteCode = computed(() => /^\/invite\/([^/]+)/.exec(route.path)?.[1] ?? null)
onMounted(() => { init() })
</script>
<template>
  <AuthCard v-if="inviteCode && store.phase !== 'app'" mode="invite" :code="inviteCode" :key="inviteCode" />
  <AuthCard v-else-if="store.phase === 'setup'" mode="setup" />
  <AuthCard v-else-if="store.phase === 'signin'" mode="signin" />
  <Shell v-else-if="store.phase === 'app'" />
  <div v-else class="min-h-full grid place-items-center text-muted">Loading…</div>
</template>
