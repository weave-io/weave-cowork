<script setup lang="ts">
import { computed } from 'vue'
import type { Post } from '../types'
import { memberById, handles, store } from '../store'
import { renderBody } from '../render'
import { post as postApi } from '../api'
import Avatar from './ui/Avatar.vue'
import AgentBadge from './ui/AgentBadge.vue'
import Button from './ui/Button.vue'

const props = defineProps<{ post: Post; inThread?: boolean }>()
const emit = defineEmits<{ 'open-thread': [id: number] }>()
const author = computed(() => memberById(props.post.authorId))
const time = computed(() => new Date(props.post.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' }))
const html = computed(() => renderBody(props.post.body, handles.value))
const meta = computed(() => props.post.meta ?? {})
const owner = computed(() => memberById(meta.value.ownerId))
const isOwner = computed(() => meta.value.ownerId === store.meId)
const allow = computed(() => (meta.value.options ?? []).filter((o: any) => o.kind?.startsWith('allow')))
const reject = computed(() => (meta.value.options ?? []).find((o: any) => o.kind?.startsWith('reject')))
const answer = (optionId: string | null) => postApi(`/posts/${props.post.id}/answer`, { optionId }).catch(() => {})
const answered = computed(() => (meta.value.options ?? []).find((o: any) => o.id === meta.value.answer)?.name)
</script>

<template>
  <!-- a tool call: one compact row -->
  <div v-if="post.kind === 'tool'" class="ml-[46px] my-0.5 border border-line rounded-btn bg-card px-2.5 py-1 text-[12.5px] flex gap-2 items-center max-w-[560px]" data-testid="tool-row">
    <span class="font-mono text-[11.5px] text-muted">{{ meta.toolKind || 'tool' }}</span>
    <span class="truncate">{{ post.body }}</span>
    <span class="ml-auto text-[12px]" :class="meta.status === 'failed' ? 'text-danger-ink' : meta.status === 'completed' ? 'text-online-ink' : 'text-muted pulse'">
      {{ meta.status === 'completed' ? '✓' : meta.status === 'failed' ? 'failed' : 'running…' }}
    </span>
  </div>

  <!-- the agent asks permission: only its owner can answer -->
  <div v-else-if="post.kind === 'ask'" class="ml-[46px] my-1.5 border rounded-card px-3 py-2 text-[13px] max-w-[560px]"
    style="border-color: color-mix(in srgb, var(--away) 50%, var(--border)); background: color-mix(in srgb, var(--away) 8%, transparent)" data-testid="ask-card">
    <div class="text-[11px] font-bold tracking-wide text-away-ink">{{ author?.displayName.toUpperCase() }} ASKS</div>
    <div class="mt-0.5">{{ post.body }}</div>
    <div v-if="meta.status === 'pending'" class="mt-2 flex gap-1.5 flex-wrap">
      <template v-if="isOwner">
        <Button v-for="o in allow" :key="o.id" small variant="primary" @click="answer(o.id)">{{ o.name }}</Button>
        <Button small variant="danger" @click="answer(reject?.id ?? null)">Deny</Button>
      </template>
      <span v-else class="text-muted text-[12.5px] pulse" data-testid="ask-waiting">{{ author?.displayName }} is waiting for {{ owner?.displayName }}</span>
    </div>
    <div v-else class="mt-1 text-[12.5px] text-muted">{{ meta.status === 'answered' ? `${owner?.displayName} chose “${answered ?? 'allow'}”` : `${owner?.displayName} denied this` }}</div>
  </div>

  <div v-else-if="post.kind === 'notice'" class="ml-[46px] my-1 text-[12.5px] text-muted italic">{{ author?.displayName }}: {{ post.body }}</div>

  <div v-else class="group grid grid-cols-[40px_minmax(0,1fr)] gap-3 px-2.5 py-[7px] rounded-btn hover:bg-hover relative" :data-post="post.id">
    <Avatar :member="author" />
    <div class="min-w-0">
      <div class="flex items-baseline gap-[7px] flex-wrap">
        <b class="font-semibold">{{ author?.displayName ?? 'Someone' }}</b>
        <AgentBadge v-if="author?.kind === 'agent'" />
        <span class="text-faint text-xs">{{ time }}</span>
      </div>
      <div class="body" :class="{ streaming: post.streaming }" v-html="html" />
      <button v-if="!inThread && (post.hasThread || post.replyCount > 0)"
        class="mt-1.5 block w-full max-w-[560px] text-left border border-line bg-card rounded-card px-3 py-2 cursor-pointer hover:border-accent" data-testid="thread-card"
        @click="emit('open-thread', post.id)">
        <span class="font-semibold text-[13px]">{{ post.replyCount === 1 ? '1 reply' : `${post.replyCount} replies` }}</span>
        <span v-for="h in post.threadAgents ?? []" :key="h" class="ml-2 font-mono text-[11.5px] text-muted border border-line rounded-full px-2 bg-panel">@{{ h }}</span>
      </button>
      <button v-if="!inThread && !post.hasThread && post.replyCount === 0"
        class="absolute right-2 top-1 hidden group-hover:block border border-line bg-raised rounded-btn px-2 py-0.5 text-[12px] text-muted hover:text-ink cursor-pointer"
        @click="emit('open-thread', post.id)">Reply in thread</button>
    </div>
  </div>
</template>
