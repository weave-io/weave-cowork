<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import { Pencil, Check, X } from 'lucide-vue-next'
import { put, ApiError } from '../../api'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import type { ThreadState } from '../../types'

// A thread's title as the heading of its full view and its quick look, edited in place: click it (or the pencil), Enter saves, Esc cancels.
// A thread always has a title, so an empty one is refused. Anyone who may post in the thread may rename it (ThreadRenamed).
const props = defineProps<{ thread: ThreadState; size?: 'lg' | 'md' }>()
const title = computed(() => props.thread.summary?.title ?? 'Thread')
const editing = ref(false), draft = ref(''), saving = ref(false), error = ref('')
const input = ref<HTMLInputElement | null>(null)
const can = computed(() => props.thread.canRename !== false && !!props.thread.summary)

function edit() {
  if (!can.value) return
  draft.value = title.value; error.value = ''; editing.value = true
  nextTick(() => { input.value?.focus(); input.value?.select() })
}
function cancel() { editing.value = false; error.value = '' }
async function save() {
  const next = draft.value.replace(/\s+/g, ' ').trim()
  if (!next) { error.value = 'A thread needs a title.'; input.value?.focus(); return }
  if (next === title.value) { cancel(); return }
  saving.value = true; error.value = ''
  try {
    const s = await put(`/threads/${props.thread.rootId}/title`, { title: next })
    props.thread.summary = { ...s, unread: props.thread.summary?.unread ?? 0 }
    editing.value = false
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'That didn’t save. Try again.' }
  finally { saving.value = false }
}
function key(e: KeyboardEvent) {
  if (e.key === 'Enter') { e.preventDefault(); save() }
  else if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); cancel() }
}
</script>
<template>
  <div class="min-w-0 flex-1" data-testid="thread-title-box">
    <div v-if="editing" class="flex min-w-0 items-center gap-1.5">
      <input ref="input" v-model="draft" name="threadTitle" maxlength="120" aria-label="Thread title" :aria-invalid="!!error"
        class="h-8 min-w-0 flex-1 rounded-btn border border-accent bg-main-bg px-2 font-semibold text-text outline-none focus:ring-2 focus:ring-accent/25"
        :class="size === 'lg' ? 'text-[18px]' : 'text-[15px]'" @keydown="key" />
      <Button size="sm" class="h-8" :disabled="saving || !draft.trim()" data-testid="save-title" @click="save"><Spinner v-if="saving" size="xs" /><Check v-else :size="14" />Save</Button>
      <Button variant="toolbar-icon" size="toolbar" aria-label="Cancel" data-testid="cancel-title" @click="cancel"><X :size="16" /></Button>
    </div>
    <div v-else class="group flex min-w-0 items-center gap-1">
      <h2 class="m-0 min-w-0 truncate font-semibold" :class="[size === 'lg' ? 'text-[19px] leading-7' : 'text-[16px] leading-6', can && 'cursor-text rounded-[5px] px-1 -mx-1 hover:bg-text/5']"
        :title="can ? `${title} (click to rename)` : title" data-testid="thread-title" @click="edit">{{ title }}</h2>
      <Button v-if="can" variant="toolbar-icon" size="toolbar" class="size-6 shrink-0" aria-label="Rename thread" title="Rename" data-testid="rename-thread" @click="edit"><Pencil :size="13" /></Button>
    </div>
    <p v-if="error" class="m-0 mt-1 text-[12.5px] text-error-text" role="alert" data-testid="title-error">{{ error }}</p>
  </div>
</template>
