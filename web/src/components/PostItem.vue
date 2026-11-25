<script setup lang="ts">
import { computed } from 'vue'
import { Check, X, FileText, Pencil, Search, Terminal, Wrench, Globe, Trash2, Brain, MessageSquare, ShieldQuestion } from 'lucide-vue-next'
import type { Component } from 'vue'
import type { Post } from '../types'
import { memberById, handles, store } from '../store'
import { renderBody } from '../render'
import { post as postApi } from '../api'
import { formatTime } from '@/lib/format'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import MemberAvatar from './MemberAvatar.vue'
import AgentBadge from './AgentBadge.vue'
import SandboxCard from './SandboxCard.vue'
import OutcomeCard from './OutcomeCard.vue'
import ThreadStatusPill from './ThreadStatusPill.vue'

const props = defineProps<{ post: Post; inThread?: boolean }>()
const emit = defineEmits<{ 'open-thread': [id: number] }>()
const author = computed(() => memberById(props.post.authorId))
const time = computed(() => formatTime(props.post.createdAt))
const html = computed(() => renderBody(props.post.body, handles.value))
const meta = computed(() => props.post.meta ?? {})
const owner = computed(() => memberById(meta.value.ownerId))
// who answers: an agent's owner, or for a team template whoever started the thread; the project's leads too, once that person has had their time
const isOwner = computed(() => meta.value.ownerId === store.meId || ((meta.value.leadIds ?? []).includes(store.meId) && Date.now() >= (meta.value.leadAfter ?? Infinity)))
const allow = computed(() => (meta.value.options ?? []).filter((o: any) => o.kind?.startsWith('allow')))
const reject = computed(() => (meta.value.options ?? []).find((o: any) => o.kind?.startsWith('reject')))
const answer = (optionId: string | null) => postApi(`/posts/${props.post.id}/answer`, { optionId }).catch(() => {})
const answered = computed(() => (meta.value.options ?? []).find((o: any) => o.id === meta.value.answer)?.name)
// a lead may answer a template's ask once whoever started the thread has had their time
const leadLater = computed(() => (meta.value.leadIds ?? []).length && meta.value.leadAfter && Date.now() < meta.value.leadAfter
  ? `a lead can answer from ${new Date(meta.value.leadAfter).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}` : '')
// a thread stopped at its budget: the project's leads can raise its cap
const canRaise = computed(() => !!meta.value.budget && meta.value.scope === 'thread' && ((meta.value.leadIds ?? []).includes(store.meId) || store.isAdmin))
const raise = () => postApi(`/threads/${props.post.threadId}/budget/raise`, {}).catch(() => {})
const toolIcons: Record<string, Component> = { read: FileText, edit: Pencil, search: Search, execute: Terminal, fetch: Globe, delete: Trash2, think: Brain }
const toolIcon = computed(() => toolIcons[meta.value.toolKind] ?? Wrench)
</script>

