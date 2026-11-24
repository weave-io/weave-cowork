<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
defineProps<{ title: string; wide?: boolean }>()
const emit = defineEmits<{ close: [] }>()
const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') emit('close') }
onMounted(() => window.addEventListener('keydown', onKey))
onUnmounted(() => window.removeEventListener('keydown', onKey))
</script>
<template>
  <div class="fixed inset-0 z-50 grid place-items-center p-4 bg-black/45" @mousedown.self="emit('close')">
    <div role="dialog" :aria-label="title" class="bg-panel border border-line rounded-panel shadow-2xl w-full max-h-[90vh] flex flex-col"
      :class="wide ? 'max-w-2xl' : 'max-w-md'">
      <header class="flex items-center justify-between px-5 pt-4 pb-3 border-b border-line">
        <h2 class="m-0 text-[16px] font-semibold">{{ title }}</h2>
        <button class="w-7 h-7 grid place-items-center rounded-btn text-muted hover:bg-hover cursor-pointer border-0 bg-transparent" aria-label="Close" @click="emit('close')">✕</button>
      </header>
      <div class="px-5 py-4 overflow-auto"><slot /></div>
      <footer v-if="$slots.footer" class="px-5 py-3 border-t border-line flex justify-end gap-2"><slot name="footer" /></footer>
    </div>
  </div>
</template>
