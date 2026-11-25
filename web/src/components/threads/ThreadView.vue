<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { Hash, Lock, Menu } from 'lucide-vue-next'
import { store } from '../../store'
import { post } from '../../api'
import { isPhone, navOpen } from '../../layout'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import Composer from '../Composer.vue'
import ThreadStatusPill from '../ThreadStatusPill.vue'
import SessionLines from './SessionLines.vue'
import ThreadConversation from './ThreadConversation.vue'
import ThreadTitle from './ThreadTitle.vue'

// A thread as a full conversation in the main area (its URL is its own, so it can be shared and reloaded): its title as the heading (edited in
// place), its status, the channel it hangs off (a link back to the post), the session line, every reply, tool row, ask and the outcome, and its
// own composer.
const router = useRouter()
const t = computed(() => store.feed.thread)
const channel = computed(() => store.channels.find(c => c.id === store.feed.channelId) ?? null)
const toPost = () => { if (channel.value && t.value) router.push({ path: `/c/${channel.value.id}`, query: { post: String(t.value.rootId) } }) }
async function send(text: string) {
  if (!t.value) return
  await post(`/channels/${store.feed.channelId}/posts`, { body: text, threadId: t.value.rootId })
}
</script>
<template>
  <main class="flex min-h-0 min-w-0 flex-1 flex-col" data-testid="thread-view">
    <template v-if="t">
      <header class="shrink-0 border-b border-border px-3 py-2.5 sm:px-4">
        <div class="flex min-h-9 items-center gap-2">
          <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
          <ThreadTitle :thread="t" size="lg" />
          <ThreadStatusPill :status="t.root.threadStatus" class="shrink-0" />
          <button v-if="channel && !isPhone" type="button" class="ml-2 flex shrink-0 cursor-pointer items-center gap-0.5 border-0 bg-transparent p-0 text-[13px] text-muted hover:text-accent-text hover:underline" data-testid="thread-channel-link" @click="toPost">
            in <Lock v-if="channel.isPrivate" :size="12" class="ml-0.5" aria-hidden="true" /><Hash v-else :size="12" class="ml-0.5" aria-hidden="true" />{{ channel.name }}
          </button>
        </div>
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
