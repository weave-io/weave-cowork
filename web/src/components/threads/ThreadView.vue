<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Hash, Lock, Menu, MessageSquare, Pencil, Check, X } from 'lucide-vue-next'
import { store } from '../../store'
import { post, put, ApiError } from '../../api'
import { isPhone, navOpen } from '../../layout'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Spinner } from '@/components/ui/spinner'
import Composer from '../Composer.vue'
import ThreadStatusPill from '../ThreadStatusPill.vue'
import SessionLines from './SessionLines.vue'
import ThreadConversation from './ThreadConversation.vue'

// A thread as a full conversation in the main area (its URL is its own, so it can be shared and reloaded): its title, status, the channel it
// hangs off (a link back to the post), the session line, every reply, tool row, ask and the outcome, and its own composer.
const router = useRouter()
const t = computed(() => store.feed.thread)
const channel = computed(() => store.channels.find(c => c.id === store.feed.channelId) ?? null)
const title = computed(() => t.value?.summary?.title ?? 'Thread')
const toPost = () => { if (channel.value && t.value) router.push({ path: `/c/${channel.value.id}`, query: { post: String(t.value.rootId) } }) }

// renaming: anyone in the thread
const editing = ref(false), draft = ref(''), saving = ref(false), error = ref('')
function edit() {
  draft.value = t.value?.summary?.titleFrom === 'words' ? '' : title.value
  editing.value = true; error.value = ''
  nextTick(() => { const el = document.querySelector<HTMLInputElement>('[data-testid=thread-view] input[name=threadTitle]'); el?.focus(); el?.select() })
}
async function save() {
  if (!t.value || saving.value) return
  saving.value = true; error.value = ''
  try {
    const s = await put(`/threads/${t.value.rootId}/title`, { title: draft.value })
    if (t.value) t.value.summary = { ...s, unread: t.value.summary?.unread ?? 0 }
    editing.value = false
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'That didn’t save. Try again.' }
  finally { saving.value = false }
}
function key(e: KeyboardEvent) {
  if (e.key === 'Enter') { e.preventDefault(); save() }
  else if (e.key === 'Escape') { e.preventDefault(); editing.value = false }
}
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}
</script>
<template>
  <main class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="thread-view">
    <template v-if="t">
      <header class="shrink-0 border-b border-border px-3 py-2.5 sm:px-4">
        <div class="flex min-h-8 items-center gap-2">
          <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
          <MessageSquare :size="16" class="shrink-0 text-muted" aria-hidden="true" />
          <template v-if="editing">
            <Input v-model="draft" name="threadTitle" class="h-8 max-w-[480px] min-w-0 flex-1 text-[14px]" :placeholder="t.summary?.titleFrom === 'words' ? title : 'Name this thread'" maxlength="120" aria-label="Thread title" @keydown="key" />
            <Button size="sm" class="h-8" :disabled="saving" data-testid="save-title" @click="save"><Spinner v-if="saving" size="xs" /><Check v-else :size="14" />Save</Button>
            <Button variant="toolbar-icon" size="toolbar" aria-label="Cancel" @click="editing = false"><X :size="16" /></Button>
          </template>
          <template v-else>
            <h2 class="m-0 min-w-0 truncate text-[15px] font-semibold" :title="title" data-testid="thread-title">{{ title }}</h2>
            <Button v-if="t.canRename" variant="toolbar-icon" size="toolbar" class="size-6" aria-label="Rename thread" title="Rename" data-testid="rename-thread" @click="edit"><Pencil :size="13" /></Button>
          </template>
          <ThreadStatusPill :status="t.root.threadStatus" class="shrink-0" />
          <button v-if="channel && !isPhone" type="button" class="ml-auto flex shrink-0 cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[13px] text-muted hover:text-accent-text hover:underline" data-testid="thread-channel-link" @click="toPost">
            in <Lock v-if="channel.isPrivate" :size="12" class="ml-0.5" aria-hidden="true" /><Hash v-else :size="12" class="ml-0.5" aria-hidden="true" />{{ channel.name }}
          </button>
        </div>
        <p v-if="error" class="m-0 mt-1 text-[12.5px] text-error-text" role="alert">{{ error }}</p>
        <button v-if="channel && isPhone" type="button" class="mt-0.5 flex cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[12.5px] text-muted" data-testid="thread-channel-link" @click="toPost">
          in <Hash :size="11" class="ml-0.5" aria-hidden="true" />{{ channel.name }}
        </button>
        <SessionLines :agents="t.agents" :started-by="t.startedBy" />
      </header>
      <ThreadConversation :thread="t" wide />
      <div :class="!isPhone && 'mx-auto w-full max-w-[944px]'"><Composer placeholder="Reply in thread… use @ to mention an agent" @send="send" /></div>
    </template>
    <div v-else class="grid flex-1 place-items-center"><Spinner /></div>
  </main>
</template>
