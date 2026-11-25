<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { get, post, ApiError } from '../api'
import { store, signedIn } from '../store'
import weaveLogo from '@/assets/weave_logo.png'
import Field from '../components/FormField.vue'
import { Button } from '@/components/ui/button'
import { ErrorNote } from '@/components/ui/error-note'
import { Spinner } from '@/components/ui/spinner'

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
  <div class="grid h-full place-items-center overflow-auto bg-main-bg p-4">
    <div class="w-full max-w-[420px] rounded-panel border border-border bg-panel-bg p-7 shadow-(--sheet-shadow)">
      <img :src="weaveLogo" alt="Weave" class="mx-auto mb-4 size-12 object-contain" data-testid="auth-logo">
      <h1 class="m-0 mb-1 text-center text-xl font-semibold">{{ title }}</h1>

      <template v-if="mode === 'invite' && invite && !invite.valid">
        <p class="mt-4 text-center text-muted" data-testid="invite-error">{{ invite.reason }}</p>
        <p class="text-center text-xs text-muted">Ask whoever invited you for a new link.</p>
      </template>
      <form v-else class="mt-5" @submit.prevent="submit">
        <p v-if="mode === 'setup'" class="mt-0 mb-4 text-center text-[13px] text-muted">You're the first person here, so you'll be the admin.</p>
        <p v-if="mode === 'invite' && invite" class="mt-0 mb-4 text-center text-[13px] text-muted">{{ invite.invitedBy }} invited you. Make your account to join.</p>
        <Field v-if="mode === 'setup'" name="serverName" label="Server name" v-model="serverName" placeholder="Lumen Labs" />
        <Field name="username" label="Username" v-model="username" autocomplete="username" :placeholder="mode === 'signin' ? '' : 'alex'" />
        <Field v-if="mode !== 'signin'" name="displayName" label="Display name" v-model="displayName" placeholder="Alex" />
        <Field name="password" label="Password" type="password" v-model="password" :autocomplete="mode === 'signin' ? 'current-password' : 'new-password'" :hint="mode === 'signin' ? undefined : 'At least 8 characters.'" />
        <ErrorNote v-if="error" :message="error" class="mb-3" data-testid="auth-error" />
        <Button type="submit" :disabled="busy" class="w-full">
          <Spinner v-if="busy" size="sm" />{{ mode === 'setup' ? 'Create server' : mode === 'invite' ? 'Create account and join' : 'Sign in' }}
        </Button>
      </form>
    </div>
  </div>
</template>
