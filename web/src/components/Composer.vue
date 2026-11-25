<script setup lang="ts">
import { computed, ref } from 'vue'
import { ArrowUp } from 'lucide-vue-next'
import { store, channelMembers, currentChannel } from '../store'
import { Button } from '@/components/ui/button'
import AgentBadge from './AgentBadge.vue'

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
  <div class="relative mx-3 mt-1.5 mb-3">
    <ul v-if="options.length" class="absolute bottom-full left-0 z-10 m-0 mb-1 min-w-[220px] list-none rounded-card border border-border bg-card-bg p-[5px] shadow-(--menu-shadow)" data-testid="mention-list">
      <li v-for="(m, i) in options" :key="m.id" class="flex cursor-pointer items-center gap-2 rounded-[6px] px-2 py-1 text-[13px]" :class="i === pick && 'bg-text/7'" @mousedown.prevent="insert(m.handle)">
        <b>@{{ m.handle }}</b><span class="text-muted">{{ m.displayName }}</span><AgentBadge v-if="m.kind === 'agent'" class="ml-auto" />
      </li>
    </ul>
    <div class="flex items-end gap-2 rounded-card border border-border bg-card-bg py-2 pr-2 pl-3.5 transition-colors focus-within:border-accent">
      <textarea ref="el" v-model="text" rows="1" :placeholder="placeholder" aria-label="Message" data-testid="composer"
        class="max-h-40 min-h-[24px] flex-1 resize-none border-0 bg-transparent py-0.5 text-sm text-text outline-none placeholder:text-muted" style="field-sizing: content" @keydown="onKey" />
      <Button size="icon-sm" class="size-8 rounded-btn" :disabled="!text.trim()" aria-label="Send" @click="send"><ArrowUp :size="16" /></Button>
    </div>
  </div>
</template>
