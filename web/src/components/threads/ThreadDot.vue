<script setup lang="ts">
import { computed } from 'vue'
import { store } from '../../store'
import { threadState } from '../../threads'
import type { ThreadStatus } from '../../types'

// A thread's status as a dot, with its words on hover (and for screen readers): starting, working, waiting for you, waiting for someone,
// done, idle. Fleet's status colours: working is green and pulses, waiting amber (it pulses when it's yours to answer), done blue.
const props = defineProps<{ status?: ThreadStatus | null }>()
const st = computed(() => threadState(props.status, store.meId))
</script>
<template>
  <i class="dot" :class="[st.dot, (st.dot === 'working' || st.dot === 'waiting-you' || st.dot === 'starting') && 'pulse']" role="img" :aria-label="st.label" :title="st.label" data-testid="thread-dot" :data-state="st.dot" />
</template>
<style scoped>
.dot { display: inline-block; flex-shrink: 0; width: 8px; height: 8px; border-radius: 999px; }
.starting { background: var(--queued); }
.working { background: var(--running); }
.waiting-you, .waiting { background: var(--idle); }
.done { background: var(--complete); }
.idle { border: 1.5px solid color-mix(in srgb, var(--muted) 70%, transparent); }
</style>
