<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { get, post, ApiError } from '../api'
import { store, signedIn } from '../store'
import Field from '../components/ui/Field.vue'
import Button from '../components/ui/Button.vue'

const props = defineProps<{ mode: 'setup' | 'signin' | 'invite'; code?: string }>()
const serverName = ref(''), username = ref(''), displayName = ref(''), password = ref('')
const error = ref(''), busy = ref(false)
const invite = ref<{ valid: boolean; reason: string | null; serverName: string; invitedBy: string } | null>(null)

onMounted(async () => {
  if (props.mode === 'invite') invite.value = await get(`/invites/${props.code}`)
})

const title = computed(() => props.mode === 'setup' ? 'Set up your server' : props.mode === 'invite' ? `Join ${invite.value?.serverName ?? ''}` : `Sign in to ${store.serverName || 'Weave CoWork'}`)

async function submit() {
  error.value = ''; busy.value = true
  try {
    if (props.mode === 'setup') await post('/setup', { serverName: serverName.value, username: username.value, displayName: displayName.value, password: password.value })
    else if (props.mode === 'invite') await post(`/invites/${props.code}/accept`, { username: username.value, displayName: displayName.value, password: password.value })
    else await post('/auth/login', { username: username.value, password: password.value })
    await signedIn()
    if (props.mode === 'invite') history.replaceState(null, '', '/')
  } catch (e) { error.value = e instanceof ApiError ? e.message : 'Something went wrong. Try again.' }
  finally { busy.value = false }
}
</script>
<template>
  <div class="min-h-full grid place-items-center p-4" style="background: radial-gradient(1200px 500px at 20% 0%, var(--accent-dim), transparent), var(--bg)">
    <div class="w-full max-w-[440px] bg-panel border border-line rounded-2xl p-7">
      <div class="w-16 h-16 rounded-[20px] mx-auto mb-3 grid place-items-center font-extrabold text-2xl text-white" style="background: linear-gradient(135deg, var(--accent), #D95A3A)">
        {{ (mode === 'setup' ? serverName : invite?.serverName ?? store.serverName ?? 'W')[0]?.toUpperCase() || 'W' }}
      </div>
      <h1 class="text-[21px] font-semibold text-center m-0 mb-1">{{ title }}</h1>

      <template v-if="mode === 'invite' && invite && !invite.valid">
        <p class="text-center text-muted mt-4" data-testid="invite-error">{{ invite.reason }}</p>
        <p class="text-center text-xs text-muted">Ask whoever invited you for a new link.</p>
      </template>
      <form v-else @submit.prevent="submit" class="mt-5">
        <p v-if="mode === 'setup'" class="text-center text-muted text-[13px] mt-0 mb-4">You're the first person here, so you'll be the admin.</p>
        <p v-if="mode === 'invite' && invite" class="text-center text-muted text-[13px] mt-0 mb-4">{{ invite.invitedBy }} invited you. Make your account to join.</p>
        <Field v-if="mode === 'setup'" name="serverName" label="Server name" v-model="serverName" placeholder="Lumen Labs" />
        <Field name="username" label="Username" v-model="username" autocomplete="username" :placeholder="mode === 'signin' ? '' : 'alex'" />
        <Field v-if="mode !== 'signin'" name="displayName" label="Display name" v-model="displayName" placeholder="Alex" />
        <Field name="password" label="Password" type="password" v-model="password" :autocomplete="mode === 'signin' ? 'current-password' : 'new-password'" :hint="mode === 'signin' ? undefined : 'At least 8 characters.'" />
        <p v-if="error" role="alert" class="text-danger-ink text-[13px] m-0 mb-3" data-testid="auth-error">{{ error }}</p>
        <Button type="submit" variant="primary" :disabled="busy" class="w-full !py-2.5 !text-sm">
          {{ mode === 'setup' ? 'Create server' : mode === 'invite' ? 'Create account and join' : 'Sign in' }}
        </Button>
      </form>
    </div>
  </div>
</template>
