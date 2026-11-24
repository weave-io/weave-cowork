import { reactive } from 'vue'
import type { Room, Participant } from 'livekit-client'
import { post, ApiError } from './api'
import { store, hubInvoke, afterReconnect, setLeaveVoiceHook } from './store'

export interface Tile { id: number; name: string; local: boolean; speaking: boolean; muted: boolean; hearing: boolean }

/** The one voice room this tab is in, if any. LiveKit carries the audio; the CoWork server is told over the hub who joined. */
export const voice = reactive({
  channelId: null as number | null,
  status: 'idle' as 'idle' | 'connecting' | 'connected',
  muted: false,
  error: '',
  tiles: [] as Tile[],
})

// livekit-client is big: it's loaded the first time someone joins a call
const lk = () => import('livekit-client')
let room: Room | null = null
let audioEls = new Map<string, HTMLMediaElement>()

/** Where the browser reaches LiveKit: LIVEKIT_URL from the server, else this host on LiveKit's default port. */
const liveKitUrl = (configured: string | null | undefined) =>
  configured || `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.hostname}:7880`

function tileOf(p: Participant, local: boolean): Tile {
  const mic = p.getTrackPublication('microphone' as any)
  return {
    id: Number(p.identity), name: p.name || p.identity, local, speaking: p.isSpeaking, muted: !p.isMicrophoneEnabled,
    hearing: local ? true : !!mic?.isSubscribed,
  }
}
function refresh() {
  if (!room) { voice.tiles = []; return }
  voice.tiles = [tileOf(room.localParticipant, true), ...[...room.remoteParticipants.values()].map(p => tileOf(p, false))]
  voice.muted = !room.localParticipant.isMicrophoneEnabled
}

export async function joinVoice(channelId: number) {
  if (voice.channelId === channelId && voice.status !== 'idle') return
  await leaveVoice()
  voice.error = ''; voice.channelId = channelId; voice.status = 'connecting'
  try {
    const { token, url } = await post<{ token: string; url: string | null }>(`/channels/${channelId}/voice/token`)
    const { Room, RoomEvent, Track } = await lk()
    const r = new Room({ adaptiveStream: false })
    room = r
    for (const e of [RoomEvent.ParticipantConnected, RoomEvent.ParticipantDisconnected, RoomEvent.ActiveSpeakersChanged, RoomEvent.TrackMuted, RoomEvent.TrackUnmuted,
      RoomEvent.TrackSubscribed, RoomEvent.TrackUnsubscribed, RoomEvent.LocalTrackPublished, RoomEvent.LocalTrackUnpublished, RoomEvent.TrackPublished])
      r.on(e as any, refresh)
    r.on(RoomEvent.TrackSubscribed, (track, _pub, p) => {
      if (track.kind !== Track.Kind.Audio) return
      const el = track.attach(); el.style.display = 'none'; document.body.appendChild(el); audioEls.set(p.identity, el)
    })
    r.on(RoomEvent.TrackUnsubscribed, (track, _pub, p) => { track.detach().forEach(e => e.remove()); audioEls.delete(p.identity) })
    r.on(RoomEvent.Disconnected, () => { if (room === r) void leaveVoice() })
    await r.connect(liveKitUrl(url ?? store.voice.url), token)
    try { await r.localParticipant.setMicrophoneEnabled(true) }
    catch { voice.error = 'Couldn’t use your microphone, so you’re listening only. Allow microphone access to talk.' }
    await hubInvoke('JoinVoice', channelId)
    voice.status = 'connected'
    refresh()
  } catch (e) {
    voice.error = e instanceof ApiError ? e.message : 'Couldn’t join the call. Try again.'
    await leaveVoice(true)
  }
}

export async function leaveVoice(keepError = false) {
  const r = room; room = null
  for (const el of audioEls.values()) el.remove()
  audioEls = new Map()
  const was = voice.channelId
  voice.channelId = null; voice.status = 'idle'; voice.tiles = []; voice.muted = false
  if (!keepError) voice.error = ''
  if (r) { try { await r.disconnect() } catch { /* already gone */ } }
  if (was != null) { try { await hubInvoke('LeaveVoice') } catch { /* the hub is gone: the server drops us anyway */ } }
}

export async function toggleMute() {
  if (!room) return
  await room.localParticipant.setMicrophoneEnabled(room.localParticipant.isMicrophoneEnabled ? false : true).catch(() => {})
  refresh()
}

setLeaveVoiceHook(() => leaveVoice())
afterReconnect(() => { if (voice.channelId != null && voice.status === 'connected') hubInvoke('JoinVoice', voice.channelId).catch(() => {}) })
