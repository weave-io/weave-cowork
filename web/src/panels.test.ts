// @vitest-environment jsdom
import { beforeEach, describe, expect, it } from 'vitest'
import { createApp, defineComponent, h, nextTick, ref } from 'vue'
import { CANVAS, MEMBERS, THREAD_PANEL, clampWidth, dragWidth, keyStep, limitsFor, loadWidth, saveWidth, usePanelWidth, type PanelWidth } from './panels'
import ResizeHandle from './components/ui/resize-handle/ResizeHandle.vue'

// brief 10: panels you can size
describe('panel widths', () => {
  beforeEach(() => localStorage.clear())

  it('keeps the canvas at 320 or more and the thread beside it at 360 or more', () => {
    const l = limitsFor(CANVAS, 1200)
    expect(l).toEqual({ min: 320, max: 840 })
    expect(clampWidth(100, l)).toBe(320)
    expect(clampWidth(2000, l)).toBe(840)
    expect(clampWidth(500.4, l)).toBe(500)
    // no room for both: the canvas keeps its minimum
    expect(limitsFor(CANVAS, 500)).toEqual({ min: 320, max: 320 })
    // not measured yet: no upper bound but the panel's own
    expect(limitsFor(CANVAS, 0).max).toBe(Infinity)
    expect(limitsFor(MEMBERS, 0).max).toBe(420)
    expect(limitsFor(MEMBERS, 2000).max).toBe(420)
    expect(limitsFor(THREAD_PANEL, 900)).toEqual({ min: 320, max: 540 })
  })

  it('moves 16 px a key, 64 with Shift, Home and End to the limits; left widens, as the handle is on the left edge', () => {
    const l = { min: 320, max: 840 }
    expect(keyStep('ArrowLeft', false, 500, l)).toBe(516)
    expect(keyStep('ArrowRight', false, 500, l)).toBe(484)
    expect(keyStep('ArrowLeft', true, 500, l)).toBe(564)
    expect(keyStep('ArrowRight', true, 500, l)).toBe(436)
    expect(keyStep('ArrowRight', true, 350, l)).toBe(320)
    expect(keyStep('ArrowLeft', true, 830, l)).toBe(840)
    expect(keyStep('Home', false, 500, l)).toBe(320)
    expect(keyStep('End', false, 500, l)).toBe(840)
    expect(keyStep('End', false, 500, { min: 320, max: Infinity })).toBeNull()
    expect(keyStep('a', false, 500, l)).toBeNull()
  })

  it('drags by how far the pointer moved left, within the limits', () => {
    const l = { min: 320, max: 840 }
    expect(dragWidth(500, 700, 600, l)).toBe(600)
    expect(dragWidth(500, 700, 900, l)).toBe(320)
    expect(dragWidth(500, 700, 0, l)).toBe(840)
  })

  it('remembers a width per panel, clamps it to a narrower room without forgetting it, and resets to the default', () => {
    saveWidth('canvas', 700)
    expect(loadWidth('canvas')).toBe(700)
    expect(loadWidth('members')).toBeNull()
    localStorage.setItem('cowork:panel:members', 'nonsense')
    expect(loadWidth('members')).toBeNull()

    const room = ref(1400)
    const p = usePanelWidth(CANVAS, () => room.value, r => Math.min(860, Math.max(420, r * 0.48)))
    expect(p.width.value).toBe(700)
    room.value = 900                       // a narrower window: shown clamped, still remembered
    expect(p.width.value).toBe(540)
    expect(loadWidth('canvas')).toBe(700)
    room.value = 1400
    expect(p.width.value).toBe(700)
    p.set(760)                             // dragging: live, remembered when it ends
    expect(p.width.value).toBe(760)
    expect(loadWidth('canvas')).toBe(700)
    p.commit()
    expect(loadWidth('canvas')).toBe(760)
    p.reset()                              // the default: 48% of the room, 420 to 860
    expect(p.width.value).toBe(672)
    expect(loadWidth('canvas')).toBeNull()
    // each panel has its own
    const m = usePanelWidth(MEMBERS, () => 1400, () => 244)
    expect(m.width.value).toBe(244)
  })
})

describe('the resize handle', () => {
  beforeEach(() => localStorage.clear())

  async function mount(room = 1200) {
    let panel!: PanelWidth
    const host = document.createElement('div'); document.body.appendChild(host)
    const app = createApp(defineComponent({ setup() { panel = usePanelWidth(CANVAS, () => room, () => 500); return () => h(ResizeHandle, { panel }) } }))
    app.mount(host)
    await nextTick()
    const sep = host.querySelector<HTMLElement>('[role=separator]')!
    return { sep, panel: () => panel, unmount: () => { app.unmount(); host.remove() } }
  }
  const key = (el: HTMLElement, k: string, shiftKey = false) => el.dispatchEvent(new KeyboardEvent('keydown', { key: k, shiftKey, bubbles: true, cancelable: true }))

  it('is a focusable separator that says its width and limits, and takes the keyboard', async () => {
    const { sep, unmount } = await mount()
    expect(sep.getAttribute('tabindex')).toBe('0')
    expect(sep.getAttribute('aria-orientation')).toBe('vertical')
    expect(sep.getAttribute('aria-label')).toBe('Resize the canvas')
    expect([sep.getAttribute('aria-valuenow'), sep.getAttribute('aria-valuemin'), sep.getAttribute('aria-valuemax')]).toEqual(['500', '320', '840'])
    key(sep, 'ArrowLeft'); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('516')
    key(sep, 'ArrowRight', true); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('452')
    expect(loadWidth('canvas')).toBe(452)
    key(sep, 'End'); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('840')
    key(sep, 'Home'); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('320')
    sep.dispatchEvent(new MouseEvent('dblclick', { bubbles: true })); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('500')
    expect(loadWidth('canvas')).toBeNull()
    unmount()
  })

  it('drags with a cover over the window, remembers the width when it ends, and Esc puts it back', async () => {
    const { sep, unmount } = await mount()
    const pointer = (type: string, clientX: number) => sep.dispatchEvent(new MouseEvent(type, { clientX, button: 0, bubbles: true, cancelable: true }))
    pointer('pointerdown', 700); await nextTick()
    expect(document.querySelector('[data-testid=resize-cover]')).not.toBeNull()
    expect(document.documentElement.classList.contains('panel-resizing')).toBe(true)
    pointer('pointermove', 600); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('600')
    expect(loadWidth('canvas')).toBeNull()
    pointer('pointerup', 600); await nextTick()
    expect(loadWidth('canvas')).toBe(600)
    expect(document.querySelector('[data-testid=resize-cover]')).toBeNull()
    expect(document.documentElement.classList.contains('panel-resizing')).toBe(false)

    pointer('pointerdown', 600); pointer('pointermove', 300); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('840')
    key(sep, 'Escape'); await nextTick()
    expect(sep.getAttribute('aria-valuenow')).toBe('600')
    expect(loadWidth('canvas')).toBe(600)
    unmount()
  })
})
