<script setup lang="ts">
import { computed } from 'vue'
import { Check, X, Clock, Circle } from 'lucide-vue-next'
import type { Post, SandboxStep } from '../types'
import { memberById } from '../store'
import { Spinner } from '@/components/ui/spinner'

// A team template's sandbox starting for a thread (step 4 of mockups/cowork/projects.html): the pool picked, the sandbox started, the
// workspace ready, the harness starting. The server writes the rows as the pool reports; the card says when it stopped and why.
const props = defineProps<{ post: Post }>()
const meta = computed(() => props.post.meta ?? {})
const steps = computed<SandboxStep[]>(() => meta.value.steps ?? [])
const asker = computed(() => memberById(meta.value.askedBy ?? 0))
const parts = (t: string) => t.split('`').map((text, i) => ({ text, code: i % 2 === 1 }))
const idle = (s: number | undefined) => s == null ? 'a while' : s >= 60 && s % 60 === 0 ? `${s / 60} minute${s === 60 ? '' : 's'}` : `${s} seconds`
const paused = computed(() => meta.value.why === 'paused' ? memberById(meta.value.by ?? 0)?.displayName ?? 'A lead' : null)
const footer = computed(() => {
  switch (meta.value.state) {
    case 'waiting': case 'starting': case 'resuming': return 'The message goes in as soon as the harness is ready. Replies sent now wait in order.'
    case 'suspending': return paused.value ? `Stopping: ${paused.value} paused the project's agents.` : `Stopping: nothing happened for ${idle(meta.value.idleSeconds)}.`
    case 'suspended': return paused.value ? `Stopped: ${paused.value} paused the project's agents. Its volume is kept, so it picks up where it left off when they're resumed.`
      : `Stopped after ${idle(meta.value.idleSeconds)} idle. Its volume is kept, so the next message picks up where it left off.`
    case 'deleted': return 'The thread is done: its sandbox and its volume were deleted.'
    case 'failed': return meta.value.reason ? `It couldn't start: ${meta.value.reason}` : "It couldn't start."
    default: return ''
  }
})
</script>
<template>
  <div class="my-1.5 ml-[44px] max-w-[560px]" data-testid="sandbox-card" :data-state="meta.state" :data-resume="meta.resume ? 'true' : 'false'">
    <div class="grid grid-cols-[minmax(0,1fr)] gap-[7px] rounded-card border border-border bg-card-bg px-3 py-2.5 text-[13px]" :aria-label="meta.resume ? 'Resuming the sandbox' : 'Starting the sandbox'">
      <div v-for="s in steps" :key="s.key" class="flex items-center gap-[9px]" :data-step="s.key" :data-step-state="s.state">
        <Check v-if="s.state === 'done'" :size="14" class="shrink-0 text-running-text" aria-label="Done" />
        <Spinner v-else-if="s.state === 'running'" size="xs" class="shrink-0" />
        <Clock v-else-if="s.state === 'waiting'" :size="14" class="pulse shrink-0 text-idle-text" aria-label="Waiting" />
        <X v-else-if="s.state === 'failed'" :size="14" class="shrink-0 text-error-text" aria-label="Failed" />
        <Circle v-else :size="12" class="mx-px shrink-0 text-muted/60" aria-hidden="true" />
        <span class="min-w-0" :class="s.state === 'todo' && 'text-muted'"><template v-for="(p, i) in parts(s.text)" :key="i"><code v-if="p.code" class="inline-block max-w-full truncate rounded-[4px] border border-border bg-panel-bg px-1 align-bottom font-mono text-[12px] leading-[18px] whitespace-nowrap" :title="p.text">{{ p.text }}</code><template v-else>{{ p.text }}</template></template></span>
        <span v-if="s.secs != null && s.state === 'done'" class="ml-auto shrink-0 text-xs text-muted tabular-nums">{{ s.secs.toFixed(1) }} s</span>
      </div>
    </div>
    <p v-if="footer" class="mx-0.5 mt-1.5 mb-0 text-[12.5px] text-muted" :class="meta.state === 'failed' && 'text-error-text'" data-testid="sandbox-footer">{{ footer }}<template v-if="asker"> {{ asker.displayName }}</template></p>
  </div>
</template>
