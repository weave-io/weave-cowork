<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { store, currentChannel, openChannel, openThread, openModal, closeModal, markRead, markThreadRead } from '../store'
import { post } from '../api'
import Sidebar from '../components/Sidebar.vue'
import PostItem from '../components/PostItem.vue'
import Composer from '../components/Composer.vue'
import ThreadPanel from '../components/ThreadPanel.vue'
import VoiceRoom from '../components/VoiceRoom.vue'
import MemberList from '../components/MemberList.vue'
import { Hash, Lock, Volume2, Menu, Users, Settings as SettingsIcon, MessageSquareText, MessagesSquare } from 'lucide-vue-next'
import ThreadView from '../components/threads/ThreadView.vue'
import ThreadsView from '../components/threads/ThreadsView.vue'
import { parseChannelPath, threadsPath } from '../threads'
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
import Audit from '../components/modals/Audit.vue'
import Mentions from '../components/modals/Mentions.vue'
import AgentActivity from '../components/modals/AgentActivity.vue'
import SearchView from './SearchView.vue'
import { searchFocus } from '../search'
import NewProject from '../components/modals/NewProject.vue'
import AddAgentToProject from '../components/modals/AddAgentToProject.vue'
import ProjectSettings from './ProjectSettings.vue'
import WhereAgentsRun from '../components/modals/WhereAgentsRun.vue'
import ServerSettings from '../components/modals/ServerSettings.vue'
import ArtifactPage from '../components/artifacts/ArtifactPage.vue'
import { canvasOpenFor } from '../artifacts'
import { ResizeHandle } from '@/components/ui/resize-handle'
import { MEMBERS, THREAD_PANEL, useElementWidth, usePanelWidth } from '../panels'

const route = useRoute(), router = useRouter()
// /c/:id the channel, /c/:id/t/:root a thread's quick look beside it, /c/:id/threads All threads, /c/:id/threads/:root a thread's full view
const ids = computed(() => parseChannelPath(route.path))
const view = computed(() => ids.value.view)
const projectPage = computed(() => { const m = /^\/p\/(\d+)/.exec(route.path); return m ? Number(m[1]) : null })
// /artifacts/:id: an artifact on its own page ("Open full view" in the canvas)
const artifactPage = computed(() => { const m = /^\/artifacts\/(\d+)\/?$/.exec(route.path); return m ? Number(m[1]) : null })
const scroller = ref<HTMLElement | null>(null)
const searching = computed(() => route.path === '/search')

// Ctrl+K / ⌘K: search, from anywhere
function onKey(e: KeyboardEvent) {
  if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'k' || e.altKey || e.shiftKey) return
  e.preventDefault(); navOpen.value = false
  if (searching.value) searchFocus.value++; else router.push('/search')
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

// a result opened from search: once its channel (or thread) is showing, scroll the post into view and light it for a moment
let focusRun = 0
async function focusPost(postId: number, channel: number, thread: number | null) {
  const mine = ++focusRun, until = Date.now() + 4000
  while (Date.now() < until && mine === focusRun) {
    const ready = store.feed.channelId === channel && !store.loadingFeed && (thread == null || store.feed.thread?.rootId === thread)
    const el = ready ? document.querySelector<HTMLElement>(`${thread != null ? (view.value === 'full' ? '[data-testid=thread-view] ' : '[data-testid=thread-panel] ') : '[data-testid=feed] '}[data-post="${postId}"]`) : null
    if (el) {
      await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))     // after the panels' own scroll-to-bottom
      document.querySelectorAll('[data-focused]').forEach(x => { x.removeAttribute('data-focused'); x.classList.remove('post-flash') })
      el.scrollIntoView({ block: 'center' }); el.setAttribute('data-focused', 'true'); el.classList.add('post-flash')
      setTimeout(() => el.classList.remove('post-flash'), 2500)
      return
    }
    await new Promise(r => setTimeout(r, 50))
  }
}
watch(() => [route.query.post, ids.value.channel, ids.value.thread] as const, ([p, ch, t]) => {
  if (typeof p === 'string' && ch != null) focusPost(Number(p), ch, t)
}, { immediate: true })

