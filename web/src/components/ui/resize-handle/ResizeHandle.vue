<script setup lang="ts">
import { onBeforeUnmount, ref } from 'vue'
import { dragWidth, keyStep, type PanelWidth } from '@/panels'

// A handle on a panel's left edge (brief 10): drag it to size the panel, double-click for the default width, or focus it and use the arrow keys
// (16 px, 64 with Shift), Home and End. It sits between what's beside the panel and the panel, takes no room (a 0-wide box with an 8 px grip
// over the border), shows a thin line and a resize cursor on hover (a thicker line is its keyboard focus, in place of the ring), and holds the pointer while dragging: a cover over the whole window keeps
// an iframe (a report in the canvas) from taking the pointer when it crosses it. The width is remembered when the drag ends.
const props = defineProps<{ panel: PanelWidth }>()
const dragging = ref(false)
let start = 0, startX = 0, pointer: number | null = null, grip: HTMLElement | null = null

function down(e: PointerEvent) {
  if (e.button !== 0) return
  e.preventDefault()
  grip = e.currentTarget as HTMLElement
  start = props.panel.width.value; startX = e.clientX; pointer = e.pointerId
  try { grip.setPointerCapture(e.pointerId) } catch { /* the pointer is gone already */ }
  dragging.value = true
  document.documentElement.classList.add('panel-resizing')
}
function move(e: PointerEvent) {
  if (!dragging.value || e.pointerId !== pointer) return
  props.panel.set(dragWidth(start, startX, e.clientX, props.panel.limits.value))
}
function end(keep: boolean) {
  if (!dragging.value) return
  dragging.value = false
  if (grip && pointer != null) { try { grip.releasePointerCapture(pointer) } catch { /* released already */ } }
  pointer = null
  document.documentElement.classList.remove('panel-resizing')
  if (keep) props.panel.commit(); else props.panel.set(start)
}
function key(e: KeyboardEvent) {
  if (e.key === 'Escape' && dragging.value) { e.preventDefault(); end(false); return }      // Esc while dragging: back to where it was
  const next = keyStep(e.key, e.shiftKey, props.panel.width.value, props.panel.limits.value)
  if (next == null) return
  e.preventDefault()
  props.panel.set(next); props.panel.commit()
}
onBeforeUnmount(() => end(true))
</script>
<template>
  <div class="relative z-10 w-0 shrink-0" data-testid="resize-handle-box">
    <div role="separator" aria-orientation="vertical" tabindex="0" :aria-label="panel.spec.label"
      :aria-valuenow="panel.width.value" :aria-valuemin="panel.limits.value.min" :aria-valuemax="Number.isFinite(panel.limits.value.max) ? panel.limits.value.max : undefined"
      :data-testid="`resize-${panel.spec.key}`" :data-dragging="dragging || undefined"
      class="group absolute inset-y-0 -left-1 w-2 cursor-col-resize touch-none select-none" data-resize
      title="Drag to resize, double-click for the default width"
      @pointerdown="down" @pointermove="move" @pointerup="end(true)" @pointercancel="end(true)" @lostpointercapture="end(true)"
      @dblclick="panel.reset()" @keydown="key">
      <span class="pointer-events-none absolute inset-y-0 left-1/2 w-[2px] -translate-x-1/2 bg-accent opacity-0 transition-opacity duration-150 group-hover:opacity-100 group-hover:delay-100 group-focus-visible:w-[4px] group-focus-visible:opacity-100 group-data-dragging:opacity-100 group-data-dragging:delay-0" aria-hidden="true" />
    </div>
    <Teleport to="body"><div v-if="dragging" class="fixed inset-0 z-[1000] cursor-col-resize" data-testid="resize-cover" /></Teleport>
  </div>
</template>
