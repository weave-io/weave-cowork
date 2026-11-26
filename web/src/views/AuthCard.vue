<script setup lang="ts">
import { ref, computed, onMounted, watch } from 'vue'
import { LogIn } from 'lucide-vue-next'
import { get, post, del, ApiError } from '../api'
import { store, signedIn } from '../store'
import weaveLogo from '@/assets/weave_logo.png'
import Field from '../components/FormField.vue'
import { Button } from '@/components/ui/button'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

// Nobody types a password: "Continue with single sign-on" goes to the server's OpenID Connect provider (a page load, /auth/sso), which sends
// the person back. A member is then signed in; anyone else comes back here with who the provider said they are ("pending") and either
// chooses a handle (setup, an invite) or is told they aren't a member (signing in).
const props = defineProps<{ mode: 'setup' | 'signin' | 'invite'; code?: string }>()
type Pending = { email: string | null; name: string | null; intent: string; code: string | null; serverName: string | null; handle: string; displayName: string }

const pending = ref<Pending | null>(null), loaded = ref(false)
const serverName = ref(''), handle = ref(''), displayName = ref('')
const error = ref(''), busy = ref(false)
const invite = ref<{ valid: boolean; reason: string | null; serverName: string; invitedBy: string } | null>(null)
const providerSaidNo = ref(false)
watch([handle, displayName, serverName], () => { error.value = '' })        // an error is about what was sent: editing clears it

onMounted(async () => {
  const q = new URLSearchParams(location.search)
  if (q.get('sso') === 'failed') { providerSaidNo.value = true; history.replaceState(null, '', location.pathname) }
  const [inv, p] = await Promise.all([props.mode === 'invite' ? get(`/invites/${props.code}`) : Promise.resolve(null), get('/auth/pending')])
  invite.value = inv
  pending.value = p.pending
  if (p.pending) { handle.value = p.pending.handle; displayName.value = p.pending.displayName; serverName.value = p.pending.serverName ?? '' }
  loaded.value = true
})

const place = computed(() => (props.mode === 'invite' ? invite.value?.serverName : store.serverName) || 'Weave CoWork')
/** Back from the provider and not a member yet: choosing a handle to set up or join with. */
const finishing = computed(() => !!pending.value && props.mode !== 'signin')
/** Back from the provider, signing in, and not a member. */
const notMember = computed(() => !!pending.value && props.mode === 'signin')
const who = computed(() => pending.value?.email || pending.value?.name || 'That account')

function toProvider(again = false) {
  if (props.mode === 'setup' && !again && !serverName.value.trim()) { error.value = 'Name the server (up to 60 characters).'; return }
  const q = new URLSearchParams({ intent: props.mode })
  if (props.code) q.set('code', props.code)
  if (props.mode === 'setup' && serverName.value.trim()) q.set('serverName', serverName.value.trim())
  if (again) q.set('again', 'true')
  busy.value = true
  location.href = `/auth/sso?${q}`
}

/** "Not you?" / "Use another account": forget who the provider said, and ask it to sign in afresh. */
async function anotherAccount() {
  await del('/auth/pending')
  toProvider(true)
}

const reload = () => location.reload()

