import { reactive, computed } from 'vue'
import * as signalR from '@microsoft/signalr'
import { get, post, ApiError } from './api'
import { applyEvent } from './reducer'
import type { Channel, ChannelThreads, FeedState, Member, Post, Project, ThreadState, ThreadSummary } from './types'
import { loadFolded, patchSummary, toggleFolded } from './threads'
import { artifactPostArrived } from './artifacts'

export const store = reactive({
  phase: 'loading' as 'loading' | 'setup' | 'signin' | 'app',
  serverName: '',
  /** How people sign in here: whether the server has a single sign-on provider, and whether it is a server from before one whose accounts had passwords. */
  auth: { sso: true, passwordAccounts: false },
  meId: 0,
  isAdmin: false,
  members: [] as Member[],
  channels: [] as Channel[],
  projects: [] as Project[],
  feed: { channelId: null, posts: [], thread: null } as FeedState,
  loadingFeed: false,
  voice: { enabled: false, url: null as string | null },
  voicePresence: {} as Record<number, number[]>,
  live: false,
  /** the last event position this browser has seen: a reconnect asks for everything after it */
  position: 0,
  modal: null as null | { name: string; props?: any },
  /** unread messages per channel and mentions not yet seen: from the server's `unread` projection */
  unread: { channels: {} as Record<string, number>, mentions: 0 },
  /** counts up when the server says a projection moved, so an open screen asks again */
  viewsVersion: 0,
  /** the channel list's threads, per channel: the most recently active ones that belong there and how many the channel has (brief 8) */
  threadList: {} as Record<number, { total: number; threads: ThreadSummary[] }>,
  /** counts up whenever any thread changes, so All threads asks again */
  threadsVersion: 0,
  /** channels whose threads are folded away in the list (remembered in this browser) */
  folded: loadFolded() as number[],
})

export const foldThreads = (channelId: number) => { store.folded = toggleFolded(store.folded, channelId) }

/** The channel list's threads. Asked for again (shortly, once for a burst) whenever a thread changes or a view moves (unread counts). */
let threadListTimer: ReturnType<typeof setTimeout> | null = null
export async function loadThreadList() {
  try {
    const list: ChannelThreads[] = await get('/thread-list')
    store.threadList = Object.fromEntries(list.map(c => [c.channelId, { total: c.total, threads: c.threads }]))
  } catch { /* signed out, or offline: the next change asks again */ }
}
function scheduleThreadList() {
  store.threadsVersion++
  if (threadListTimer) return
  threadListTimer = setTimeout(() => { threadListTimer = null; loadThreadList() }, 150)
}
/** A thread's live update: the list shows its new title and status at once, and asks for the list again (order, what belongs in it). */
function threadChanged(p: any) {
  if (p?.thread) {
    const c = store.threadList[p.thread.channelId]
    if (c) patchSummary(c.threads, p.thread as ThreadSummary)
  }
  scheduleThreadList()
}

/** The person has read the thread they are looking at, up to its last reply (one event, and only if something is new). */
let threadReadTimer: ReturnType<typeof setTimeout> | null = null
export function markThreadRead(rootId: number | null) {
  if (rootId == null || document.visibilityState === 'hidden') return
  if (threadReadTimer) clearTimeout(threadReadTimer)
  threadReadTimer = setTimeout(async () => { threadReadTimer = null; try { await post(`/threads/${rootId}/read`); await loadThreadList() } catch { /* try again on the next reply */ } }, 250)
}

export const memberById = (id: number) => store.members.find(m => m.id === id)
export const me = computed(() => memberById(store.meId))
export const currentChannel = computed(() => store.channels.find(c => c.id === store.feed.channelId) ?? null)
export const handles = computed(() => new Set(store.members.filter(m => !m.removed).map(m => m.handle.toLowerCase())))

let unreadTimer: ReturnType<typeof setTimeout> | null = null
export async function loadUnread() {
  try { store.unread = await get('/unread') } catch { /* signed out */ }
}
/** Tell the server the person has read the channel they are looking at (it writes one event, and only if something is new). */
let readTimer: ReturnType<typeof setTimeout> | null = null
export function markRead(channelId: number | null) {
  if (channelId == null || document.visibilityState === 'hidden') return
  if (readTimer) clearTimeout(readTimer)
  readTimer = setTimeout(async () => { readTimer = null; try { await post(`/channels/${channelId}/read`); await loadUnread() } catch { /* try again on the next change */ } }, 250)
}