// the URL is the source of truth for what's open; the channel list decides what you may open
watch([ids, () => store.channels.length], async () => {
  if (searching.value) return
  const want = ids.value.channel
  const ok = want != null && store.channels.some(c => c.id === want)
  if (!ok) {
    const first = store.channels.find(c => c.name === 'general') ?? store.channels[0]
    if (projectPage.value != null) { await openChannel(null); return }          // a project's settings page: no channel open
    if (artifactPage.value != null) return                                       // an artifact's own page keeps whatever was open
    if (first && (want != null || route.path === '/' || route.path === '')) router.replace(`/c/${first.id}`)
    else if (!first) await openChannel(null)
    return
  }
  await openChannel(want)
  await openThread(ids.value.thread).catch(() => router.replace(`/c/${want}`))
}, { immediate: true })

// a thread that is open (beside the channel or in full) is a thread that is read: when it opens, and as replies arrive
watch(() => [searching.value ? null : store.feed.thread?.rootId ?? null, store.feed.thread?.replies.length ?? 0] as const, ([id]) => { if (id != null) markThreadRead(id) }, { immediate: true })

// a channel that is open is a channel that is read: when it has unread messages (it was just opened, or something arrived), say so
watch(() => [searching.value ? null : store.feed.channelId, store.unread.channels[String(store.feed.channelId)] ?? 0] as const, ([id, n]) => { if (id != null && n > 0) markRead(id) }, { immediate: true })

const posts = computed(() => store.feed.posts)
watch(() => [posts.value.length, store.feed.channelId], async () => {
  await nextTick(); if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight
}, { flush: 'post' })

// back from search to the channel that was open: its feed is drawn again, at the bottom
watch(searching, async s => { if (!s) { await nextTick(); if (scroller.value) scroller.value.scrollTop = scroller.value.scrollHeight } }, { flush: 'post' })

// brief 10: the quick look and the member list are sized by their handles and remembered. The member list's room leaves the quick look its
// minimum; the quick look gets what the member list leaves (so neither squeezes the channel below 360 px).
const area = ref<HTMLElement | null>(null)
const areaWidth = useElementWidth(area)
const panelShown = computed(() => view.value === 'panel' && !!store.feed.thread)
const membersShown = computed(() => !isPhone.value && (!panelShown.value || isWide.value) && !(view.value === 'full' && canvasOpenFor(ids.value.thread)))
const membersWidth = usePanelWidth(MEMBERS, () => areaWidth.value - (panelShown.value ? THREAD_PANEL.min : 0), () => panelShown.value ? 220 : 244)
const panelWidth = usePanelWidth(THREAD_PANEL, () => areaWidth.value - (membersShown.value ? membersWidth.width.value : 0), () => 400)

