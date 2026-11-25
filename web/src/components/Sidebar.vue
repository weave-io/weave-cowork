<script setup lang="ts">
import { computed } from 'vue'
import { ChevronDown, Hash, Lock, Volume2, Plus, Mic, MicOff, LogOut, Palette, UserPlus, Bot, Users, Check, AtSign, ScrollText, Search } from 'lucide-vue-next'
import { useRoute, useRouter } from 'vue-router'
import { searchShortcut } from '../search'
import { navOpen } from '../layout'
import { store, me, openModal, signOut, memberById } from '../store'
import { voice, leaveVoice, toggleMute } from '../voice'
import { selection, setTheme, themes, type ThemeSelection } from '../theme'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuRadioGroup, DropdownMenuRadioItem,
  DropdownMenuSeparator, DropdownMenuSub, DropdownMenuSubContent, DropdownMenuSubTrigger, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import MemberAvatar from './MemberAvatar.vue'
import StatusDot from './StatusDot.vue'

defineProps<{ channelId: number | null }>()
const emit = defineEmits<{ go: [id: number] }>()
const text = computed(() => store.channels.filter(c => c.kind !== 'voice'))
const voices = computed(() => store.channels.filter(c => c.kind === 'voice'))
const unreadIn = (id: number) => store.unread.channels[String(id)] ?? 0
const route = useRoute(), router = useRouter()
const searching = computed(() => route.path === '/search')
function openSearch() { navOpen.value = false; if (!searching.value) router.push('/search') }
const themeLabel = computed(() => selection.value === 'system' ? 'System' : themes.find(t => t.id === selection.value)?.label)
</script>
<template>
  <nav class="flex min-h-0 min-w-0 flex-1 flex-col" aria-label="Channels">
    <DropdownMenu>
      <DropdownMenuTrigger as-child>
        <button type="button" class="mx-1 mt-1 mb-1 flex h-10 items-center justify-between gap-2 rounded-btn border-0 bg-transparent px-2.5 text-left text-[15px] font-semibold text-text transition-colors hover:bg-text/5" data-testid="server-menu">
          <span class="truncate">{{ store.serverName }}</span><ChevronDown :size="14" class="shrink-0 text-muted" aria-hidden="true" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" class="w-56">
        <DropdownMenuItem v-if="store.isAdmin" data-testid="menu-invite-people" @select="openModal('invite-people')"><UserPlus />Invite people</DropdownMenuItem>
        <DropdownMenuItem data-testid="menu-invite-agent" @select="openModal('invite-agent')"><Bot />Invite an agent</DropdownMenuItem>
        <DropdownMenuItem data-testid="menu-people" @select="openModal('people')"><Users />{{ store.isAdmin ? 'People & agents' : 'People' }}</DropdownMenuItem>
        <DropdownMenuItem v-if="store.isAdmin" data-testid="menu-audit" @select="openModal('audit')"><ScrollText />Audit</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>

    <div class="thin-scrollbar min-h-0 flex-1 overflow-auto px-1 pb-2">
      <button type="button" class="chan mt-1" :class="{ unread: store.unread.mentions > 0 }" data-testid="mentions" @click="openModal('mentions')">
        <AtSign :size="14" class="shrink-0" aria-hidden="true" /><span class="truncate">Mentions</span>
        <span v-if="store.unread.mentions > 0" class="count" data-testid="mentions-badge" :aria-label="`${store.unread.mentions} unread mentions`">{{ store.unread.mentions }}</span>
      </button>
      <button type="button" class="chan" :class="{ active: searching }" :aria-current="searching ? 'page' : undefined" data-testid="search-nav" @click="openSearch">
        <Search :size="14" class="shrink-0" aria-hidden="true" /><span class="truncate">Search</span>
        <kbd class="ml-auto font-sans text-[11px] text-muted">{{ searchShortcut }}</kbd>
      </button>
      <div class="flex items-center justify-between px-2.5 pt-3 pb-1">
        <span class="text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Channels</span>
        <Button variant="toolbar-icon" size="toolbar" class="size-6" aria-label="Create channel" data-testid="create-channel" @click="openModal('create-channel')"><Plus :size="14" /></Button>
      </div>
      <button v-for="c in text" :key="c.id" type="button" class="chan" :class="{ active: c.id === channelId, unread: unreadIn(c.id) > 0 }" :data-channel="c.name" :data-kind="c.kind" :aria-current="c.id === channelId ? 'true' : undefined" @click="emit('go', c.id)">
        <Lock v-if="c.isPrivate" :size="14" class="shrink-0" aria-label="Private" /><Hash v-else :size="14" class="shrink-0" aria-hidden="true" />
        <span class="truncate">{{ c.name }}</span>
        <span v-if="unreadIn(c.id) > 0" class="count" data-testid="unread-badge" :aria-label="`${unreadIn(c.id)} unread`">{{ unreadIn(c.id) > 99 ? '99+' : unreadIn(c.id) }}</span>
      </button>

      <div v-if="voices.length" class="px-2.5 pt-4 pb-1"><span class="text-[11px] font-semibold tracking-[0.05em] text-muted uppercase">Voice channels</span></div>
      <template v-for="c in voices" :key="c.id">
        <button type="button" class="chan" :class="{ active: c.id === channelId }" :data-channel="c.name" :data-kind="c.kind" :aria-current="c.id === channelId ? 'true' : undefined" @click="emit('go', c.id)">
          <Volume2 :size="14" class="shrink-0" aria-hidden="true" /><span class="truncate">{{ c.name }}</span>
          <Lock v-if="c.isPrivate" :size="12" class="ml-auto shrink-0 opacity-70" aria-label="Private" />
        </button>
        <ul v-if="store.voicePresence[c.id]?.length" class="m-0 mb-1 list-none py-0 pr-2 pl-[30px]" :data-voice-members="c.name">
          <li v-for="id in store.voicePresence[c.id]" :key="id" class="flex items-center gap-2 py-[3px] text-[12.5px] text-muted" :data-voice-member="memberById(id)?.handle">
            <MemberAvatar :member="memberById(id)" size="xs" /><span class="truncate">{{ memberById(id)?.displayName }}</span>
          </li>
        </ul>
      </template>
    </div>

    <div v-if="voice.status === 'connected' && voice.channelId != null" class="mx-1 mb-1 flex items-center gap-2 rounded-card border border-border bg-card-bg px-2.5 py-1.5 text-[12.5px]" data-testid="voice-bar">
      <StatusDot state="online" /><span class="truncate font-medium">{{ store.channels.find(c => c.id === voice.channelId)?.name }}</span>
      <span class="ml-auto flex gap-1">
        <Button variant="toolbar-icon" size="toolbar" :aria-label="voice.muted ? 'Unmute' : 'Mute'" :title="voice.muted ? 'Unmute' : 'Mute'" @click="toggleMute"><MicOff v-if="voice.muted" :size="14" /><Mic v-else :size="14" /></Button>
        <Button variant="toolbar-icon-danger" size="sm" class="h-7 px-2 text-xs" @click="leaveVoice()">Leave</Button>
      </span>
    </div>

    <DropdownMenu>
      <DropdownMenuTrigger as-child>
        <button type="button" class="mx-1 mb-1 flex items-center gap-2.5 rounded-btn border-0 bg-transparent px-2.5 py-2 text-left text-[13px] text-text transition-colors hover:bg-text/5" data-testid="user-menu">
          <MemberAvatar :member="me" size="sm" />
          <span class="min-w-0 flex-1 truncate font-medium" data-testid="me-name">{{ me?.displayName }}</span>
          <ChevronDown :size="14" class="shrink-0 text-muted" aria-hidden="true" />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" side="top" class="w-56">
        <DropdownMenuLabel>Signed in as @{{ me?.handle }}</DropdownMenuLabel>
        <DropdownMenuSub>
          <DropdownMenuSubTrigger data-testid="theme-menu"><Palette />Theme<span class="ml-auto pr-1 text-xs text-muted">{{ themeLabel }}</span></DropdownMenuSubTrigger>
          <DropdownMenuSubContent class="w-48">
            <DropdownMenuRadioGroup :model-value="selection" @update:model-value="setTheme($event as ThemeSelection)">
              <DropdownMenuRadioItem value="system" data-theme-option="system">System</DropdownMenuRadioItem>
              <DropdownMenuRadioItem v-for="t in themes" :key="t.id" :value="t.id" :data-theme-option="t.id">{{ t.label }}</DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
          </DropdownMenuSubContent>
        </DropdownMenuSub>
        <DropdownMenuSeparator />
        <DropdownMenuItem data-testid="sign-out" @select="signOut"><LogOut />Sign out</DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  </nav>
</template>
<style scoped>
.chan { width: 100%; min-width: 0; min-height: 32px; display: flex; align-items: center; gap: 9px; padding: 0 10px; margin: 1px 0; border: 0; border-radius: var(--radius-btn); background: transparent; color: color-mix(in srgb, var(--text) 86%, transparent); text-align: left; cursor: pointer; font-size: 13.5px; transition: background var(--transition), color var(--transition); }
.chan:hover { background: color-mix(in srgb, var(--text) 5%, transparent); color: var(--text); }
.chan.active { background: var(--accent-dim); color: var(--text); font-weight: 600; }
.chan svg { color: var(--muted); }
.chan.unread { color: var(--text); font-weight: 600; }
.count { margin-left: auto; min-width: 18px; height: 18px; padding: 0 6px; border-radius: 9px; background: var(--accent); color: var(--accent-foreground, #fff); font-size: 11px; font-weight: 600; line-height: 18px; text-align: center; }
.chan.active svg { color: var(--accent-text); }
</style>
