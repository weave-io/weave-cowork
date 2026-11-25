<script setup lang="ts">
import { computed } from 'vue'
import { GitBranch, RotateCcw } from 'lucide-vue-next'
import type { Post } from '../types'
import { memberById } from '../store'
import { post as postApi } from '../api'
import { renderBody } from '../render'
import { handles } from '../store'
import { Button } from '@/components/ui/button'
import MemberAvatar from './MemberAvatar.vue'
import AgentBadge from './AgentBadge.vue'

// A resolved thread's outcome (step 4 of mockups/cowork/projects.html, "Done"): the agent's title and summary, how long it took and what it
// used, and what became of its branch. Reopen makes the thread work again; the outcome stays as what was done then.
const props = defineProps<{ post: Post }>()
const m = computed(() => props.post.meta ?? {})
const author = computed(() => memberById(props.post.authorId))
const summary = computed(() => renderBody(m.value.summary ?? '', handles.value))
function duration(ms: number) {
  const s = Math.round(ms / 1000)
  if (s < 60) return `${s} s`
  const min = Math.round(s / 60)
  return min < 60 ? `${min} min` : `${Math.floor(min / 60)} h ${min % 60} min`
}
const used = computed(() => {
  const parts: string[] = []
  if (m.value.tokens) parts.push(`${Number(m.value.tokens).toLocaleString()}${m.value.capTokens ? ` of ${Number(m.value.capTokens).toLocaleString()}` : ''} tokens`)
  if (m.value.cost != null) parts.push(`$${Number(m.value.cost).toFixed(2)}${m.value.capUsd ? ` of $${Number(m.value.capUsd).toFixed(2)}` : ''}`)
  if (!m.value.sandbox) parts.push(`on ${memberById(m.value.agentId)?.ownerId ? memberById(memberById(m.value.agentId)!.ownerId!)?.displayName + "'s own access" : 'its owner\'s own access'}`)
  return parts
})
const branch = computed(() => {
  const b = m.value.branch; if (!b) return null
  const what = b.state === 'deleted' ? 'no changes, deleted with its volume' : b.state === 'kept' ? (b.reason ?? 'kept') : b.state === 'volume' ? `in its sandbox's volume on ${b.host}` : `in a worktree on ${b.host ?? 'its runner'}`
  return { name: b.name, what }
})
const reopen = () => postApi(`/threads/${props.post.threadId}/reopen`, {}).catch(() => {})
</script>
<template>
  <div class="grid grid-cols-[32px_minmax(0,1fr)] gap-3 px-2.5 py-1.5" :data-post="post.id" data-testid="outcome-post">
    <MemberAvatar :member="author" class="mt-0.5" />
    <div class="min-w-0">
      <div class="flex flex-wrap items-baseline gap-[7px]"><b class="text-[13.5px] font-semibold">{{ author?.displayName }}</b><AgentBadge v-if="author?.kind === 'agent'" /></div>
      <div class="mt-1 grid max-w-[560px] gap-2 rounded-card border px-3.5 py-3 text-[13.5px]" style="border-color: color-mix(in srgb, var(--complete) 40%, var(--border)); background: var(--card-bg)" data-testid="outcome-card" :data-state="m.state">
        <div class="flex flex-wrap items-center gap-2"><span class="status-pill" :class="m.state === 'reopened' ? 'starting' : 'done'">{{ m.state === 'reopened' ? 'reopened' : 'done' }}</span><b data-testid="outcome-title">{{ m.title }}</b></div>
        <div class="body outcome-summary" data-testid="outcome-summary" v-html="summary" />
        <div v-if="branch" class="grid grid-cols-[13px_minmax(0,1fr)] items-start gap-x-2 text-[12.5px]" data-testid="outcome-branch">
          <GitBranch :size="13" class="mt-[3px] text-muted" aria-hidden="true" />
          <code class="max-w-full justify-self-start truncate whitespace-nowrap" :title="branch.name">{{ branch.name }}</code>
          <span class="col-start-2 text-muted">{{ branch.what }}</span>
        </div>
        <div class="flex flex-wrap gap-x-3.5 gap-y-1 text-xs text-muted" data-testid="outcome-meta">
          <span>{{ duration(m.elapsedMs ?? 0) }}</span><span v-for="u in used" :key="u">{{ u }}</span>
          <span>{{ m.toolCalls ?? 0 }} tool call{{ m.toolCalls === 1 ? '' : 's' }} · {{ m.asks ?? 0 }} ask{{ m.asks === 1 ? '' : 's' }}</span>
          <span v-if="m.sandbox">sandbox on {{ m.sandbox.pool }}</span>
        </div>
        <div v-if="m.state === 'done'"><Button size="sm" variant="outline" class="h-7 gap-1 px-2 text-xs" data-testid="reopen" @click="reopen"><RotateCcw :size="12" />Reopen</Button></div>
      </div>
    </div>
  </div>
</template>
<style scoped>
.outcome-summary :deep(ul) { margin: 0; padding-left: 18px; }
</style>
