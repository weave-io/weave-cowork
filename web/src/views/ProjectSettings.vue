<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { GitBranch, Folder, Plus, Settings2, Hash, Volume2, Lock, X, Menu, ShieldAlert } from 'lucide-vue-next'
import { get, post, put, del, ApiError } from '../api'
import { store, memberById, openModal } from '../store'
import { isPhone, navOpen } from '../layout'
import MemberAvatar from '../components/MemberAvatar.vue'
import StatusDot from '../components/StatusDot.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { confirmAction } from '@/components/ui/confirm'

// A project's settings: what isn't conversation. Its workspaces (and where agents cloned them), its people with their roles, its agents with
// where they run, who they take work from and who they ask, and the rules in force. Leads and admins change it.
interface Settings {
  id: number; name: string; about: string; role: 'lead' | 'member' | null; canEdit: boolean
  workspaces: { id: number; name: string; kind: string; url: string | null; branch: string | null; path: string | null; host: string | null; addedBy: string; places: { host: string | null; location: string | null; agent: string }[]; cantReach: string[] }[]
  people: { id: number; role: 'lead' | 'member' }[]
  agents: { id: number; handle: string; displayName: string; ownerId: number; ownerName: string; runnerHost: string | null; online: boolean; takesWorkFrom: 'owner' | 'members'; sandboxed: boolean; canChange: boolean
    placement?: 'runner' | 'pool'; pools?: string[] | null }[]
  channels: { id: number; name: string; kind: string; isPrivate: boolean }[]
  rules: string[]
}
const props = defineProps<{ projectId: number }>()
const s = ref<Settings | null>(null), error = ref(''), notFound = ref(false)
async function load() {
  try { s.value = await get(`/projects/${props.projectId}`); notFound.value = false }
  catch (e) { if (e instanceof ApiError && e.status === 404) notFound.value = true; else error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
onMounted(load)
// someone changed the server (a person, an agent, a channel or this project): read it again
watch(() => [store.projects, store.channels, store.members], load)

const leadNames = computed(() => (s.value?.people ?? []).filter(p => p.role === 'lead').map(p => memberById(p.id)?.displayName).filter(Boolean).join(' and '))
const roleLine = computed(() => s.value?.role === 'lead' ? 'you lead it' : s.value?.role === 'member' ? "you're a member" : store.isAdmin ? "you're an admin" : '')
const parts = (t: string) => t.split('`').map((text, i) => ({ text, code: i % 2 === 1 }))
const hosts = (w: Settings['workspaces'][number]) => [...new Set(w.places.map(p => p.host).filter(Boolean))] as string[]

async function guard(fn: () => Promise<unknown>) {
  error.value = ''
  try { await fn(); await load() } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
}
// people
const others = computed(() => store.members.filter(m => m.kind === 'person' && !m.removed && !s.value?.people.some(p => p.id === m.id)))
const addPersonId = ref('')
const addPerson = (v: unknown) => guard(async () => { if (v) await put(`/projects/${props.projectId}/members/${v}`, { role: 'member' }); addPersonId.value = '' })
const removePerson = (id: number) => guard(() => del(`/projects/${props.projectId}/members/${id}`))
// agents
const setGrant = (agentId: number, v: unknown) => guard(() => put(`/projects/${props.projectId}/agents/${agentId}`, { takesWorkFrom: v }))
async function removeAgent(a: Settings['agents'][number]) {
  if (!(await confirmAction({ title: `Take ${a.handle} out of ${s.value?.name}?`, body: 'It stops taking work in this project’s channels. Its threads stay.', confirmLabel: 'Take it out', destructive: true }))) return
  await guard(() => del(`/projects/${props.projectId}/agents/${a.id}`))
}
// workspaces
const adding = ref(false), wsKind = ref<'repository' | 'folder'>('repository'), wsName = ref(''), wsUrl = ref(''), wsBranch = ref('main'), wsPath = ref(''), wsHost = ref('')
const addWorkspace = () => guard(async () => {
  await post(`/projects/${props.projectId}/workspaces`, wsKind.value === 'repository'
    ? { kind: 'repository', name: wsName.value || 'app', url: wsUrl.value, branch: wsBranch.value }
    : { kind: 'folder', name: wsName.value || 'folder', path: wsPath.value, host: wsHost.value })
  adding.value = false; wsName.value = ''; wsUrl.value = ''; wsPath.value = ''; wsHost.value = ''
})
async function removeWorkspace(w: Settings['workspaces'][number]) {
  if (!(await confirmAction({ title: `Remove ${w.name}?`, body: 'New threads won’t work on it. Clones and worktrees on runners stay where they are.', confirmLabel: 'Remove', destructive: true }))) return
  await guard(() => del(`/projects/${props.projectId}/workspaces/${w.id}`))
}
</script>
<template>
  <section class="flex min-h-0 min-w-0 flex-col" aria-label="Project settings" data-testid="project-settings-page">
    <header class="flex h-[52px] shrink-0 items-center gap-2.5 border-b border-border px-3 sm:px-4">
      <Button v-if="isPhone" variant="toolbar-icon" size="toolbar-lg" aria-label="Open menu" @click="navOpen = true"><Menu :size="18" /></Button>
      <Settings2 :size="16" class="text-muted" aria-hidden="true" />
      <h2 class="m-0 text-[15px] font-semibold whitespace-nowrap" data-testid="project-title">{{ s?.name ?? '' }}</h2>
      <span v-if="s" class="truncate border-l border-border pl-2.5 text-[13px] text-muted" data-testid="project-role">Project settings<template v-if="roleLine"> · {{ roleLine }}</template></span>
    </header>

    <div class="thin-scrollbar min-h-0 flex-1 overflow-auto px-3 py-4 sm:px-4">
      <p v-if="notFound" class="text-[13px] text-muted">This project doesn't exist, or you aren't in it.</p>
      <Spinner v-else-if="!s" />
      <div v-else class="mx-auto flex max-w-[880px] flex-col gap-4">
        <p v-if="s.about" class="m-0 text-[13.5px] text-muted" data-testid="project-about">{{ s.about }}</p>
        <ErrorNote v-if="error" :message="error" />

        <!-- workspaces -->
        <div class="card" data-testid="workspaces">
          <div class="card-head"><h3>Workspaces</h3><span class="desc">What agents work on. A thread gets its own worktree and branch.</span>
            <Button v-if="s.canEdit && !adding" variant="outline" size="sm" class="ml-auto" data-testid="add-workspace" @click="adding = true"><Plus :size="14" />Add</Button></div>
          <p v-if="!s.workspaces.length && !adding" class="row text-[13px] text-muted">No workspace: each thread gets an empty folder on its agent's machine.</p>
          <div v-for="w in s.workspaces" :key="w.id" class="row flex items-start gap-3" data-testid="workspace" :data-kind="w.kind">
            <span class="icon"><GitBranch v-if="w.kind === 'repository'" :size="15" /><Folder v-else :size="15" /></span>
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-1.5 text-[13.5px]"><b>{{ w.name }}</b>
                <template v-if="w.kind === 'repository'"><code class="code">{{ w.url }}</code>·<code class="code">{{ w.branch }}</code></template>
                <template v-else><code class="code">{{ w.path }}</code> on {{ w.host }}</template>
              </div>
              <p class="m-0 text-xs text-muted" v-if="w.kind === 'repository'">Repository · agents write to <code class="code">cowork/&lt;thread&gt;</code> branches, never to {{ w.branch }} · added by {{ w.addedBy }}</p>
              <p class="m-0 text-xs text-muted" v-else>Folder on a machine · only agents whose runner is on {{ w.host }} can use it · added by {{ w.addedBy }}</p>
              <p v-for="(p, i) in w.places" :key="i" class="m-0 truncate text-xs text-muted" data-testid="workspace-place" :title="p.location ?? ''">{{ w.kind === 'repository' ? 'Cloned' : 'Used' }} by {{ p.agent }} on {{ p.host ?? 'its runner' }}: <code class="code">{{ p.location }}</code></p>
            </div>
            <span class="flex shrink-0 flex-col items-end gap-1">
              <span class="pill" data-testid="workspace-cloned">{{ hosts(w).length ? `${w.kind === 'repository' ? 'cloned' : 'used'} on ${hosts(w).join(', ')}` : w.kind === 'repository' ? 'not cloned yet' : 'not used yet' }}</span>
              <span v-if="w.cantReach.length" class="pill warn" data-testid="workspace-cant-reach">{{ w.cantReach.length }} agent{{ w.cantReach.length === 1 ? '' : 's' }} can't reach it</span>
            </span>
            <Button v-if="s.canEdit" variant="toolbar-icon-danger" size="toolbar" :aria-label="`Remove ${w.name}`" @click="removeWorkspace(w)"><X :size="14" /></Button>
          </div>
          <form v-if="adding" class="row flex flex-col gap-2" data-testid="workspace-form" @submit.prevent="addWorkspace">
            <div class="flex gap-1.5">
              <Button type="button" size="sm" :variant="wsKind === 'repository' ? 'secondary' : 'ghost'" @click="wsKind = 'repository'"><GitBranch :size="14" />Repository</Button>
              <Button type="button" size="sm" :variant="wsKind === 'folder' ? 'secondary' : 'ghost'" @click="wsKind = 'folder'"><Folder :size="14" />Folder</Button>
            </div>
            <div class="grid grid-cols-[140px_1fr_160px] gap-2 max-sm:grid-cols-1">
              <Input v-model="wsName" placeholder="Name (app)" aria-label="Name" class="text-sm" />
              <template v-if="wsKind === 'repository'"><Input v-model="wsUrl" placeholder="git@github.com:lumen/checkout-app.git" aria-label="Repository" class="font-mono text-[12.5px]" /><Input v-model="wsBranch" aria-label="Branch" class="font-mono text-[12.5px]" /></template>
              <template v-else><Input v-model="wsPath" placeholder="/home/alex/shared/specs" aria-label="Folder" class="font-mono text-[12.5px]" /><Input v-model="wsHost" placeholder="build-01" aria-label="On" class="text-sm" /></template>
            </div>
            <div class="flex justify-end gap-2"><Button type="button" variant="outline" size="sm" @click="adding = false">Cancel</Button><Button type="submit" size="sm">Add workspace</Button></div>
          </form>
        </div>

        <!-- people -->
        <div class="card" data-testid="project-people">
          <div class="card-head"><h3>People</h3><span class="desc">{{ s.people.length }}</span>
            <Select v-if="s.canEdit && others.length" :model-value="addPersonId" @update:model-value="addPerson">
              <SelectTrigger class="ml-auto h-8 w-auto gap-1 text-[13px]" aria-label="Add people" data-testid="project-add-person"><SelectValue placeholder="Add people…" /></SelectTrigger>
              <SelectContent><SelectItem v-for="m in others" :key="m.id" :value="String(m.id)">{{ m.displayName }} (@{{ m.handle }})</SelectItem></SelectContent>
            </Select>
          </div>
          <div class="row flex flex-wrap gap-1.5">
            <span v-for="p in s.people" :key="p.id" class="flex items-center gap-1.5 rounded-full border border-border py-0.5 pr-2 pl-1 text-[13px]" data-testid="person-chip" :data-person="memberById(p.id)?.handle" :data-role="p.role">
              <MemberAvatar :member="memberById(p.id)" size="xs" />{{ memberById(p.id)?.displayName }}<span class="text-xs text-muted">{{ p.role }}</span>
              <button v-if="s.canEdit && p.id !== store.meId" type="button" class="grid size-4 place-items-center rounded-full text-muted hover:bg-text/10" :aria-label="`Remove ${memberById(p.id)?.displayName}`" @click="removePerson(p.id)"><X :size="11" /></button>
            </span>
          </div>
        </div>

        <!-- agents -->
        <div class="card" data-testid="project-agents">
          <div class="card-head"><h3>Agents</h3><span class="desc">Who works here, where, and who answers for them</span>
            <Button v-if="s.canEdit" variant="outline" size="sm" class="ml-auto" data-testid="add-agent" @click="openModal('add-agent-to-project', { projectId: s.id })"><Plus :size="14" />Add an agent</Button></div>
          <p v-if="!s.agents.length" class="row text-[13px] text-muted">No agents yet. Add one to see what it may do here before it joins.</p>
          <div v-else class="overflow-x-auto">
            <table class="w-full border-collapse text-[13px]">
              <thead><tr class="text-left text-xs text-muted"><th>Agent</th><th>Runs</th><th>Takes work from</th><th>Asks</th><th>Sandbox</th><th /></tr></thead>
              <tbody>
                <tr v-for="a in s.agents" :key="a.id" :data-agent-row="a.handle">
                  <td><span class="flex items-center gap-2"><MemberAvatar :member="memberById(a.id)" size="xs" status /><b class="font-medium">{{ a.handle }}</b><span class="text-xs text-muted">{{ a.placement === 'pool' ? 'team template' : `${a.ownerName}'s own` }}</span></span></td>
                  <td><span class="flex items-center gap-1.5" data-testid="agent-runs"><StatusDot :state="a.online ? 'online' : 'offline'" />{{ a.placement === 'pool' ? `${a.pools?.length ? a.pools.join(', ') : 'a pool'}, a sandbox per thread` : a.runnerHost ? `${a.runnerHost}, pinned` : 'not connected yet' }}</span></td>
                  <td>
                    <Select v-if="a.canChange" :model-value="a.takesWorkFrom" @update:model-value="setGrant(a.id, $event)">
                      <SelectTrigger class="h-7 w-auto gap-1 text-[13px]" :aria-label="`${a.handle} takes work from`" :data-testid="`grant-${a.handle}`"><SelectValue /></SelectTrigger>
                      <SelectContent><SelectItem value="members" data-grant="members">Members</SelectItem><SelectItem value="owner" data-grant="owner">Only {{ a.ownerName }}</SelectItem></SelectContent>
                    </Select>
                    <span v-else>{{ a.takesWorkFrom === 'owner' ? `Only ${a.ownerName}` : 'Members' }}</span>
                  </td>
                  <td>{{ a.placement === 'pool' ? `Whoever started the thread, then ${leadNames || 'a lead'}` : a.ownerName }}</td>
                  <td><span class="pill" :class="!a.sandboxed && 'warn'" data-testid="sandbox-pill"><ShieldAlert v-if="!a.sandboxed" :size="11" />{{ a.sandboxed ? 'sandboxed' : 'unsandboxed' }}</span></td>
                  <td class="text-right"><Button v-if="a.canChange" variant="toolbar-icon-danger" size="toolbar" :aria-label="`Take ${a.handle} out`" @click="removeAgent(a)"><X :size="14" /></Button></td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>

        <!-- rules -->
        <div class="card" data-testid="project-rules">
          <div class="card-head"><h3>Rules</h3><span class="desc">The same on every runner</span></div>
          <ul class="row m-0 flex list-none flex-col gap-1.5 text-[13px]">
            <li v-for="(r, i) in s.rules" :key="i" class="flex gap-2" data-testid="rule"><span class="mt-[7px] size-1.5 shrink-0 rounded-full bg-muted" /><span><template v-for="(p, j) in parts(r)" :key="j"><code v-if="p.code" class="code">{{ p.text }}</code><template v-else>{{ p.text }}</template></template></span></li>
          </ul>
        </div>

        <!-- channels -->
        <div class="card" data-testid="project-channels">
          <div class="card-head"><h3>Channels</h3><span class="desc">Its members see the public ones</span>
            <Button variant="outline" size="sm" class="ml-auto" @click="openModal('create-channel', { projectId: s.id })"><Plus :size="14" />Create a channel</Button></div>
          <div class="row flex flex-wrap gap-x-4 gap-y-1 text-[13.5px]">
            <span v-for="c in s.channels" :key="c.id" class="flex items-center gap-1"><Volume2 v-if="c.kind === 'voice'" :size="14" class="text-muted" /><Lock v-else-if="c.isPrivate" :size="14" class="text-muted" /><Hash v-else :size="14" class="text-muted" />{{ c.name }}</span>
          </div>
        </div>
      </div>
    </div>
  </section>
</template>
<style scoped>
.card { border: 1px solid var(--border); border-radius: var(--radius-panel); background: var(--panel-bg); overflow: hidden; }
.card-head { display: flex; align-items: center; gap: 10px; padding: 10px 14px; border-bottom: 1px solid var(--border); }
.card-head h3 { margin: 0; font-size: 14px; font-weight: 600; }
.card-head .desc { font-size: 12.5px; color: var(--muted); }
.row { padding: 10px 14px; }
.row + .row { border-top: 1px solid var(--border); }
.icon { display: grid; place-items: center; width: 30px; height: 30px; border: 1px solid var(--border); border-radius: var(--radius-btn); color: var(--muted); flex-shrink: 0; }
.code { font-family: var(--font-mono-stack); font-size: 12px; padding: 0 4px; border: 1px solid var(--border); border-radius: 4px; background: var(--card-bg); }
.pill { display: inline-flex; align-items: center; gap: 4px; padding: 1px 8px; border-radius: 999px; font-size: 11.5px; font-weight: 500; color: var(--muted); background: color-mix(in srgb, var(--text) 6%, transparent); white-space: nowrap; }
.pill.warn { color: var(--idle-text); background: color-mix(in srgb, var(--idle) 14%, transparent); }
table th { padding: 8px 14px; font-weight: 500; border-bottom: 1px solid var(--border); }
table td { padding: 8px 14px; border-bottom: 1px solid var(--border); vertical-align: middle; }
table tr:last-child td { border-bottom: 0; }
</style>
