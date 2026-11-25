<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { get, put, ApiError } from '../../api'
import { closeModal } from '../../store'
import AppDialog from '../AppDialog.vue'
import Field from '../FormField.vue'
import { Button } from '@/components/ui/button'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

// Server settings: where CoWork's model gateway sends a sandbox's model calls (the key stays here, never in a sandbox), and how long an idle
// sandbox keeps running before it stops (its volume is kept).
interface S { models: { kind: string | null; baseUrl: string; hasKey: boolean; copilotSignedIn: boolean }; sandboxes: { idleSeconds: number } }
const s = ref<S | null>(null), error = ref(''), saved = ref(''), busy = ref(false)
const kind = ref<'openai-compatible' | 'github-copilot'>('openai-compatible'), baseUrl = ref(''), apiKey = ref(''), idle = ref('900')
onMounted(async () => {
  try {
    s.value = await get('/settings/server')
    kind.value = (s.value!.models.kind as any) ?? (s.value!.models.copilotSignedIn ? 'github-copilot' : 'openai-compatible')
    baseUrl.value = s.value!.models.baseUrl; idle.value = String(s.value!.sandboxes.idleSeconds)
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' }
})
const idleLabel = computed(() => { const n = Number(idle.value); return n >= 60 && n % 60 === 0 ? `${n / 60} minute${n === 60 ? '' : 's'}` : `${n} seconds` })
async function run(what: string, f: () => Promise<unknown>) {
  error.value = ''; saved.value = ''; busy.value = true
  try { await f(); saved.value = what; s.value = await get('/settings/server'); apiKey.value = '' } catch (e) { error.value = e instanceof ApiError ? e.message : 'Try again.' } finally { busy.value = false }
}
const saveModels = () => run('models', () => put('/settings/models', kind.value === 'github-copilot' ? { kind: kind.value } : { kind: kind.value, baseUrl: baseUrl.value, apiKey: apiKey.value }))
const saveIdle = () => run('sandboxes', () => put('/settings/sandboxes', { idleSeconds: Number(idle.value) }))
</script>
<template>
  <AppDialog title="Server settings" wide @close="closeModal">
    <Spinner v-if="!s && !error" />
    <template v-if="s">
      <section class="mb-5" data-testid="settings-models">
        <h3 class="m-0 mb-1 text-[14px] font-semibold">Models</h3>
        <p class="mt-0 text-[13px] text-muted">Sandboxes reach models only through CoWork's gateway, with their thread's token. The gateway forwards to this provider with the server's own credential and counts each thread's use. Subscriptions people hold stay with their own pinned agents.</p>
        <div class="grid gap-2" role="radiogroup" aria-label="Provider">
          <label class="opt" :class="{ on: kind === 'openai-compatible' }"><input v-model="kind" type="radio" value="openai-compatible" class="accent-(--accent)" data-testid="provider-openai" />
            <span><b class="font-medium">An OpenAI-compatible provider</b><span class="block text-[12.5px] text-muted">A base URL and a key: OpenRouter, OpenAI, a model on your own machine.</span></span></label>
          <label class="opt" :class="{ on: kind === 'github-copilot', off: !s.models.copilotSignedIn }"><input v-model="kind" type="radio" value="github-copilot" :disabled="!s.models.copilotSignedIn" class="accent-(--accent)" data-testid="provider-copilot" />
            <span><b class="font-medium">GitHub Copilot, signed in on this server's machine</b><span class="block text-[12.5px] text-muted">For a server only its owner uses: every sandbox's model calls go through the owner's Copilot subscription. {{ s.models.copilotSignedIn ? 'OpenCode on this machine is signed in.' : 'Sign in with OpenCode on this machine first (opencode auth login).' }}</span></span></label>
        </div>
        <div v-if="kind === 'openai-compatible'" class="mt-3">
          <Field name="baseUrl" label="Base URL" v-model="baseUrl" mono placeholder="https://openrouter.ai/api/v1" />
          <Field name="apiKey" label="API key" v-model="apiKey" type="password" :placeholder="s.models.hasKey ? 'Saved. Type a new one to replace it.' : 'sk-…'" hint="Kept in the server's secrets. It never reaches a sandbox or the event log." />
        </div>
        <div class="mt-3 flex items-center justify-end gap-3">
          <span v-if="saved === 'models'" class="text-[13px] text-running-text" data-testid="models-saved">Saved.</span>
          <Button data-testid="save-models" :disabled="busy || (kind === 'openai-compatible' && !baseUrl.trim())" @click="saveModels">Save models</Button>
        </div>
      </section>
      <section class="border-t border-border pt-4" data-testid="settings-sandboxes">
        <h3 class="m-0 mb-1 text-[14px] font-semibold">Sandboxes</h3>
        <p class="mt-0 text-[13px] text-muted">A sandbox with nothing to do stops after this long. Its volume stays, so the thread's next message picks up where it left off.</p>
        <div class="flex items-end gap-3">
          <div class="w-40"><Field name="idleSeconds" label="Stop when idle for (seconds)" v-model="idle" type="number" /></div>
          <span class="mb-5 text-[13px] text-muted">{{ idleLabel }}</span>
          <span v-if="saved === 'sandboxes'" class="mb-5 ml-auto text-[13px] text-running-text" data-testid="idle-saved">Saved.</span>
          <Button class="mb-3" :class="saved !== 'sandboxes' && 'ml-auto'" data-testid="save-idle" :disabled="busy || !(Number(idle) >= 5)" @click="saveIdle">Save</Button>
        </div>
      </section>
    </template>
    <ErrorNote v-if="error" :message="error" class="mt-3" />
  </AppDialog>
</template>
<style scoped>
.opt { display: flex; align-items: flex-start; gap: 10px; padding: 9px 11px; border: 1px solid var(--border); border-radius: var(--radius-btn); background: var(--card-bg); cursor: pointer; font-size: 13.5px; }
.opt input { margin-top: 3px; }
.opt.on { border-color: var(--accent); background: var(--accent-dim); }
.opt.off { cursor: default; opacity: .7; }
</style>
