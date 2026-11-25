import MarkdownIt from 'markdown-it'
import type StateInline from 'markdown-it/lib/rules_inline/state_inline.mjs'
import DOMPurify from 'dompurify'
import hljs from 'highlight.js/lib/core'
import bash from 'highlight.js/lib/languages/bash'
import csharp from 'highlight.js/lib/languages/csharp'
import css from 'highlight.js/lib/languages/css'
import diff from 'highlight.js/lib/languages/diff'
import go from 'highlight.js/lib/languages/go'
import javascript from 'highlight.js/lib/languages/javascript'
import json from 'highlight.js/lib/languages/json'
import markdown from 'highlight.js/lib/languages/markdown'
import plaintext from 'highlight.js/lib/languages/plaintext'
import python from 'highlight.js/lib/languages/python'
import rust from 'highlight.js/lib/languages/rust'
import sql from 'highlight.js/lib/languages/sql'
import typescript from 'highlight.js/lib/languages/typescript'
import xml from 'highlight.js/lib/languages/xml'
import yaml from 'highlight.js/lib/languages/yaml'

// GitHub-flavoured markdown in every post, as Weave Fleet renders it (markdown-it, highlight.js, DOMPurify): headings, lists, task lists,
// tables, links (in a new tab), quotes, fenced code with highlighting. No raw HTML in posts: markdown-it escapes it, and the result is sanitised
// against an allowlist as well. @mentions of known handles are marked.
for (const [names, lang] of [
  [['bash', 'sh', 'shell', 'zsh'], bash], [['csharp', 'cs', 'c#'], csharp], [['css'], css], [['diff', 'patch'], diff], [['go'], go],
  [['javascript', 'js', 'jsx', 'mjs'], javascript], [['json'], json], [['markdown', 'md'], markdown], [['plaintext', 'text', 'txt'], plaintext],
  [['python', 'py'], python], [['rust', 'rs'], rust], [['sql'], sql], [['typescript', 'ts', 'tsx'], typescript], [['html', 'xml', 'svg', 'vue'], xml],
  [['yaml', 'yml'], yaml],
] as const) for (const n of names) hljs.registerLanguage(n, lang)

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

// "- [ ] todo" and "- [x] done": a class on the item (the box is drawn in CSS), the marker text dropped. No HTML added.
const TASK = /^\[([ xX])\]\s+/
function taskLists(md: MarkdownIt) {
  md.core.ruler.after('inline', 'task-lists', state => {
    const t = state.tokens
    for (let i = 2; i < t.length; i++) {
      const inline = t[i]
      if (inline.type !== 'inline' || t[i - 1].type !== 'paragraph_open' || t[i - 2].type !== 'list_item_open') continue
      const m = TASK.exec(inline.content)
      const first = inline.children?.[0]
      if (!m || first?.type !== 'text' || !TASK.test(first.content)) continue
      first.content = first.content.replace(TASK, '')
      inline.content = inline.content.replace(TASK, '')
      t[i - 2].attrJoin('class', m[1] === ' ' ? 'task-list-item' : 'task-list-item task-list-item--done')
    }
  })
}

// @handle, for handles this server knows (passed in env), as a mention chip
function mentions(md: MarkdownIt) {
  md.inline.ruler.before('emphasis', 'mention', (state: StateInline, silent: boolean) => {
    if (state.src.charCodeAt(state.pos) !== 0x40 /* @ */) return false
    const prev = state.pos > 0 ? state.src[state.pos - 1] : ''
    if (prev && /[\w@]/.test(prev)) return false
    const m = /^@([a-zA-Z0-9][a-zA-Z0-9_-]*)/.exec(state.src.slice(state.pos))
    const handles: Set<string> | undefined = state.env?.handles
    if (!m || !handles?.has(m[1].toLowerCase())) return false
    if (!silent) {
      const open = state.push('mention_open', 'span', 1); open.attrSet('class', 'mention')
      const text = state.push('text', '', 0); text.content = m[0]
      state.push('mention_close', 'span', -1)
    }
    state.pos += m[0].length
    return true
  })
}

