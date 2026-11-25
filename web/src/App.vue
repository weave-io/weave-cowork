<script setup lang="ts">
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { store, init } from './store'
import AuthCard from './views/AuthCard.vue'
import Shell from './views/Shell.vue'
import { ConfirmHost } from '@/components/ui/confirm'
import { Spinner } from '@/components/ui/spinner'

const route = useRoute()
const inviteCode = computed(() => /^\/invite\/([^/]+)/.exec(route.path)?.[1] ?? null)
onMounted(() => { init() })
</script>
<template>
  <ConfirmHost />
  <AuthCard v-if="inviteCode && store.phase !== 'app'" mode="invite" :code="inviteCode" :key="inviteCode" />
  <AuthCard v-else-if="store.phase === 'setup'" mode="setup" />
  <AuthCard v-else-if="store.phase === 'signin'" mode="signin" />
  <Shell v-else-if="store.phase === 'app'" />
  <div v-else class="grid h-full place-items-center bg-main-bg text-muted"><span class="flex items-center gap-2"><Spinner />Loading…</span></div>
</template>
