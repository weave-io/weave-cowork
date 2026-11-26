// Artifacts (docs/build/brief-9.md, mockup A): what an agent shows with cowork_show. A card in the thread; the canvas beside the full view, in
// tabs, with versions. Pure of the browser except fetch (through api.ts), so the rules are testable.
import { reactive } from 'vue'
import { get } from './api'
import type { Post } from './types'

export interface ArtifactFile { path: string; size: number; type: string }
export interface ArtifactVersion {
  version: number; title: string; kind: ArtifactKind; entry: string; size: number; files: number; at: number
  publishedBy: number; behalf: number | null; postId: number | null; redacted: boolean
  /** a short-lived signed address of the entry file, and of its folder (for a page's own files); null once redacted */
  url: string | null; base: string | null; fileList: ArtifactFile[]
}
export interface Artifact {
  id: number; threadId: number; channelId: number; agentId: number; path: string; title: string; kind: ArtifactKind; version: number; createdAt: number
  canRedact: boolean; versions: ArtifactVersion[]
}
export type ArtifactKind = 'html' | 'markdown' | 'image' | 'svg' | 'csv' | 'json' | 'pdf' | 'file'

/** The chip on a card and a tab. */
export const kindLabel = (k: string) => ({ html: 'html', markdown: 'md', image: 'image', svg: 'svg', csv: 'csv', json: 'json', pdf: 'pdf' } as Record<string, string>)[k] ?? 'file'
/** What it is, in words, on a card ("report · v2 · 41 KB"). */
/** A page's address with the app's light or dark in its fragment (brief 13). The fragment never reaches the server; the script CoWork adds to
 * each page reads it and switches the page's own prefers-color-scheme rules, so changing the theme changes the page without a reload. */
export const withScheme = (url: string, scheme: 'dark' | 'light') => `${url.split('#')[0]}#scheme=${scheme}`

export const kindWords = (k: string) => ({ html: 'page', markdown: 'document', image: 'image', svg: 'image', csv: 'table', json: 'data', pdf: 'PDF' } as Record<string, string>)[k] ?? 'file'

export function formatSize(b: number): string {
  if (b < 1024) return `${b} B`
  if (b < 1024 * 1024) return `${Math.max(1, Math.round(b / 1024))} KB`
  return `${(b / 1024 / 1024).toFixed(1)} MB`
}

// ---- what the browser knows: fetched artifacts (their signed addresses last 30 minutes, so they're asked for again after 20) ----
const REFRESH_MS = 20 * 60 * 1000
export const artifacts = reactive({ byId: {} as Record<number, { data: Artifact | null; at: number }> })
const inflight = new Map<number, Promise<Artifact | null>>()

export function loadArtifact(id: number, force = false): Promise<Artifact | null> {
  const have = artifacts.byId[id]
  if (!force && have && Date.now() - have.at < REFRESH_MS) return Promise.resolve(have.data)
  const running = inflight.get(id)
  if (running && !force) return running
  const p = get<Artifact>(`/artifacts/${id}`)
    .then(a => { artifacts.byId[id] = { data: a, at: Date.now() }; return a })
    .catch(() => { artifacts.byId[id] = { data: null, at: Date.now() }; return null })
    .finally(() => { if (inflight.get(id) === p) inflight.delete(id) })
  inflight.set(id, p)
  return p
}
export const artifactById = (id: number | null | undefined) => (id == null ? null : artifacts.byId[id]?.data ?? null)

// ---- the canvas: tabs for one thread, each following the latest version unless a version was picked ----
export interface CanvasTab { id: number; version: number | null }
export const canvas = reactive({
  threadId: null as number | null,
  tabs: [] as CanvasTab[],
  active: null as number | null,
  full: false,
  /** the thread whose full view is on screen (where the canvas can open by itself) */
  host: null as number | null,
  /** the thread whose quick look is on screen: the canvas opening by itself moves it to the full view */
  panel: null as number | null,
  /** an artifact the quick look should open in the full view (it navigates; the full view opens the canvas from the URL) */
  request: null as { threadId: number; id: number } | null,
})

export const canvasOpenFor = (threadId: number | null | undefined) => threadId != null && canvas.threadId === threadId && canvas.tabs.length > 0

export function openInCanvas(threadId: number, id: number, version: number | null = null) {
  if (canvas.threadId !== threadId) { canvas.threadId = threadId; canvas.tabs = []; canvas.full = false }
  const tab = canvas.tabs.find(t => t.id === id)
  if (tab) tab.version = version
  else canvas.tabs.push({ id, version })
  canvas.active = id
  void loadArtifact(id)
}

export function pickVersion(id: number, version: number | null) {
  const tab = canvas.tabs.find(t => t.id === id)
  if (tab) tab.version = version
}