const goChannel = (id: number) => { navOpen.value = false; router.push(`/c/${id}`) }
const goProject = (id: number) => { navOpen.value = false; router.push(`/p/${id}`) }
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
        <div class="flex w-[244px] min-w-0 shrink-0 flex-col"><Sidebar :channel-id="searching ? null : store.feed.channelId" :project-id="projectPage" :thread-id="view === 'full' || view === 'panel' ? ids.thread : null" @go="goChannel" @project="goProject" /></div>
      </template>
      <template v-else-if="navOpen">
        <div class="fixed inset-0 z-40 bg-black/60" @click="navOpen = false" />
        <div class="fixed inset-y-0 left-0 z-50 flex w-[min(340px,calc(100%-56px))] border-r border-border bg-main-bg" data-testid="phone-nav">
          <Rail /><div class="flex min-w-0 flex-1 flex-col"><Sidebar :channel-id="searching ? null : store.feed.channelId" :project-id="projectPage" :thread-id="view === 'full' || view === 'panel' ? ids.thread : null" @go="goChannel" @project="goProject" /></div>
        </div>
      </template>

      <div ref="area" class="flex min-h-0 min-w-0 flex-1 overflow-hidden bg-panel-bg" :class="isPhone ? '' : 'rounded-panel border border-border shadow-(--sheet-shadow)'"
        :style="{ '--thread-panel-w': `${panelWidth.width.value}px`, '--members-w': `${membersWidth.width.value}px` }">
        <SearchView v-if="searching" />
        <ProjectSettings v-else-if="projectPage != null" :key="projectPage" :project-id="projectPage" class="min-h-0 min-w-0 flex-1" />
        <ArtifactPage v-else-if="artifactPage != null" :id="artifactPage" />
        <template v-else>
        <ThreadsView v-if="view === 'threads' && currentChannel" :key="'threads' + currentChannel.id" :channel-id="currentChannel.id" />
        <ThreadView v-else-if="view === 'full' && currentChannel" />
        <main v-else class="flex min-h-0 min-w-0 flex-1 flex-col" :class="isPhone && view === 'panel' && store.feed.thread && 'hidden'">
          <template v-if="currentChannel">
            <header class="flex h-[52px] shrink-0 items-center gap-2.5 border-b border-border px-3 sm:px-4">
              <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" data-testid="phone-menu" @click="navOpen = true"><Menu :size="18" /></Button>
              <h2 class="m-0 flex items-center gap-1.5 text-[15px] font-semibold whitespace-nowrap" data-testid="channel-title">
                <Volume2 v-if="currentChannel.kind === 'voice'" :size="16" class="text-muted" aria-hidden="true" /><Lock v-else-if="currentChannel.isPrivate" :size="15" class="text-muted" aria-hidden="true" /><Hash v-else :size="16" class="text-muted" aria-hidden="true" />
                <span>{{ currentChannel.name }}</span>
              </h2>
              <span v-if="currentChannel.topic && !isPhone" class="truncate border-l border-border pl-2.5 text-[13px] text-muted" data-testid="channel-topic">{{ currentChannel.topic }}</span>
              <span class="ml-auto flex gap-1.5">
                <Button v-if="currentChannel.kind !== 'voice'" variant="outline" size="sm" class="h-8 gap-1.5" data-testid="channel-threads" aria-label="Threads" title="All threads in this channel" @click="router.push(threadsPath(currentChannel.id))"><MessagesSquare :size="14" /><span class="max-sm:hidden">Threads</span></Button>
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

        <ResizeHandle v-if="panelShown && !isPhone" :panel="panelWidth" />
        <ThreadPanel v-if="panelShown" class="border-l border-border" :class="isPhone ? 'w-full' : 'w-(--thread-panel-w) shrink-0'" @close="closeT" />
        <ResizeHandle v-if="membersShown" :panel="membersWidth" />
        <MemberList v-if="membersShown" class="w-(--members-w) shrink-0 border-l border-border" />
        </template>
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
      <Audit v-if="store.modal?.name === 'audit'" />
      <Mentions v-if="store.modal?.name === 'mentions'" />
      <AgentActivity v-if="store.modal?.name === 'agent-activity'" :agent-id="store.modal.props.id" />
      <SettingsDialog v-if="store.modal?.name === 'settings'" />
      <AppDialog v-if="store.modal?.name === 'new-project'" title="New project" wide @close="closeModal"><NewProject /></AppDialog>
      <AddAgentToProject v-if="store.modal?.name === 'add-agent-to-project'" :project-id="store.modal.props.projectId" />
      <WhereAgentsRun v-if="store.modal?.name === 'where-agents-run'" />
      <ServerSettings v-if="store.modal?.name === 'server-settings'" />
    </div>
  </TooltipProvider>
</template>