<template>
  <!-- a tool call: one compact row, like Fleet's tool rows -->
  <details v-if="post.kind === 'tool' && meta.output" class="my-0.5 ml-[44px] max-w-[560px] rounded-card border border-border bg-card-bg text-[12.5px]" data-testid="tool-row" data-setup="true">
    <summary class="flex cursor-pointer list-none items-center gap-2 px-2.5 py-1">
      <Terminal :size="13" class="shrink-0 text-muted" aria-hidden="true" /><span class="font-mono text-[11.5px] text-muted">setup</span>
      <span class="min-w-0 truncate font-mono text-[12px]">{{ post.body.replace(/^setup: /, '') }}</span>
      <span class="ml-auto flex shrink-0 items-center gap-1 text-[12px]" :class="meta.status === 'failed' ? 'text-error-text' : 'text-running-text'"><Check v-if="meta.status === 'completed'" :size="13" aria-label="Done" /><template v-else><X :size="13" aria-hidden="true" />failed</template></span>
    </summary>
    <pre class="m-0 max-h-48 overflow-auto border-t border-border px-2.5 py-1.5 font-mono text-[11.5px] whitespace-pre-wrap text-muted" data-testid="setup-output">{{ meta.output }}</pre>
  </details>
  <div v-else-if="post.kind === 'tool'" class="my-0.5 ml-[44px] flex max-w-[560px] items-center gap-2 rounded-card border border-border bg-card-bg px-2.5 py-1 text-[12.5px]" data-testid="tool-row">
    <component :is="toolIcon" :size="13" class="shrink-0 text-muted" aria-hidden="true" />
    <span v-if="(meta.toolKind || 'tool') !== post.body.toLowerCase()" class="font-mono text-[11.5px] text-muted">{{ !meta.toolKind || meta.toolKind === 'other' ? 'tool' : meta.toolKind }}</span>
    <span class="min-w-0 truncate font-mono text-[12px]">{{ post.body }}</span>
    <span class="ml-auto flex shrink-0 items-center gap-1 text-[12px]" :class="meta.status === 'failed' ? 'text-error-text' : meta.status === 'completed' ? 'text-running-text' : 'text-muted'">
      <Check v-if="meta.status === 'completed'" :size="13" aria-label="Done" />
      <template v-else-if="meta.status === 'failed'"><X :size="13" aria-hidden="true" />failed</template>
      <template v-else><Spinner size="xs" />running…</template>
    </span>
  </div>

  <!-- the agent asks permission: only its owner can answer -->
  <div v-else-if="post.kind === 'ask'" class="my-1.5 ml-[44px] max-w-[560px] rounded-card border px-3 py-2 text-[13px]"
    style="border-color: color-mix(in srgb, var(--idle) 45%, var(--border)); background: color-mix(in srgb, var(--idle) 8%, transparent)" data-testid="ask-card">
    <div class="flex items-center gap-1.5 text-[11px] font-semibold tracking-[0.05em] text-idle-text uppercase"><ShieldQuestion :size="13" aria-hidden="true" />{{ author?.displayName }} asks</div>
    <div v-if="meta.egress" class="mt-0.5" data-testid="ask-egress"><b>{{ author?.displayName }} wants to reach <code class="rounded-[4px] border border-border bg-card-bg px-1 font-mono text-[12px]">{{ meta.host }}</code></b>
      <div class="text-[12.5px] text-muted" data-testid="ask-why">{{ meta.why }}</div></div>
    <div v-else class="mt-0.5">{{ post.body }}</div>
    <div v-if="meta.status === 'pending'" class="mt-2 flex flex-wrap gap-1.5">
      <template v-if="isOwner">
        <Button v-for="o in allow" :key="o.id" size="sm" :variant="o.kind === 'allow_always' ? 'outline' : 'default'" :data-answer="o.id" @click="answer(o.id)">{{ o.name }}</Button>
        <Button size="sm" variant="outline" @click="answer(reject?.id ?? null)">Deny</Button>
      </template>
      <span v-else class="pulse text-[12.5px] text-muted" data-testid="ask-waiting">{{ author?.displayName }} is waiting for {{ owner?.displayName }}<template v-if="leadLater"> · {{ leadLater }}</template></span>
    </div>
    <div v-else class="mt-1 text-[12.5px] text-muted">{{ meta.status === 'answered' ? `${owner?.displayName} chose “${answered ?? 'allow'}”` : `${owner?.displayName} denied this` }}</div>
  </div>

  <SandboxCard v-else-if="post.kind === 'sandbox'" :post="post" />

  <OutcomeCard v-else-if="post.kind === 'outcome'" :post="post" />

  <div v-else-if="post.kind === 'notice'" class="my-1 ml-[44px] text-[12.5px] text-muted italic" data-testid="notice" :data-budget="meta.budget ? meta.scope : undefined">{{ author?.displayName }}: {{ post.body }}
    <Button v-if="canRaise" size="sm" variant="outline" class="ml-1.5 h-6 px-2 text-xs not-italic" data-testid="raise-cap" @click="raise">Raise this thread's cap</Button></div>

  <div v-else class="group relative grid grid-cols-[32px_minmax(0,1fr)] gap-3 rounded-btn px-2.5 py-1.5 transition-colors hover:bg-text/4" :data-post="post.id">
    <MemberAvatar :member="author" class="mt-0.5" />
    <div class="min-w-0">
      <div class="flex flex-wrap items-baseline gap-[7px]">
        <b class="text-[13.5px] font-semibold">{{ author?.displayName ?? 'Someone' }}</b>
        <AgentBadge v-if="author?.kind === 'agent'" />
        <span class="text-xs text-muted">{{ time }}</span>
      </div>
      <div class="body" :class="{ streaming: post.streaming }" v-html="html" />
      <button v-if="!inThread && (post.hasThread || post.replyCount > 0)"
        class="mt-1.5 flex w-full max-w-[560px] cursor-pointer items-center gap-2 rounded-card border border-border bg-card-bg px-3 py-2 text-left transition-colors hover:border-accent" data-testid="thread-card"
        @click="emit('open-thread', post.id)">
        <MessageSquare :size="14" class="shrink-0 text-accent-text" aria-hidden="true" />
        <span class="shrink-0 text-[13px] font-semibold whitespace-nowrap text-accent-text">{{ post.replyCount === 1 ? '1 reply' : `${post.replyCount} replies` }}</span>
        <span v-for="h in post.threadAgents ?? []" :key="h" class="rounded-[5px] bg-accent-dim px-1.5 font-mono text-[11.5px] text-accent-text">@{{ h }}</span>
        <ThreadStatusPill :status="post.threadStatus" />
        <span v-if="post.threadStatus?.state === 'done' && post.threadStatus.outcome" class="min-w-0 truncate text-xs text-muted" data-testid="thread-outcome">{{ post.threadStatus.outcome }}</span>
      </button>
      <Button v-if="!inThread && !post.hasThread && post.replyCount === 0" variant="outline" size="sm" class="absolute top-1 right-2 hidden h-7 gap-1 px-2 text-[12px] group-hover:inline-flex focus-visible:inline-flex" @click="emit('open-thread', post.id)"><MessageSquare :size="12" aria-hidden="true" />Reply in thread</Button>
    </div>
  </div>
</template>