export function closeTab(id: number) {
  const i = canvas.tabs.findIndex(t => t.id === id)
  if (i === -1) return
  canvas.tabs.splice(i, 1)
  if (canvas.active === id) canvas.active = canvas.tabs[Math.min(i, canvas.tabs.length - 1)]?.id ?? null
  if (!canvas.tabs.length) closeCanvas()
}

export function closeCanvas() { canvas.tabs = []; canvas.active = null; canvas.full = false }

/** The version a tab shows: the one picked, or the latest that wasn't redacted. */
export function shownVersion(a: Artifact | null, tab: CanvasTab | undefined): ArtifactVersion | null {
  if (!a) return null
  if (tab?.version != null) return a.versions.find(v => v.version === tab.version) ?? null
  return a.versions.find(v => !v.redacted) ?? a.versions[0] ?? null
}

/** The canvas opens by itself only for whoever started the thread, only if they are looking at its full view, and only for a card the agent
 *  published with open "canvas" (decided 25 November 2026: everyone else sees the card). */
export function shouldAutoOpen(p: Post, meId: number, host: number | null, startedBy: number | null): boolean {
  return p.kind === 'artifact' && p.meta?.open === 'canvas' && p.threadId != null && host === p.threadId && startedBy === meId
}

/** A card arrived or changed live: the artifact is asked for again if anyone shows it (a new version), and the canvas may open by itself. */
export function artifactPostArrived(p: Post, created: boolean, meId: number, startedBy: number | null): boolean {
  if (p.kind !== 'artifact' || p.meta?.artifactId == null) return false
  const id = Number(p.meta.artifactId)
  if (artifacts.byId[id] || canvas.tabs.some(t => t.id === id)) void loadArtifact(id, true)
  if (created && shouldAutoOpen(p, meId, canvas.host, startedBy)) { openInCanvas(p.threadId!, id); return true }
  if (created && canvas.host == null && shouldAutoOpen(p, meId, canvas.panel, startedBy)) { canvas.request = { threadId: p.threadId!, id }; return true }
  return false
}

/** A link to the artifact in its thread (it opens the canvas there), for "Copy link". */
export const canvasLink = (origin: string, channelId: number, rootId: number, id: number, version?: number | null) =>
  `${origin}/c/${channelId}/threads/${rootId}?a=${id}${version != null ? `&v=${version}` : ''}`

/** The artifacts a thread's replies show, in the order they first appeared. */
export function threadArtifacts(replies: Post[]): number[] {
  const ids: number[] = []
  for (const r of replies) if (r.kind === 'artifact' && r.meta?.artifactId != null && !ids.includes(Number(r.meta.artifactId))) ids.push(Number(r.meta.artifactId))
  return ids
}

/** RFC 4180 CSV: quoted fields, doubled quotes, newlines inside quotes. At most `max` rows. */
export function parseCsv(text: string, max = 2000): { rows: string[][]; more: boolean } {
  const rows: string[][] = []
  let row: string[] = [], field = '', quoted = false, i = 0
  const s = text.replace(/^﻿/, '')
  while (i < s.length) {
    const ch = s[i]
    if (quoted) {
      if (ch === '"' && s[i + 1] === '"') { field += '"'; i += 2; continue }
      if (ch === '"') { quoted = false; i++; continue }
      field += ch; i++; continue
    }
    if (ch === '"' && field === '') { quoted = true; i++; continue }
    if (ch === ',') { row.push(field); field = ''; i++; continue }
    if (ch === '\r' || ch === '\n') {
      row.push(field); field = ''
      if (row.length > 1 || row[0] !== '') rows.push(row)
      row = []
      if (rows.length > max) return { rows: rows.slice(0, max), more: true }
      i += ch === '\r' && s[i + 1] === '\n' ? 2 : 1
      continue
    }
    field += ch; i++
  }
  if (field !== '' || row.length) { row.push(field); rows.push(row) }
  return { rows: rows.slice(0, max), more: rows.length > max }
}

/** JSON as a table when it is a list of objects (the columns are every key seen), else null. */
export function jsonTable(v: unknown): { columns: string[]; rows: unknown[][] } | null {
  const list = Array.isArray(v) ? v : v && typeof v === 'object' && Object.values(v).length === 1 && Array.isArray(Object.values(v)[0]) ? Object.values(v)[0] as unknown[] : null
  if (!list || !list.length || !list.every(x => x && typeof x === 'object' && !Array.isArray(x))) return null
  const columns: string[] = []
  for (const o of list as Record<string, unknown>[]) for (const k of Object.keys(o)) if (!columns.includes(k)) columns.push(k)
  return { columns, rows: (list as Record<string, unknown>[]).map(o => columns.map(c => o[c])) }
}
