import { computed, onBeforeUnmount, onMounted, ref, watch, type Ref } from 'vue'

// Brief 10: panels you can size. The canvas, a thread's quick look and the member list sit on the right of the main area; each has a handle on
// its left edge (components/ui/resize-handle). A panel's width is remembered per browser, per panel (not per artifact or thread), and is always
// clamped to the room there is: the panel never below its minimum, and what's beside it never below its own.
// Everything here but localStorage (guarded) and the ResizeObserver is pure, so it's testable.

export interface PanelSpec {
  key: string            // what it's remembered as: cowork:panel:<key>
  min: number            // the panel never narrower
  max?: number           // the panel never wider (besides the room there is)
  beside: number         // what's beside it (the thread, the channel) never narrower
  label: string          // the handle's accessible name
}
// the canvas beside a thread's full view: at least about 320 px, and the thread at least about 360
export const CANVAS: PanelSpec = { key: 'canvas', min: 320, beside: 360, label: 'Resize the canvas' }
// a thread's quick look beside the channel (from "N replies")
export const THREAD_PANEL: PanelSpec = { key: 'thread-panel', min: 320, beside: 360, label: 'Resize the thread' }
// the member list
export const MEMBERS: PanelSpec = { key: 'members', min: 180, max: 420, beside: 360, label: 'Resize the member list' }

export const STEP = 16, BIG_STEP = 64

export interface Limits { min: number; max: number }

/** The widths a panel may take in `room` px (the space it shares with what's beside it; 0 or less: not measured yet, so no upper bound). */
export function limitsFor(spec: PanelSpec, room: number): Limits {
  const roomMax = room > 0 ? room - spec.beside : Infinity
  return { min: spec.min, max: Math.max(spec.min, Math.min(spec.max ?? Infinity, roomMax)) }
}

/** A width within the limits, in whole pixels. Below the minimum is the minimum, even when there's no room (the panel wins over what's beside it). */
export function clampWidth(w: number, l: Limits): number {
  return Math.round(Math.max(l.min, Math.min(l.max, w)))
}

/**
 * A key on the handle (it's on the panel's left edge, so left widens the panel and right narrows it): the next width, or null for a key it
 * doesn't take. Arrows move 16 px, Shift+arrow 64; Home is the narrowest, End the widest.
 */
export function keyStep(key: string, shift: boolean, width: number, l: Limits): number | null {
  const step = shift ? BIG_STEP : STEP
  switch (key) {
    case 'ArrowLeft': return clampWidth(width + step, l)
    case 'ArrowRight': return clampWidth(width - step, l)
    case 'Home': return l.min
    case 'End': return Number.isFinite(l.max) ? l.max : null
    default: return null
  }
}

/** The width a drag gives: where it started, plus how far the pointer moved left. */
export function dragWidth(start: number, startX: number, x: number, l: Limits): number {
  return clampWidth(start + (startX - x), l)
}

const KEY = (k: string) => `cowork:panel:${k}`
function storage(): Storage | null { try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null } }
export function loadWidth(key: string): number | null {
  try { const v = Number(storage()?.getItem(KEY(key)) ?? NaN); return Number.isFinite(v) && v > 0 ? Math.round(v) : null } catch { return null }
}
export function saveWidth(key: string, w: number | null) {
  try { if (w == null) storage()?.removeItem(KEY(key)); else storage()?.setItem(KEY(key), String(Math.round(w))) } catch { /* private mode: not remembered */ }
}

/** An element's width, kept up to date (0 until it's mounted). */
export function useElementWidth(el: Ref<HTMLElement | null | undefined>): Ref<number> {
  const w = ref(0)
  const measure = () => { w.value = el.value?.getBoundingClientRect().width ?? 0 }
  const ro = typeof ResizeObserver === 'function' ? new ResizeObserver(measure) : null
  watch(el, (now, before) => { if (before) ro?.unobserve(before); if (now) ro?.observe(now); measure() }, { flush: 'post', immediate: true })
  onMounted(measure)
  onBeforeUnmount(() => ro?.disconnect())
  return w
}

export interface PanelWidth {
  spec: PanelSpec
  width: Readonly<Ref<number>>       // what the panel is now: the remembered (or dragged) width, else its default, clamped to the room
  limits: Readonly<Ref<Limits>>
  set(w: number): void               // while dragging: live, not remembered yet
  commit(): void                     // remember what it is now
  reset(): void                      // back to the default, and forget
}

/**
 * A panel's width: `room` is the space it shares with what's beside it (the container less any other panel), `fallback` its default there.
 * Remembered under the panel's key; a narrower window clamps what's shown and leaves what's remembered, so a wider one gets it back.
 */
export function usePanelWidth(spec: PanelSpec, room: () => number, fallback: (room: number) => number): PanelWidth {
  const chosen = ref<number | null>(loadWidth(spec.key))
  const limits = computed(() => limitsFor(spec, room()))
  const width = computed(() => clampWidth(chosen.value ?? fallback(room()), limits.value))
  return {
    spec, width, limits,
    set: w => { chosen.value = clampWidth(w, limits.value) },
    commit: () => { if (chosen.value != null) saveWidth(spec.key, chosen.value) },
    reset: () => { chosen.value = null; saveWidth(spec.key, null) },
  }
}
