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

const props = defineProps<{ post: Post; inThread?: boolean }>()
const emit = defineEmits<{ 'open-thread': [id: number] }>()
const author = computed(() => memberById(props.post.authorId))
const time = computed(() => formatTime(props.post.createdAt))
const html = computed(() => renderBody(props.post.body, handles.value))
const meta = computed(() => props.post.meta ?? {})
const owner = computed(() => memberById(meta.value.ownerId))
const isOwner = computed(() => meta.value.ownerId === store.meId)
const allow = computed(() => (meta.value.options ?? []).filter((o: any) => o.kind?.startsWith('allow')))
const reject = computed(() => (meta.value.options ?? []).find((o: any) => o.kind?.startsWith('reject')))
const answer = (optionId: string | null) => postApi(`/posts/${props.post.id}/answer`, { optionId }).catch(() => {})
const answered = computed(() => (meta.value.options ?? []).find((o: any) => o.id === meta.value.answer)?.name)
const toolIcons: Record<string, Component> = { read: FileText, edit: Pencil, search: Search, execute: Terminal, fetch: Globe, delete: Trash2, think: Brain }
const toolIcon = computed(() => toolIcons[meta.value.toolKind] ?? Wrench)
</script>

<template>
  <!-- a tool call: one compact row, like Fleet's tool rows -->
  <div v-if="post.kind === 'tool'" class="my-0.5 ml-[44px] flex max-w-[560px] items-center gap-2 rounded-card border border-border bg-card-bg px-2.5 py-1 text-[12.5px]" data-testid="tool-row">
    <component :is="toolIcon" :size="13" class="shrink-0 text-muted" aria-hidden="true" />
    <span v-if="(meta.toolKind || 'tool') !== post.body.toLowerCase()" class="font-mono text-[11.5px] text-muted">{{ meta.toolKind || 'tool' }}</span>
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
    <div class="mt-0.5">{{ post.body }}</div>
    <div v-if="meta.status === 'pending'" class="mt-2 flex flex-wrap gap-1.5">
      <template v-if="isOwner">
        <Button v-for="o in allow" :key="o.id" size="sm" @click="answer(o.id)">{{ o.name }}</Button>
        <Button size="sm" variant="outline" @click="answer(reject?.id ?? null)">Deny</Button>
      </template>
      <span v-else class="pulse text-[12.5px] text-muted" data-testid="ask-waiting">{{ author?.displayName }} is waiting for {{ owner?.displayName }}</span>
    </div>
    <div v-else class="mt-1 text-[12.5px] text-muted">{{ meta.status === 'answered' ? `${owner?.displayName} chose “${answered ?? 'allow'}”` : `${owner?.displayName} denied this` }}</div>
  </div>

  <div v-else-if="post.kind === 'notice'" class="my-1 ml-[44px] text-[12.5px] text-muted italic" data-testid="notice">{{ author?.displayName }}: {{ post.body }}</div>

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
        <span class="text-[13px] font-semibold text-accent-text">{{ post.replyCount === 1 ? '1 reply' : `${post.replyCount} replies` }}</span>
        <span v-for="h in post.threadAgents ?? []" :key="h" class="rounded-[5px] bg-accent-dim px-1.5 font-mono text-[11.5px] text-accent-text">@{{ h }}</span>
      </button>
      <Button v-if="!inThread && !post.hasThread && post.replyCount === 0" variant="outline" size="sm" class="absolute top-1 right-2 hidden h-7 gap-1 px-2 text-[12px] group-hover:inline-flex focus-visible:inline-flex" @click="emit('open-thread', post.id)"><MessageSquare :size="12" aria-hidden="true" />Reply in thread</Button>
    </div>
  </div>
</template>
