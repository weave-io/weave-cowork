<script setup lang="ts">
import { computed } from 'vue'
import { store } from '../store'
import type { ThreadStatus } from '../types'

// Where a thread's work stands: starting, working, waiting for someone (you, when it's yours to answer), done.
const props = defineProps<{ status?: ThreadStatus | null }>()
const label = computed(() => {
  const s = props.status; if (!s) return ''
  if (s.state === 'waiting') return s.waitingForId === store.meId ? 'waiting for you' : `waiting for ${s.waitingFor ?? 'someone'}`
  return s.state
})
</script>
<template>
  <span v-if="status" class="status-pill" :class="[status.state, status.state === 'working' && 'pulse']" data-testid="thread-status" :data-state="status.state">{{ label }}</span>
</template>
