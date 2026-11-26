import { computed, ref } from 'vue'

// Fleet's themes (same ids and names as Weave Fleet's client/src/stores/theme.ts). "System" follows the operating system
// and resolves to dark or light; the choice is remembered per browser.
export type ThemeId = 'dark' | 'light' | 'weave-classic' | 'black' | 'nord' | 'dracula' | 'solarized-dark' | 'solarized-light' | 'monokai' | 'github-dark'
export type ThemeSelection = ThemeId | 'system'

export const themes: readonly { id: ThemeId; label: string; colorScheme: 'dark' | 'light'; swatches: readonly [string, string, string, string] }[] = [
  { id: 'dark', label: 'Default dark', colorScheme: 'dark', swatches: ['#0d0d10', '#1b1b20', '#6366f1', '#e8e8ec'] },
  { id: 'light', label: 'Light', colorScheme: 'light', swatches: ['#F3F2EF', '#ffffff', '#5B6EC7', '#1A1918'] },
  { id: 'weave-classic', label: 'Weave Classic', colorScheme: 'dark', swatches: ['#0F172A', '#1E293B', '#A855F7', '#F8FAFC'] },
  { id: 'black', label: 'Black (OLED)', colorScheme: 'dark', swatches: ['#000000', '#0A0A0A', '#A855F7', '#FAFAFA'] },
  { id: 'nord', label: 'Nord', colorScheme: 'dark', swatches: ['#2E3440', '#3B4252', '#88C0D0', '#ECEFF4'] },
  { id: 'dracula', label: 'Dracula', colorScheme: 'dark', swatches: ['#282A36', '#343746', '#BD93F9', '#F8F8F2'] },
  { id: 'solarized-dark', label: 'Solarized Dark', colorScheme: 'dark', swatches: ['#002B36', '#073642', '#268BD2', '#FDF6E3'] },
  { id: 'solarized-light', label: 'Solarized Light', colorScheme: 'light', swatches: ['#FDF6E3', '#EEE8D5', '#268BD2', '#073642'] },
  { id: 'monokai', label: 'Monokai', colorScheme: 'dark', swatches: ['#272822', '#3E3D32', '#A6E22E', '#F8F8F2'] },
  { id: 'github-dark', label: 'GitHub Dark', colorScheme: 'dark', swatches: ['#0D1117', '#161B22', '#58A6FF', '#E6EDF3'] },
]

const KEY = 'weave:theme'
const valid = (v: string | null): v is ThemeId => themes.some(t => t.id === v)
function read(): ThemeSelection {
  try { const raw = localStorage.getItem(KEY); return raw === 'system' || valid(raw) ? raw : 'dark' } catch { return 'dark' }
}

export const selection = ref<ThemeSelection>(read())
const systemDark = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : null
const systemTick = ref(systemDark?.matches ?? true)
systemDark?.addEventListener('change', e => { systemTick.value = e.matches; apply() })

export const resolved = computed<ThemeId>(() => selection.value === 'system' ? (systemTick.value ? 'dark' : 'light') : selection.value)
/** Light or dark, whichever theme is chosen: what an agent's page in the canvas follows (brief 12). */
export const scheme = computed<'dark' | 'light'>(() => themes.find(t => t.id === resolved.value)?.colorScheme ?? 'dark')

function apply() {
  const t = themes.find(x => x.id === resolved.value)!
  document.documentElement.dataset.theme = t.id
  document.documentElement.style.colorScheme = t.colorScheme
}
export function setTheme(s: ThemeSelection) {
  selection.value = s
  try { localStorage.setItem(KEY, s) } catch { /* private mode */ }
  apply()
}
export const initTheme = apply