async function finish() {
  error.value = ''; busy.value = true
  try {
    if (props.mode === 'setup') await post('/setup', { serverName: serverName.value, handle: handle.value, displayName: displayName.value })
    else await post(`/invites/${props.code}/accept`, { handle: handle.value, displayName: displayName.value })
    await signedIn()
    if (props.mode === 'invite') history.replaceState(null, '', '/')
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Something went wrong. Try again.' }
  finally { busy.value = false }
}
</script>
<template>
  <div class="grid h-full place-items-center overflow-auto bg-main-bg p-4">
    <div class="w-full max-w-[420px] rounded-panel border border-border bg-panel-bg p-7 shadow-(--sheet-shadow)" data-testid="auth-card">
      <img :src="weaveLogo" alt="Weave" class="mx-auto mb-4 size-12 object-contain" data-testid="auth-logo">

      <div v-if="!loaded" class="flex justify-center py-6 text-muted"><Spinner /></div>

      <!-- nobody can sign in until whoever runs the server sets a provider up -->
      <template v-else-if="!store.auth.sso">
        <h1 class="m-0 mb-1 text-center text-xl font-semibold">Sign-in isn't set up yet</h1>
        <p class="mt-2 mb-4 text-center text-[13px] text-muted" data-testid="sso-missing">This server needs an OpenID Connect provider before anyone can sign in. Whoever runs it sets these and restarts it:</p>
        <ul class="m-0 mb-4 list-none space-y-1 p-0 text-center text-xs"><li><code>COWORK_OIDC__AUTHORITY</code></li><li><code>COWORK_OIDC__CLIENTID</code></li><li><code>COWORK_OIDC__CLIENTSECRET</code></li></ul>
        <Button variant="outline" class="w-full" @click="reload">Try again</Button>
      </template>

      <!-- a server from before single sign-on: its accounts had passwords -->
      <template v-else-if="mode === 'signin' && store.auth.passwordAccounts">
        <h1 class="m-0 mb-1 text-center text-xl font-semibold">Accounts here used passwords</h1>
        <p class="mt-2 mb-0 text-center text-[13px] text-muted" data-testid="password-accounts">CoWork signs people in with single sign-on now, and accounts made with a password can't sign in any more. Whoever runs this server starts it with a fresh data folder and sets it up again.</p>
      </template>

      <template v-else-if="mode === 'invite' && invite && !invite.valid && !finishing">
        <h1 class="m-0 mb-1 text-center text-xl font-semibold">Join {{ place }}</h1>
        <p class="mt-4 text-center text-muted" data-testid="invite-error">{{ invite.reason }}</p>
        <p class="text-center text-xs text-muted">Ask whoever invited you for a new link.</p>
      </template>

      <!-- back from the provider: choose how people here see you -->
      <form v-else-if="finishing" @submit.prevent="finish" data-testid="finish-form">
        <h1 class="m-0 mb-1 text-center text-xl font-semibold">{{ mode === 'setup' ? 'Set up your server' : `Join ${place}` }}</h1>
        <p class="mt-0 mb-4 text-center text-[13px] text-muted">{{ mode === 'setup' ? "You're the first person here, so you'll be the admin." : 'Choose how people here see you.' }}</p>
        <div class="mb-4 flex items-center gap-2.5 rounded-btn border border-border bg-main-bg px-3 py-2.5 text-[13px]" data-testid="sso-who">
          <span class="grid size-7 shrink-0 place-items-center rounded-md bg-accent-dim text-accent-text"><LogIn :size="15" aria-hidden="true" /></span>
          <span class="min-w-0 flex-1"><span class="block truncate font-medium">{{ who }}</span><span class="block text-xs text-muted">Signed in with single sign-on</span></span>
          <button type="button" class="shrink-0 cursor-pointer border-0 bg-transparent p-0 text-xs text-accent-text hover:underline" @click="anotherAccount">Not you?</button>
        </div>
        <Field v-if="mode === 'setup'" name="serverName" label="Server name" v-model="serverName" placeholder="Lumen Labs" />
        <Field name="handle" label="Handle" v-model="handle" autocomplete="username" placeholder="rosa" :hint="handle ? `People mention you as @${handle}.` : 'Lowercase letters, numbers, - and _.'" />
        <Field name="displayName" label="Display name" v-model="displayName" autocomplete="name" placeholder="Rosa" />
        <ErrorNote v-if="error" :message="error" class="mb-3" data-testid="auth-error" />
        <Button type="submit" :disabled="busy" class="w-full"><Spinner v-if="busy" size="sm" />{{ mode === 'setup' ? 'Create server' : `Join ${place}` }}</Button>
      </form>

      <!-- step 1: off to the provider -->
      <template v-else>
        <template v-if="mode === 'invite' && invite">
          <p class="m-0 text-center text-[13px] text-muted">{{ invite.invitedBy }} invited you to</p>
          <h1 class="m-0 mb-1 text-center text-xl font-semibold">{{ place }}</h1>
          <p class="mt-0 mb-5 text-center text-[13px] text-muted">Sign in with your work account to join.</p>
        </template>
        <template v-else-if="mode === 'setup'">
          <h1 class="m-0 mb-1 text-center text-xl font-semibold">Set up your server</h1>
          <p class="mt-0 mb-4 text-center text-[13px] text-muted">You're the first person here, so you'll be the admin.</p>
          <Field name="serverName" label="Server name" v-model="serverName" placeholder="Lumen Labs" />
        </template>
        <template v-else>
          <h1 class="m-0 mb-5 text-center text-xl font-semibold">Sign in to {{ place }}</h1>
        </template>
        <ErrorNote v-if="providerSaidNo" message="Your sign-in provider didn't sign you in." reason="Try again, or ask whoever runs it for access to CoWork." class="mb-3" data-testid="sso-failed" />
        <ErrorNote v-if="notMember" :message="`${who} isn't a member of ${place}.`" reason="Ask someone here for an invite link." class="mb-3" data-testid="not-member" />
        <ErrorNote v-if="error" :message="error" class="mb-3" data-testid="auth-error" />
        <Button v-if="notMember" class="w-full" :disabled="busy" data-testid="sso-continue" @click="anotherAccount"><Spinner v-if="busy" size="sm" /><LogIn v-else aria-hidden="true" />Use another account</Button>
        <Button v-else class="w-full" :disabled="busy" data-testid="sso-continue" @click="toProvider()"><Spinner v-if="busy" size="sm" /><LogIn v-else aria-hidden="true" />Continue with single sign-on</Button>
        <p class="mt-4 mb-0 text-center text-xs text-muted">You sign in with your organisation's account. {{ place }} never sees your password.</p>
      </template>
    </div>
  </div>
</template>
