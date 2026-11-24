<script setup lang="ts">
import { computed, ref } from 'vue'
import { store, channelMembers, currentChannel } from '../store'

const props = defineProps<{ placeholder: string; busy?: boolean }>()
const emit = defineEmits<{ send: [text: string] }>()
const text = ref('')
const el = ref<HTMLTextAreaElement | null>(null)
const pick = ref(0)

const query = computed(() => /(?:^|\s)@([a-zA-Z0-9_-]*)$/.exec(text.value.slice(0, el.value?.selectionStart ?? text.value.length))?.[1] ?? null)
const options = computed(() => {
  if (query.value === null) return []
  const q = query.value.toLowerCase()
  return channelMembers(currentChannel.value).filter(m => m.id !== store.meId && m.handle.toLowerCase().startsWith(q)).slice(0, 5)
})

function insert(handle: string) {
  const pos = el.value?.selectionStart ?? text.value.length
  const before = text.value.slice(0, pos).replace(/@[a-zA-Z0-9_-]*$/, `@${handle} `)
  text.value = before + text.value.slice(pos)
  pick.value = 0
  requestAnimationFrame(() => { el.value?.focus(); el.value?.setSelectionRange(before.length, before.length) })
}
function onKey(e: KeyboardEvent) {
  if (options.value.length && (e.key === 'Enter' || e.key === 'Tab')) { e.preventDefault(); insert(options.value[pick.value]?.handle ?? options.value[0].handle); return }
  if (options.value.length && e.key === 'ArrowDown') { e.preventDefault(); pick.value = (pick.value + 1) % options.value.length; return }
  if (options.value.length && e.key === 'ArrowUp') { e.preventDefault(); pick.value = (pick.value + options.value.length - 1) % options.value.length; return }
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); send() }
}
function send() {
  const t = text.value.trim()
  if (!t) return
  emit('send', t); text.value = ''
}
</script>
<template>
  <div class="relative mx-4 mt-1.5 mb-3.5">
    <ul v-if="options.length" class="absolute bottom-full mb-1 left-0 bg-raised border border-line rounded-card shadow-lg py-1 min-w-[220px] list-none m-0 p-1 z-10" data-testid="mention-list">
      <li v-for="(m, i) in options" :key="m.id" class="px-2 py-1 rounded-btn cursor-pointer flex gap-2 items-center text-[13px]" :class="i === pick && 'bg-accent-dim'" @mousedown.prevent="insert(m.handle)">
        <b>@{{ m.handle }}</b><span class="text-muted">{{ m.displayName }}</span><span v-if="m.kind === 'agent'" class="ml-auto text-[10px] font-bold text-accent-ink">AGENT</span>
      </li>
    </ul>
    <div class="bg-card border border-line rounded-card px-3.5 py-2.5 flex gap-2.5 items-end focus-within:border-accent">
      <textarea ref="el" v-model="text" rows="1" :placeholder="placeholder" aria-label="Message" data-testid="composer"
        class="flex-1 bg-transparent border-0 resize-none text-ink text-sm max-h-40 composer-ta" style="field-sizing: content" @keydown="onKey" />
      <button class="text-accent-ink font-semibold text-[13px] bg-transparent border-0 cursor-pointer disabled:opacity-40" :disabled="!text.trim()" @click="send">Send</button>
    </div>
  </div>
</template>
