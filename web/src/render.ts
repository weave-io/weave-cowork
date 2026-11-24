const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** Safe, tiny markdown: fenced code, inline code, bold, @mentions, line breaks. Everything is escaped first. */
export function renderBody(text: string, handles: Set<string> = new Set()): string {
  const parts = text.split(/```/)
  return parts.map((part, i) => {
    if (i % 2 === 1) {
      const nl = part.indexOf('\n')
      const code = nl >= 0 && /^[\w+-]*$/.test(part.slice(0, nl).trim()) ? part.slice(nl + 1) : part
      return `<pre><code>${esc(code.replace(/\n$/, ''))}</code></pre>`
    }
    return esc(part)
      .replace(/`([^`\n]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>')
      .replace(/(^|[^\w@])@([a-zA-Z0-9][a-zA-Z0-9_-]*)/g, (m, pre, h) =>
        handles.has(h.toLowerCase()) ? `${pre}<span class="mention">@${h}</span>` : m)
      .split(/\n{2,}/).map(par => `<p>${par.replace(/\n/g, '<br>')}</p>`).join('')
  }).join('')
}