function create(): MarkdownIt {
  const md = new MarkdownIt({
    html: false, linkify: true, breaks: true, typographer: false,
    highlight(code, lang) {
      const l = (lang || '').toLowerCase()
      const body = l && hljs.getLanguage(l) ? hljs.highlight(code, { language: l, ignoreIllegals: true }).value : esc(code)
      return `<pre class="hljs"${l ? ` data-lang="${esc(l)}"` : ''}><code>${body}</code></pre>`
    },
  }).use(taskLists).use(mentions)
  md.linkify.set({ fuzzyLink: false, fuzzyEmail: false })   // only real addresses become links (not "Levy.io" in a sentence)
  // links open in a new tab, without telling the other site where they came from
  const link = md.renderer.rules.link_open ?? ((tokens, i, opts, _env, self) => self.renderToken(tokens, i, opts))
  md.renderer.rules.link_open = (tokens, i, opts, env, self) => {
    tokens[i].attrSet('target', '_blank'); tokens[i].attrSet('rel', 'noopener noreferrer nofollow')
    return link(tokens, i, opts, env, self)
  }
  // tables scroll sideways on their own instead of widening the post
  md.renderer.rules.table_open = () => '<div class="table-wrap"><table>'
  md.renderer.rules.table_close = () => '</table></div>'
  return md
}

let shared: MarkdownIt | null = null

const ALLOWED_TAGS = ['h1', 'h2', 'h3', 'h4', 'h5', 'h6', 'p', 'div', 'blockquote', 'pre', 'hr', 'br', 'a', 'strong', 'em', 's', 'del', 'code', 'span',
  'img', 'ul', 'ol', 'li', 'table', 'thead', 'tbody', 'tr', 'th', 'td', 'sup', 'sub']
const ALLOWED_ATTR = ['href', 'title', 'target', 'rel', 'class', 'src', 'alt', 'start', 'data-lang', 'style']

let hooked = false
function sanitize(html: string, images: boolean): string {
  if (typeof window === 'undefined' || !DOMPurify.isSupported) return html       // markdown-it's own escaping (html: false) still holds
  if (!hooked) {
    hooked = true
    DOMPurify.addHook('afterSanitizeAttributes', node => {
      if (node.tagName === 'A' && node.getAttribute('href')) { node.setAttribute('target', '_blank'); node.setAttribute('rel', 'noopener noreferrer nofollow') }
      // only table cells keep a style, and only their alignment
      if (node.hasAttribute('style')) {
        const align = /^text-align:\s*(left|right|center)$/.exec(node.getAttribute('style') ?? '')
        if ((node.tagName === 'TD' || node.tagName === 'TH') && align) node.setAttribute('style', `text-align:${align[1]}`)
        else node.removeAttribute('style')
      }
    })
  }
  return String(DOMPurify.sanitize(html, {
    ALLOWED_TAGS: images ? ALLOWED_TAGS : ALLOWED_TAGS.filter(t => t !== 'img'), ALLOWED_ATTR,
    ALLOWED_URI_REGEXP: /^(?:(?:https?|mailto):|[^a-z]|[a-z+.-]+(?:[^a-z+.\-:]|$))/i, ADD_ATTR: ['target'],
  }))
}

/** A post's text as safe HTML: GitHub-flavoured markdown, @mentions of known handles marked, no raw HTML. Images in posts stay links. */
export function renderBody(text: string, handles: Set<string> = new Set()): string {
  shared ??= create()
  const html = shared.render(text, { handles })
  return sanitize(images(html, false), false)
}

/** A markdown document (an artifact): the same rules, with its images loaded from the artifact's own signed address. */
export function renderDocument(text: string, base: string | null, handles: Set<string> = new Set()): string {
  shared ??= create()
  let html = shared.render(text, { handles })
  if (base) html = html.replace(/<img src="([^"]+)"/g, (m, src: string) => /^[a-z][a-z0-9+.-]*:|^\/|^#/i.test(src) ? m : `<img src="${base}${src.replace(/^\.\//, '')}"`)
  return sanitize(html, true)
}

/** In a post, an image is a link to it (a post never loads anything from elsewhere by itself). */
function images(html: string, keep: boolean): string {
  if (keep) return html
  return html.replace(/<img src="([^"]*)" alt="([^"]*)"[^>]*>/g, (_m, src: string, alt: string) =>
    `<a href="${src}" target="_blank" rel="noopener noreferrer nofollow">${alt || src}</a>`)
}

/** The words of a post without its markdown, for a one-line preview. */
export function plainText(text: string): string {
  return text.replace(/```[\s\S]*?```/g, ' ').replace(/`([^`]*)`/g, '$1').replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1').replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/^\s{0,3}(#{1,6}|>|[-*+]|\d+\.)\s+/gm, '').replace(/[*_~]{1,3}([^*_~]+)[*_~]{1,3}/g, '$1').replace(/\|/g, ' ').replace(/\s+/g, ' ').trim()
}