export function openModal(name: string, props?: any) { store.modal = { name, props } }
export function closeModal() { store.modal = null }

export async function init() {
  const st = await get('/setup/status')
  store.serverName = st.serverName ?? ''
  store.auth = { sso: !!st.sso, passwordAccounts: !!st.passwordAccounts }
  if (st.needsSetup) { store.phase = 'setup'; return }
  try { store.position = (await loadBootstrap()).position ?? 0; store.phase = 'app'; startHub(); loadThreadList() }
  catch (e) { if (e instanceof ApiError && e.status === 401) store.phase = 'signin'; else throw e }
}

export async function loadBootstrap() {
  const b = await get('/bootstrap')
  store.serverName = b.server.name
  store.meId = b.meId
  store.isAdmin = b.isAdmin
  store.members = b.members
  store.channels = b.channels
  store.projects = b.projects ?? []
  store.voice = b.voice ?? { enabled: false, url: null }
  store.voicePresence = b.voicePresence ?? {}
  return b
}

export async function signedIn() {
  store.position = (await loadBootstrap()).position ?? 0
  store.phase = 'app'
  startHub()
  loadThreadList()
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
  store.members = []; store.channels = []; store.projects = []; store.threadList = {}
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
  store.feed.thread = { rootId, root: t.root, replies: t.replies, agents: t.agents, startedBy: t.startedBy, summary: t.thread ?? null, canRename: !!t.canRename } as ThreadState
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
  if (evt === 'thread-updated') threadChanged(p)
  // an artifact's card: a new version reaches whoever shows it, and the canvas opens by itself for whoever started the thread
  const open = store.feed.thread
  if ((evt === 'post-created' || evt === 'post-updated') && p?.kind === 'artifact')
    artifactPostArrived(p as Post, evt === 'post-created', store.meId, open && open.rootId === p.threadId ? open.startedBy : null)
}

/** After a reconnect: ask for everything after the last position seen, and apply it as if it had arrived live. */
export async function catchUp() {
  if (!hub || catchingUp) return
  catchingUp = true
  try {
    const r = await hub.invoke('CatchUp', store.position)
    if (r.truncated) { await reloadFeed(); store.position = r.head; return }     // too far behind (or the server was reset): show it afresh
    for (const m of r.messages) applyEvent(store.feed, m.name, m.payload)
    if (r.messages.some((m: any) => m.name === 'thread-updated')) scheduleThreadList()
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
  hub.on('views-changed', () => {
    store.viewsVersion++
    scheduleThreadList()             // unread replies in threads moved
    if (unreadTimer) return
    unreadTimer = setTimeout(() => { unreadTimer = null; loadUnread() }, 120)
  })
  hub.on('bootstrap-changed', () => {
    if (pending) return
    pending = setTimeout(async () => { pending = null; try { await loadBootstrap(); scheduleThreadList() } catch { /* signed out */ } }, 120)
  })
  hub.onreconnected(() => {
    store.live = true
    catchingUp = false
    catchUp().catch(() => reloadFeed().catch(() => {}))
    loadBootstrap().catch(() => {})
    loadUnread()
    loadThreadList()
    for (const f of onReconnect) f()
  })
  hub.onreconnecting(() => { store.live = false })
  hub.onclose(() => { store.live = false })
  hub.start().then(() => { store.live = true; loadUnread() }).catch(() => { store.live = false })
}

/** Who is in a channel: a public channel outside any project includes every person plus the agents added; one inside a project, the project's
 *  people and agents (the server sends them as its members); a private one only those added. */
export function channelMembers(ch: Channel | null): Member[] {
  if (!ch) return []
  const open = !ch.isPrivate && ch.projectId == null
  return store.members.filter(m => !m.removed && (m.kind === 'person' && open || ch.memberIds.includes(m.id)))
}
export const projectById = (id: number | null | undefined) => store.projects.find(p => p.id === id)
