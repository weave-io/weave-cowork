<script setup lang="ts">
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
// Every CoWork dialog: Fleet's Dialog, open for as long as it is mounted; closing asks the parent to unmount it.
defineProps<{ title: string; description?: string; wide?: boolean }>()
const emit = defineEmits<{ close: [] }>()
</script>
<template>
  <Dialog :open="true" @update:open="(v: boolean) => { if (!v) emit('close') }">
    <DialogContent :class="['flex max-h-[90vh] flex-col gap-0 overflow-hidden p-0', wide ? 'sm:max-w-2xl' : 'sm:max-w-md']" :aria-describedby="undefined">
      <DialogHeader class="border-b border-border px-5 pt-4 pb-3">
        <DialogTitle class="text-base">{{ title }}</DialogTitle>
        <DialogDescription v-if="description" class="text-[13px]">{{ description }}</DialogDescription>
      </DialogHeader>
      <div class="min-h-0 overflow-auto px-5 py-4"><slot /></div>
      <div v-if="$slots.footer" class="flex justify-end gap-2 border-t border-border px-5 py-3"><slot name="footer" /></div>
    </DialogContent>
  </Dialog>
</template>
