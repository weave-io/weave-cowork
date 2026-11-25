<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { GitBranch, Folder, NotebookText, Hash, Volume2, KeyRound, X } from 'lucide-vue-next'
import { post, ApiError } from '../../api'
import { store, me, closeModal, loadBootstrap } from '../../store'
import FormField from '../FormField.vue'
import MemberAvatar from '../MemberAvatar.vue'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

// A project holds channels and owns what isn't conversation: its people, a workspace for agents to work on, and later its sources and budget.
const router = useRouter()
const name = ref(''), about = ref('')
const start = ref<'repository' | 'folder' | 'none'>('repository')
const url = ref(''), branch = ref('main'), path = ref(''), host = ref('')
const error = ref(''), busy = ref(false)

/** "Checkout app" → checkout-app, checkout-standup (voice), checkout-alerts: the channels a project usually starts with */
const slug = computed(() => name.value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40))
const short = computed(() => slug.value.split('-')[0] || slug.value)
const suggested = computed(() => slug.value ? [
  { name: slug.value, kind: 'text' as const },
  { name: `${short.value}-standup`, kind: 'voice' as const },
  { name: `${short.value}-alerts`, kind: 'text' as const },
] : [])
const picked = ref<boolean[]>([true, true, false])
const channels = computed(() => suggested.value.filter((_, i) => picked.value[i]))

const people = ref<number[]>([])
const others = computed(() => store.members.filter(m => m.kind === 'person' && !m.removed && m.id !== store.meId && !people.value.includes(m.id)))
const addId = ref('')
function addPerson(v: unknown) { if (v) people.value.push(Number(v)); addId.value = '' }
const hosts = computed(() => [...new Set(store.members.filter(m => m.kind === 'agent' && m.runnerHost).map(m => m.runnerHost as string))])

const ready = computed(() => name.value.trim() && channels.value.length > 0 && (start.value !== 'repository' || url.value.trim()) && (start.value !== 'folder' || (path.value.trim() && host.value.trim())))

