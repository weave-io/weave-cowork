<script setup lang="ts">
import { computed } from 'vue'
import { MicOff, Mic, PhoneOff, Volume2, VolumeX } from 'lucide-vue-next'
import { store, memberById } from '../store'
import { voice, joinVoice, leaveVoice, toggleMute } from '../voice'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/ui/empty-state'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'
import MemberAvatar from './MemberAvatar.vue'

const props = defineProps<{ channelId: number }>()
const inRoom = computed(() => voice.channelId === props.channelId && voice.status !== 'idle')
const roster = computed(() => (store.voicePresence[props.channelId] ?? []).map(id => memberById(id)).filter(Boolean))
</script>
<template>
  <div class="flex min-h-0 flex-1 flex-col" data-testid="voice-room">
    <div v-if="!store.voice.enabled" class="grid flex-1 place-items-center p-8" data-testid="voice-not-set-up">
      <EmptyState :icon="VolumeX" title="Voice isn't set up on this server" class="max-w-[440px]">
        To turn it on, run the LiveKit service and set <code>LIVEKIT_API_KEY</code> and <code>LIVEKIT_API_SECRET</code> on the server. The README has the steps.
      </EmptyState>
    </div>
    <template v-else>
      <div v-if="!inRoom" class="grid flex-1 place-items-center p-8">
        <div class="flex flex-col items-center text-center">
          <Volume2 :size="28" class="mb-2 text-muted" aria-hidden="true" />
          <h3 class="m-0 mb-1 text-base font-semibold">{{ roster.length ? `${roster.length} in the call` : 'Nobody is here yet' }}</h3>
          <div v-if="roster.length" class="my-3 flex justify-center gap-2"><MemberAvatar v-for="m in roster" :key="m!.id" :member="m" :title="m!.displayName" /></div>
          <ErrorNote v-if="voice.error" :message="voice.error" class="my-2 max-w-[420px] text-left" />
          <Button class="mt-3" data-testid="voice-join" :disabled="voice.status === 'connecting'" @click="joinVoice(channelId)"><Spinner v-if="voice.status === 'connecting'" size="sm" />{{ voice.status === 'connecting' ? 'Joining…' : 'Join voice' }}</Button>
        </div>
      </div>
      <template v-else>
        <div class="thin-scrollbar min-h-0 flex-1 overflow-auto p-5">
          <div class="grid gap-3.5" style="grid-template-columns: repeat(auto-fill, minmax(190px, 1fr))">
            <div v-for="t in voice.tiles" :key="t.id" class="tile relative grid aspect-video place-items-center rounded-panel border border-border bg-card-bg"
              :class="t.speaking && 'speaking'" data-testid="voice-tile" :data-member="memberById(t.id)?.handle" :data-speaking="t.speaking" :data-muted="t.muted" :data-hearing="t.hearing">
              <MemberAvatar :member="memberById(t.id)" size="lg" />
              <span class="absolute bottom-2 left-2.5 flex items-center gap-1.5 text-[12.5px] font-medium">{{ t.local ? 'You' : t.name }}<MicOff v-if="t.muted" :size="13" class="text-muted" aria-label="Muted" /></span>
            </div>
          </div>
        </div>
        <ErrorNote v-if="voice.error" :message="voice.error" class="mx-5" />
        <div class="flex shrink-0 justify-center gap-2 border-t border-border p-3">
          <Button variant="outline" data-testid="voice-mute" :aria-pressed="voice.muted" @click="toggleMute"><MicOff v-if="voice.muted" :size="16" /><Mic v-else :size="16" />{{ voice.muted ? 'Unmute' : 'Mute' }}</Button>
          <Button variant="destructive" data-testid="voice-leave" @click="leaveVoice()"><PhoneOff :size="16" />Leave</Button>
        </div>
      </template>
    </template>
  </div>
</template>
<style scoped>
.tile { transition: box-shadow var(--transition), border-color var(--transition); }
.tile.speaking { border-color: var(--running); box-shadow: 0 0 0 3px color-mix(in srgb, var(--running) 55%, transparent); }
</style>
