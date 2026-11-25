<script setup lang="ts">
import { Eye, Users, GitBranch, Globe, Hand, Coins, Brain, CirclePause, ShieldAlert } from 'lucide-vue-next'
import type { Boundary } from '../types'

// An agent's boundary in a project, in sentences the server computed from what it enforces. Used where an agent is added to a project,
// and anywhere else that shows what an agent may do (its profile). Text between backticks is code.
defineProps<{ boundary: Boundary }>()
const icons: Record<string, any> = { sees: Eye, takesWorkFrom: Users, worksOn: GitBranch, reaches: Globe, asks: Hand, spends: Coins, remembers: Brain, stops: CirclePause }
const parts = (t: string) => t.split('`').map((text, i) => ({ text, code: i % 2 === 1 }))
</script>
<template>
  <div data-testid="boundary">
    <dl class="m-0 grid grid-cols-[150px_1fr] gap-x-3 gap-y-2 text-[13.5px] max-sm:grid-cols-1">
      <template v-for="l in boundary.lines" :key="l.key">
        <dt class="flex items-start gap-2 pt-px text-[12.5px] font-medium text-muted"><component :is="icons[l.key] ?? Eye" :size="14" class="mt-0.5 shrink-0" aria-hidden="true" />{{ l.label }}</dt>
        <dd class="m-0 leading-snug" :data-line="l.key"><template v-for="(p, i) in parts(l.text)" :key="i"><code v-if="p.code" class="rounded-[4px] border border-border bg-card-bg px-1 font-mono text-[12px]">{{ p.text }}</code><template v-else>{{ p.text }}</template></template>
          <slot :name="l.key" /></dd>
      </template>
    </dl>
    <p v-if="!boundary.sandboxed" class="mt-3 mb-0 flex items-start gap-2 rounded-card border border-idle/40 bg-idle/10 px-3 py-2 text-[13px]" data-testid="boundary-warning">
      <ShieldAlert :size="15" class="mt-0.5 shrink-0 text-idle-text" aria-hidden="true" /><span>{{ boundary.warning }}</span>
    </p>
  </div>
</template>
