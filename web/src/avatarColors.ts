// A member's colour is stable: their id picks one from this palette. White initials sit on it, so each colour reaches 4.5:1
// against white (theme-contrast.test.ts checks it); the avatar's fill doesn't change with the theme, so neither does the ratio.
// Ordered so the first members (the server's founder, the next person, the first agent) are far apart in hue.
export const avatarColors = ['#4F46E5', '#0F766E', '#C2410C', '#BE185D', '#1D4ED8', '#7E22CE', '#4D7C0F', '#B91C1C'] as const
export const avatarColor = (id: number) => avatarColors[Math.abs(id) % avatarColors.length]
