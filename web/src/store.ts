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
  voice: { enabled: false, url: null as string | null },
  voicePresence: {} as Record<number, number[]>,
  live: false,
  /** the last event position this browser has seen: a reconnect asks for everything after it */
  position: 0,
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
  try { store.position = (await loadBootstrap()).position ?? 0; store.phase = 'app'; startHub() }
  catch (e) { if (e instanceof ApiError && e.status === 401) store.phase = 'signin'; else throw e }
}

export async function loadBootstrap() {
  const b = await get('/bootstrap')
  store.serverName = b.server.name
  store.meId = b.meId
  store.isAdmin = b.isAdmin
  store.members = b.members
  store.channels = b.channels
  store.voice = b.voice ?? { enabled: false, url: null }
  store.voicePresence = b.voicePresence ?? {}
  return b
}

export async function signedIn() {
  store.position = (await loadBootstrap()).position ?? 0
  store.phase = 'app'
  startHub()
}

/** voice.ts registers this so signing out leaves the room first */
export let leaveVoiceHook: (() => Promise<void>) | null = null
export const setLeaveVoiceHook = (f: () => Promise<void>) => { leaveVoiceHook = f }

export async function signOut() {
  await leaveVoiceHook?.()
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
  store.position = (await loadBootstrap()).position ?? store.position
  store.feed.channelId = null; store.feed.thread = null
  if (id != null && store.channels.some(c => c.id === id)) { await openChannel(id); await openThread(tid) }
}

let hub: signalR.HubConnection | null = null
const onReconnect: Array<() => void> = []
/** Call a hub method (used by voice to report joins and leaves). */
export const hubInvoke = (method: string, ...args: unknown[]) => hub ? hub.invoke(method, ...args) : Promise.reject(new Error('not connected'))
export const afterReconnect = (f: () => void) => { onReconnect.push(f) }
let pending: ReturnType<typeof setTimeout> | null = null

// Live messages that arrive while a reconnect is catching up wait, so they land after what was missed
let catchingUp = false
const held: Array<[string, any]> = []
function applyLive(evt: string, p: any) {
  if (catchingUp) { held.push([evt, p]); return }
  if (typeof p?.at === 'number' && p.at > store.position) store.position = p.at
  applyEvent(store.feed, evt, p)
}

/** After a reconnect: ask for everything after the last position seen, and apply it as if it had arrived live. */
export async function catchUp() {
  if (!hub || catchingUp) return
  catchingUp = true
  try {
    const r = await hub.invoke('CatchUp', store.position)
    if (r.truncated) { await reloadFeed(); store.position = r.head; return }     // too far behind (or the server was reset): show it afresh
    for (const m of r.messages) applyEvent(store.feed, m.name, m.payload)
    if (r.head > store.position) store.position = r.head
  } finally {
    catchingUp = false
    for (const [evt, p] of held.splice(0)) applyLive(evt, p)
  }
}

function startHub() {
  if (hub) return
  // never gives up: a laptop that sleeps for an hour should come back to a live page
  hub = new signalR.HubConnectionBuilder().withUrl('/hubs/app')
    .withAutomaticReconnect({ nextRetryDelayInMilliseconds: c => [0, 1000, 3000, 5000][c.previousRetryCount] ?? 10000 }).build()
  for (const evt of ['post-created', 'post-updated', 'post-deleted', 'post-delta', 'thread-updated'])
    hub.on(evt, (p: any) => applyLive(evt, p))
  hub.on('voice-presence', (p: { channelId: number; memberIds: number[] }) => { store.voicePresence = { ...store.voicePresence, [p.channelId]: p.memberIds } })
  hub.on('bootstrap-changed', () => {
    if (pending) return
    pending = setTimeout(async () => { pending = null; try { await loadBootstrap() } catch { /* signed out */ } }, 120)
  })
  hub.onreconnected(() => {
    store.live = true
    catchingUp = false
    catchUp().catch(() => reloadFeed().catch(() => {}))
    loadBootstrap().catch(() => {})
    for (const f of onReconnect) f()
  })
  hub.onreconnecting(() => { store.live = false })
  hub.onclose(() => { store.live = false })
  hub.start().then(() => { store.live = true }).catch(() => { store.live = false })
}

/** Who is in a channel: public channels include every person plus the agents added; private ones only those added. */
export function channelMembers(ch: Channel | null): Member[] {
  if (!ch) return []
  return store.members.filter(m => !m.removed && (m.kind === 'person' ? (!ch.isPrivate || ch.memberIds.includes(m.id)) : ch.memberIds.includes(m.id)))
}