async function create() {
  error.value = ''; busy.value = true
  try {
    const workspace = start.value === 'repository' ? { kind: 'repository', name: 'app', url: url.value, branch: branch.value }
      : start.value === 'folder' ? { kind: 'folder', name: path.value.trim().split('/').filter(Boolean).pop() ?? 'folder', path: path.value, host: host.value } : { kind: 'none' }
    const { id } = await post('/projects', { name: name.value, about: about.value, workspace, memberIds: people.value, channels: channels.value })
    await loadBootstrap(); closeModal(); router.push(`/p/${id}`)
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
const starts = [
  { id: 'repository', icon: GitBranch, title: 'A repository', sub: 'Any agent can work on it, anywhere' },
  { id: 'folder', icon: Folder, title: 'A folder', sub: 'On one machine with a runner' },
  { id: 'none', icon: NotebookText, title: 'Nothing yet', sub: 'Channels only, for now' },
] as const
</script>
<template>
  <form @submit.prevent="create" data-testid="new-project-form">
    <div class="grid grid-cols-[1fr_200px] gap-3 max-sm:grid-cols-1">
      <FormField name="projectName" label="Name" v-model="name" placeholder="Checkout app" />
      <div class="mb-3"><span class="mb-1 block text-xs font-medium text-muted">Lead</span><div class="flex h-9 items-center rounded-btn border border-border bg-card-bg px-3 text-sm">{{ me?.displayName }} (you)</div></div>
    </div>
    <FormField name="about" label="What it's about" v-model="about" placeholder="Cart, payment and order confirmation" />

    <div class="mt-1 border-t border-border pt-3">
      <span class="mb-2 block text-xs font-medium text-muted">Start from</span>
      <div class="grid grid-cols-3 gap-2 max-sm:grid-cols-1" role="radiogroup" aria-label="Start from">
        <button v-for="s in starts" :key="s.id" type="button" role="radio" :aria-checked="start === s.id" :data-testid="`start-${s.id}`"
          class="flex flex-col items-start gap-0.5 rounded-card border px-3 py-2.5 text-left transition-colors" :class="start === s.id ? 'border-accent bg-accent-dim' : 'border-border bg-transparent hover:bg-text/5'" @click="start = s.id">
          <span class="flex items-center gap-1.5 text-[13.5px] font-semibold"><component :is="s.icon" :size="15" />{{ s.title }}</span>
          <span class="text-xs text-muted">{{ s.sub }}</span>
        </button>
      </div>
      <div v-if="start === 'repository'" class="mt-3">
        <div class="grid grid-cols-[1fr_200px] gap-3 max-sm:grid-cols-1">
          <FormField name="repoUrl" label="Repository" v-model="url" mono placeholder="git@github.com:lumen/checkout-app.git" />
          <FormField name="branch" label="Branch" v-model="branch" mono />
        </div>
        <p class="m-0 flex items-start gap-2 text-xs text-muted"><KeyRound :size="13" class="mt-0.5 shrink-0" />Access: each agent's runner clones it with that machine's own git access.</p>
        <p class="m-0 mt-1 flex items-start gap-2 text-xs text-muted"><GitBranch :size="13" class="mt-0.5 shrink-0" /><span>Each thread works in its own worktree on <code>cowork/&lt;thread&gt;</code>. Agents never push to <code>{{ branch || 'main' }}</code>.</span></p>
      </div>
      <div v-else-if="start === 'folder'" class="mt-3">
        <div class="grid grid-cols-[1fr_200px] gap-3 max-sm:grid-cols-1">
          <FormField name="folderPath" label="Folder" v-model="path" mono placeholder="/home/alex/source/checkout-app" />
          <div class="mb-3"><label for="np-host" class="mb-1 block text-xs font-medium text-muted">On</label><Input id="np-host" name="folderHost" v-model="host" list="np-hosts" placeholder="build-01" class="text-sm" /><datalist id="np-hosts"><option v-for="h in hosts" :key="h" :value="h" /></datalist></div>
        </div>
        <p class="m-0 text-xs text-muted">Only agents whose runner is on {{ host || 'that machine' }} can work here. If it's a git folder, threads still get worktrees.</p>
      </div>
      <p v-else class="mt-3 mb-0 text-xs text-muted">Agents can talk and think here; each thread gets an empty folder. Add a repository or a folder in the project's settings later.</p>
    </div>

    <div class="mt-3 border-t border-border pt-3">
      <span class="mb-2 block text-xs font-medium text-muted">Channels</span>
      <p v-if="!suggested.length" class="m-0 text-xs text-muted">Name the project first.</p>
      <div class="flex flex-wrap gap-x-5 gap-y-1.5">
        <label v-for="(c, i) in suggested" :key="i" class="flex cursor-pointer items-center gap-1.5 text-[13.5px]">
          <input type="checkbox" class="size-4 accent-(--accent)" v-model="picked[i]" :data-pick-channel="c.name" />
          <Volume2 v-if="c.kind === 'voice'" :size="14" class="text-muted" aria-hidden="true" /><Hash v-else :size="14" class="text-muted" aria-hidden="true" />{{ c.name }}
        </label>
      </div>
    </div>

    <div class="mt-3 border-t border-border pt-3">
      <span class="mb-2 block text-xs font-medium text-muted">People</span>
      <div class="flex flex-wrap items-center gap-1.5" data-testid="project-people">
        <span v-for="id in people" :key="id" class="flex items-center gap-1.5 rounded-full border border-border py-0.5 pr-1.5 pl-1 text-[13px]" :data-person="store.members.find(m => m.id === id)?.handle">
          <MemberAvatar :member="store.members.find(m => m.id === id)" size="xs" />{{ store.members.find(m => m.id === id)?.displayName }}
          <button type="button" class="grid size-4 place-items-center rounded-full text-muted hover:bg-text/10" :aria-label="`Remove ${store.members.find(m => m.id === id)?.displayName}`" @click="people = people.filter(p => p !== id)"><X :size="11" /></button>
        </span>
        <Select v-if="others.length" :model-value="addId" @update:model-value="addPerson">
          <SelectTrigger class="h-7 w-auto gap-1 rounded-full px-2.5 text-[13px]" aria-label="Add people" data-testid="add-person"><SelectValue placeholder="+ Add people" /></SelectTrigger>
          <SelectContent><SelectItem v-for="m in others" :key="m.id" :value="String(m.id)">{{ m.displayName }} (@{{ m.handle }})</SelectItem></SelectContent>
        </Select>
      </div>
      <p class="m-0 mt-2 text-xs text-muted">Add agents in the project's settings, where you'll see what each one may do before it joins.</p>
    </div>

    <ErrorNote v-if="error" :message="error" class="mt-3" data-testid="form-error" />
    <div class="mt-4 flex items-center justify-end gap-2 border-t border-border pt-3">
      <span class="mr-auto text-xs text-muted">You can change all of this later.</span>
      <Button variant="outline" type="button" @click="closeModal">Cancel</Button>
      <Button type="submit" :disabled="busy || !ready" data-testid="create-project"><Spinner v-if="busy" size="sm" />Create project</Button>
    </div>
  </form>
</template>
