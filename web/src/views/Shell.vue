<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { store, currentChannel, openChannel, openThread, openModal, closeModal } from '../store'
import { post } from '../api'
import Sidebar from '../components/Sidebar.vue'
import PostItem from '../components/PostItem.vue'
import Composer from '../components/Composer.vue'
import ThreadPanel from '../components/ThreadPanel.vue'
import MemberList from '../components/MemberList.vue'
import Modal from '../components/ui/Modal.vue'
import CreateChannel from '../components/modals/CreateChannel.vue'
import ChannelSettings from '../components/modals/ChannelSettings.vue'
import InvitePeople from '../components/modals/InvitePeople.vue'
import InviteAgent from '../components/modals/InviteAgent.vue'
import EditAgent from '../components/modals/EditAgent.vue'
import People from '../components/modals/People.vue'

const route = useRoute(), router = useRouter()
const ids = computed(() => {
  const m = /^\/c\/(\d+)(?:\/t\/(\d+))?/.exec(route.path)
  return { channel: m ? Number(m[1]) : null, thread: m?.[2] ? Number(m[2]) : null }
})
const scroller = ref<HTMLElement | null>(null)

// the URL is the source of truth for what's open; the channel list decides what you may open
watch([ids, () => store.channels.length], async () => {
  const want = ids.value.channel
  const ok = want != null && store.channels.some(c => c.id === want)
  if (!ok) {
    const first = store.channels.find(c => c.name === 'general') ?? store.channels[0]
    if (first && (want != null || route.path === '/' || route.path === '')) router.replace(`/c/${first.id}`)
    else if (!first) await openChannel(null)
    return
  }
  await openChannel(want)
  await openThread(ids.value.thread).catch(() => router.replace(`/c/${want}`))
}, { immediate: true })

const posts = computed(() => store.feed.posts)
watch(() => [posts.value.length, store.feed.channelId], async () => {
  await nextTick(); if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
}, { flush: 'post' })

const goChannel = (id: number) => router.push(`/c/${id}`)
const openT = (rootId: number) => router.push(`/c/${store.feed.channelId}/t/${rootId}`)
const closeT = () => router.push(`/c/${store.feed.channelId}`)

async function send(text: string) {
  const p = await post(`/channels/${store.feed.channelId}/posts`, { body: text })
  // a post that mentions an agent starts a thread on it: open it
  if (p.hasThread) openT(p.id)
}
</script>
<template>
  <div class="h-full grid" :style="{ gridTemplateColumns: `64px 244px minmax(0,1fr) ${store.feed.thread ? '420px' : '260px'}` }">
    <div class="bg-rail border-r border-line flex flex-col items-center gap-2 py-3" aria-label="Servers">
      <div class="w-[42px] h-[42px] rounded-[14px] grid place-items-center font-bold text-white relative" style="background: linear-gradient(135deg, var(--accent), #D95A3A)" :title="store.serverName">
        {{ store.serverName[0]?.toUpperCase() }}
        <span class="absolute -left-[11px] top-2 bottom-2 w-1 rounded-r bg-ink" />
      </div>
    </div>
    <Sidebar :channel-id="store.feed.channelId" @go="goChannel" />

    <main class="bg-panel flex flex-col min-w-0 min-h-0">
      <template v-if="currentChannel">
        <header class="h-[52px] shrink-0 border-b border-line flex items-center gap-2.5 px-4 min-w-0">
          <h2 class="m-0 text-[15px] font-semibold whitespace-nowrap" data-testid="channel-title">{{ currentChannel.isPrivate ? '🔒' : '#' }} {{ currentChannel.name }}</h2>
          <span v-if="currentChannel.topic" class="text-muted text-[13px] truncate border-l border-line pl-2.5" data-testid="channel-topic">{{ currentChannel.topic }}</span>
          <span class="ml-auto flex gap-1.5">
            <button v-if="currentChannel.canManage" class="border border-line bg-raised rounded-btn px-2.5 py-1 text-[13px] cursor-pointer hover:bg-hover" data-testid="channel-settings" @click="openModal('channel-settings', { id: currentChannel.id })">Settings</button>
          </span>
        </header>
        <div ref="scroller" class="flex-1 min-h-0 overflow-auto px-2 pt-2.5 pb-1.5" data-testid="feed">
          <p v-if="!posts.length && !store.loadingFeed" class="text-muted text-center mt-10">No posts yet. Say something to #{{ currentChannel.name }}.</p>
          <PostItem v-for="p in posts" :key="p.id" :post="p" @open-thread="openT" />
        </div>
        <Composer :placeholder="`Message #${currentChannel.name}`" @send="send" />
      </template>
      <div v-else class="flex-1 grid place-items-center text-muted">Pick a channel, or create one with +.</div>
    </main>

    <ThreadPanel v-if="store.feed.thread" @close="closeT" />
    <MemberList v-else />

    <Modal v-if="store.modal?.name === 'create-channel'" title="Create a channel" @close="closeModal"><CreateChannel /></Modal>
    <ChannelSettings v-if="store.modal?.name === 'channel-settings'" :channel-id="store.modal.props.id" />
    <InvitePeople v-if="store.modal?.name === 'invite-people'" />
    <InviteAgent v-if="store.modal?.name === 'invite-agent'" />
    <EditAgent v-if="store.modal?.name === 'edit-agent'" :agent-id="store.modal.props.id" />
    <People v-if="store.modal?.name === 'people'" />
  </div>
</template>
