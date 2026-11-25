<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { store, currentChannel, openChannel, openThread, openModal, closeModal } from '../store'
import { post } from '../api'
import Sidebar from '../components/Sidebar.vue'
import PostItem from '../components/PostItem.vue'
import Composer from '../components/Composer.vue'
import ThreadPanel from '../components/ThreadPanel.vue'
import VoiceRoom from '../components/VoiceRoom.vue'
import MemberList from '../components/MemberList.vue'
import { Hash, Lock, Volume2, Menu, Users, Settings as SettingsIcon, MessageSquareText } from 'lucide-vue-next'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { TooltipProvider } from '@/components/ui/tooltip'
import Rail from '../components/shell/Rail.vue'
import AppDialog from '../components/AppDialog.vue'
import SettingsDialog from '../components/modals/SettingsDialog.vue'
import { isPhone, isWide, navOpen, membersOpen } from '../layout'
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

const goChannel = (id: number) => { navOpen.value = false; router.push(`/c/${id}`) }
const openT = (rootId: number) => router.push(`/c/${store.feed.channelId}/t/${rootId}`)
const closeT = () => router.push(`/c/${store.feed.channelId}`)

async function send(text: string) {
  const p = await post(`/channels/${store.feed.channelId}/posts`, { body: text })
  // a post that mentions an agent starts a thread on it: open it
  if (p.hasThread) openT(p.id)
}
</script>
<template>
  <TooltipProvider :delay-duration="300">
    <div class="flex h-full bg-main-bg" :class="isPhone ? 'p-0' : 'gap-2 p-2'">
      <!-- desktop: the rail and the channel list sit on the window; the phone moves them behind the menu button -->
      <template v-if="!isPhone">
        <Rail />
        <div class="flex w-[244px] min-w-0 shrink-0 flex-col"><Sidebar :channel-id="store.feed.channelId" @go="goChannel" /></div>
      </template>
      <template v-else-if="navOpen">
        <div class="fixed inset-0 z-40 bg-black/60" @click="navOpen = false" />
        <div class="fixed inset-y-0 left-0 z-50 flex w-[min(340px,calc(100%-56px))] border-r border-border bg-main-bg" data-testid="phone-nav">
          <Rail /><div class="flex min-w-0 flex-1 flex-col"><Sidebar :channel-id="store.feed.channelId" @go="goChannel" /></div>
        </div>
      </template>

      <div class="flex min-h-0 min-w-0 flex-1 overflow-hidden bg-panel-bg" :class="isPhone ? '' : 'rounded-panel border border-border shadow-(--sheet-shadow)'">
        <main class="flex min-h-0 min-w-0 flex-1 flex-col" :class="isPhone && store.feed.thread && 'hidden'">
          <template v-if="currentChannel">
            <header class="flex h-[52px] shrink-0 items-center gap-2.5 border-b border-border px-3 sm:px-4">
              <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
              <h2 class="m-0 flex items-center gap-1.5 text-[15px] font-semibold whitespace-nowrap" data-testid="channel-title">
                <Volume2 v-if="currentChannel.kind === 'voice'" :size="16" class="text-muted" aria-hidden="true" /><Lock v-else-if="currentChannel.isPrivate" :size="15" class="text-muted" aria-hidden="true" /><Hash v-else :size="16" class="text-muted" aria-hidden="true" />
                <span>{{ currentChannel.name }}</span>
              </h2>
              <span v-if="currentChannel.topic && !isPhone" class="truncate border-l border-border pl-2.5 text-[13px] text-muted" data-testid="channel-topic">{{ currentChannel.topic }}</span>
              <span class="ml-auto flex gap-1.5">
                <Button v-if="currentChannel.canManage" variant="outline" size="sm" class="h-8 gap-1.5" data-testid="channel-settings" aria-label="Channel settings" @click="openModal('channel-settings', { id: currentChannel.id })"><SettingsIcon :size="14" /><span class="max-sm:hidden">Settings</span></Button>
                <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Show members" data-testid="phone-members" @click="membersOpen = true"><Users :size="18" /></Button>
              </span>
            </header>
            <VoiceRoom v-if="currentChannel.kind === 'voice'" :channel-id="currentChannel.id" />
            <div v-else ref="scroller" class="thin-scrollbar min-h-0 flex-1 overflow-auto px-1.5 pt-2.5 pb-1.5" data-testid="feed">
              <EmptyState v-if="!posts.length && !store.loadingFeed" :icon="MessageSquareText" :title="`No posts in #${currentChannel.name} yet`" description="Say something to get it started." class="mt-10" />
              <PostItem v-for="p in posts" :key="p.id" :post="p" @open-thread="openT" />
            </div>
            <Composer v-if="currentChannel.kind !== 'voice'" :placeholder="`Message #${currentChannel.name}`" @send="send" />
          </template>
          <EmptyState v-else :icon="Hash" title="Pick a channel" description="Or create one with +." class="flex-1" />
        </main>

        <ThreadPanel v-if="store.feed.thread" class="border-l border-border" :class="isPhone ? 'w-full' : 'w-[400px] shrink-0'" @close="closeT" />
        <MemberList v-if="!isPhone && (!store.feed.thread || isWide)" class="shrink-0 border-l border-border" :class="store.feed.thread ? 'w-[220px]' : 'w-[244px]'" />
      </div>

      <template v-if="isPhone && membersOpen">
        <div class="fixed inset-0 z-40 bg-black/60" @click="membersOpen = false" />
        <MemberList class="fixed inset-y-0 right-0 z-50 w-[min(320px,calc(100%-40px))] border-l border-border" />
      </template>

      <AppDialog v-if="store.modal?.name === 'create-channel'" title="Create a channel" @close="closeModal"><CreateChannel /></AppDialog>
      <ChannelSettings v-if="store.modal?.name === 'channel-settings'" :channel-id="store.modal.props.id" />
      <InvitePeople v-if="store.modal?.name === 'invite-people'" />
      <InviteAgent v-if="store.modal?.name === 'invite-agent'" />
      <EditAgent v-if="store.modal?.name === 'edit-agent'" :agent-id="store.modal.props.id" />
      <People v-if="store.modal?.name === 'people'" />
      <SettingsDialog v-if="store.modal?.name === 'settings'" />
    </div>
  </TooltipProvider>
</template>
