const v = (n) => `var(--${n})`
/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{vue,ts}'],
  theme: {
    extend: {
      colors: {
        bg: v('bg'), rail: v('rail'), side: v('side'), panel: v('panel'), card: v('card'), raised: v('raised'),
        line: v('border'), ink: v('text'), muted: v('muted'), faint: v('faint'),
        accent: v('accent'), 'accent-ink': v('accent-text'), 'accent-dim': v('accent-dim'),
        online: v('online'), 'online-ink': v('online-text'), away: v('away'), 'away-ink': v('away-text'), offline: v('offline'),
        danger: v('danger'), 'danger-ink': v('danger-text'), hover: v('hover'),
      },
      fontFamily: { sans: ['var(--sans)'], mono: ['var(--mono)'] },
      borderRadius: { btn: '8px', card: '10px', panel: '12px' },
    },
  },
}
