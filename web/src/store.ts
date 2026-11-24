import { reactive, computed } from 'vue'
import * as signalR from '@microsoft/signalr'
import { get, post, ApiError } from './api'
import { applyEvent } from './reducer'
import type { Channel, FeedState, Member, Post, ThreadState } from './types'

export const store = reactive({
  phase: 'loading' as 'loading' | 'setup' | 'signin' | 'app',
  serverName: '',
  meId: 0,
  isAdmin: false,
  members: [] as Member[],
  channels: [] as Channel[],
  feed: { channelId: null, posts: [], thread: null } as FeedState,
  loadingFeed: false,
  live: false,
  modal: null as null | { name: string; props?: any },
})

export const memberById = (id: number) => store.members.find(m => m.id === id)
export const me = computed(() => memberById(store.meId))
export const currentChannel = computed(() => store.channels.find(c => c.id === store.feed.channelId) ?? null)
export const handles = computed(() => new Set(store.members.filter(m => !m.removed).map(m => m.handle.toLowerCase())))

export function openModal(name: string, props?: any) { store.modal = { name, props } }
export function closeModal() { store.modal = null }

export async function init() {
  const st = await get('/setup/status')
  store.serverName = st.serverName ?? ''
  if (st.needsSetup) { store.phase = 'setup'; return }
  try { await loadBootstrap(); store.phase = 'app'; startHub() }
  catch (e) { if (e instanceof ApiError && e.status === 401) store.phase = 'signin'; else throw e }
}

export async function loadBootstrap() {
  const b = await get('/bootstrap')
  store.serverName = b.server.name
  store.meId = b.meId
  store.isAdmin = b.isAdmin
  store.members = b.members
  store.channels = b.channels
}

export async function signedIn() {
  await loadBootstrap()
  store.phase = 'app'
  startHub()
}

export async function signOut() {
  await post('/auth/logout')
  await hub?.stop(); hub = null
  store.phase = 'signin'
  store.feed = { channelId: null, posts: [], thread: null }
  store.members = []; store.channels = []
}

export async function openChannel(id: number | null) {
  if (id === store.feed.channelId) return
  store.feed = { channelId: id, posts: [], thread: null }
  if (id == null) return
  store.loadingFeed = true
  try {
    const posts: Post[] = await get(`/channels/${id}/posts`)
    if (store.feed.channelId === id) store.feed.posts = posts
    hub?.invoke('Subscribe', id).catch(() => {})
  } finally { store.loadingFeed = false }
}

export async function openThread(rootId: number | null) {
  if (rootId == null) { store.feed.thread = null; return }
  if (store.feed.thread?.rootId === rootId) return
  const t = await get(`/threads/${rootId}`)
  if (store.feed.thread?.rootId === rootId) return
  store.feed.thread = { rootId, root: t.root, replies: t.replies, agents: t.agents, startedBy: t.startedBy } as ThreadState
}

async function reloadFeed() {
  const id = store.feed.channelId, tid = store.feed.thread?.rootId ?? null
  await loadBootstrap()
  store.feed.channelId = null; store.feed.thread = null
  if (id != null && store.channels.some(c => c.id === id)) { await openChannel(id); await openThread(tid) }
}

let hub: signalR.HubConnection | null = null
let pending: ReturnType<typeof setTimeout> | null = null

function startHub() {
  if (hub) return
  hub = new signalR.HubConnectionBuilder().withUrl('/hubs/app').withAutomaticReconnect([0, 1000, 3000, 5000, 10000]).build()
  for (const evt of ['post-created', 'post-updated', 'post-delta', 'thread-updated'])
    hub.on(evt, (p: any) => applyEvent(store.feed, evt, p))
  hub.on('bootstrap-changed', () => {
    if (pending) return
    pending = setTimeout(async () => { pending = null; try { await loadBootstrap() } catch { /* signed out */ } }, 120)
  })
  hub.onreconnected(() => { store.live = true; reloadFeed().catch(() => {}) })
  hub.onreconnecting(() => { store.live = false })
  hub.onclose(() => { store.live = false })
  hub.start().then(() => { store.live = true }).catch(() => { store.live = false })
}

/** Who is in a channel: public channels include every person plus the agents added; private ones only those added. */
export function channelMembers(ch: Channel | null): Member[] {
  if (!ch) return []
  return store.members.filter(m => !m.removed && (m.kind === 'person' ? (!ch.isPrivate || ch.memberIds.includes(m.id)) : ch.memberIds.includes(m.id)))
}
