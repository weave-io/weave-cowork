import { ref } from 'vue'

// Phone width: the channel list and the member list move behind buttons (Fleet's phone breakpoint is 768 px).
const q = typeof matchMedia === 'function' ? matchMedia('(max-width: 767px)') : null
export const isPhone = ref(q?.matches ?? false)
q?.addEventListener('change', e => { isPhone.value = e.matches; if (!e.matches) { navOpen.value = false; membersOpen.value = false } })
export const navOpen = ref(false)
export const membersOpen = ref(false)
// Wide enough to show the member list beside an open thread.
const w = typeof matchMedia === 'function' ? matchMedia('(min-width: 1280px)') : null
export const isWide = ref(w?.matches ?? true)
w?.addEventListener('change', e => { isWide.value = e.matches })
