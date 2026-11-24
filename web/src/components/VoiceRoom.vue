<script setup lang="ts">
import { computed } from 'vue'
import { store, memberById } from '../store'
import { voice, joinVoice, leaveVoice, toggleMute } from '../voice'
import Avatar from './ui/Avatar.vue'
import Button from './ui/Button.vue'

const props = defineProps<{ channelId: number }>()
const inRoom = computed(() => voice.channelId === props.channelId && voice.status !== 'idle')
const roster = computed(() => (store.voicePresence[props.channelId] ?? []).map(id => memberById(id)).filter(Boolean))
</script>
<template>
  <div class="flex-1 min-h-0 flex flex-col" data-testid="voice-room">
    <div v-if="!store.voice.enabled" class="flex-1 grid place-items-center p-8" data-testid="voice-not-set-up">
      <div class="max-w-[420px] text-center">
        <div class="text-3xl mb-2">🔇</div>
        <h3 class="m-0 mb-1 text-[16px]">Voice isn't set up on this server</h3>
        <p class="m-0 text-muted text-[13px]">To turn it on, run the LiveKit service and set <code>LIVEKIT_API_KEY</code> and <code>LIVEKIT_API_SECRET</code> on the server. The README has the steps.</p>
      </div>
    </div>
    <template v-else>
      <div v-if="!inRoom" class="flex-1 grid place-items-center p-8">
        <div class="text-center">
          <h3 class="m-0 mb-1 text-[16px]">{{ roster.length ? `${roster.length} in the call` : 'Nobody is here yet' }}</h3>
          <div v-if="roster.length" class="flex gap-2 justify-center my-3"><Avatar v-for="m in roster" :key="m!.id" :member="m" :title="m!.displayName" /></div>
          <p v-if="voice.error" role="alert" class="text-danger-ink text-[13px]">{{ voice.error }}</p>
          <Button variant="primary" data-testid="voice-join" :disabled="voice.status === 'connecting'" @click="joinVoice(channelId)">{{ voice.status === 'connecting' ? 'Joining…' : 'Join voice' }}</Button>
        </div>
      </div>
      <template v-else>
        <div class="flex-1 min-h-0 overflow-auto p-5">
          <div class="grid gap-3.5" style="grid-template-columns: repeat(auto-fill, minmax(190px, 1fr))">
            <div v-for="t in voice.tiles" :key="t.id" class="tile bg-card border border-line rounded-panel aspect-video grid place-items-center relative"
              :class="t.speaking && 'speaking'" data-testid="voice-tile" :data-member="memberById(t.id)?.handle" :data-speaking="t.speaking" :data-muted="t.muted" :data-hearing="t.hearing">
              <Avatar :member="memberById(t.id)" size="md" class="!w-[56px] !h-[56px] !text-[20px]" />
              <span class="absolute left-2.5 bottom-2 text-[12.5px] font-medium flex items-center gap-1.5">{{ t.local ? 'You' : t.name }}<span v-if="t.muted" title="Muted" aria-label="Muted">🔇</span></span>
            </div>
          </div>
        </div>
        <p v-if="voice.error" role="alert" class="text-danger-ink text-[13px] px-5 m-0">{{ voice.error }}</p>
        <div class="shrink-0 border-t border-line p-3 flex justify-center gap-2">
          <Button data-testid="voice-mute" :aria-pressed="voice.muted" @click="toggleMute">{{ voice.muted ? 'Unmute' : 'Mute' }}</Button>
          <Button variant="danger" data-testid="voice-leave" @click="leaveVoice()">Leave</Button>
        </div>
      </template>
    </template>
  </div>
</template>
<style scoped>
.tile { transition: box-shadow .12s ease, border-color .12s ease; }
.tile.speaking { border-color: var(--online); box-shadow: 0 0 0 3px color-mix(in srgb, var(--online) 55%, transparent); }
</style>
