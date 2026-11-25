export interface Member {
  id: number; kind: 'person' | 'agent'; handle: string; displayName: string; removed: boolean
  role: 'admin' | 'member' | null; ownerId: number | null; about: string | null; online: boolean
  harnessCommand: string[] | null; workspace: string | null; runnerHost: string | null; connected: boolean
  /** agents: 'runner' (its owner connects a runner) or 'pool' (a team template: a sandbox per thread on a pool) */
  placement?: 'runner' | 'pool' | null; image?: string | null; model?: string | null
}
export interface Channel {
  id: number; name: string; kind: 'text' | 'voice'; topic: string; isPrivate: boolean; creatorId: number; memberIds: number[]; canManage: boolean
  /** the project the channel is in, or null outside any project */
  projectId: number | null
}
/** A project the person may see: it holds channels and owns its workspaces, people and agents. */
export interface Project { id: number; name: string; about: string; role: 'lead' | 'member' | null; canEdit: boolean }
/** An agent's boundary in a project, in sentences the server computed from what it enforces. */
export interface BoundaryLine { key: string; label: string; text: string }
export interface Boundary {
  project: { id: number; name: string }
  agent: { id: number; handle: string; displayName: string; about: string; ownerId: number; ownerName: string; runnerHost: string | null; online: boolean; harnessCommand: string[] }
  takesWorkFrom: 'owner' | 'members'; inProject: boolean; sandboxed: boolean; lines: BoundaryLine[]; warning: string
  /** a team template's boundary: what its sandbox means, said once */
  note?: string
}
export interface Post {
  id: number; channelId: number; threadId: number | null; authorId: number; body: string
  kind: 'text' | 'tool' | 'ask' | 'notice' | 'sandbox'; meta: any; streaming: boolean; createdAt: number
  replyCount: number; hasThread: boolean; threadAgents?: string[] | null
  /** the position of the last event that changed this post: of two copies, the higher one is newer */
  position?: number
}
export interface ThreadAgent {
  id: number; handle: string; displayName: string; online: boolean; busy: boolean; queued: number
  harnessCommand: string[]; runnerHost: string | null; ownerId: number
  /** where the thread's work happens, as the agent's runner reported it */
  workspace?: { place: 'worktree' | 'folder' | 'scratch' | 'agent' | 'sandbox'; branch: string | null; host: string | null; sandboxed: boolean } | null
  placement?: 'runner' | 'pool'
  /** a team template's sandbox for this thread: its state and its pool */
  sandbox?: { state: SandboxState; pool: string | null; starts: number } | null
  /** Pause all agents: who paused this thread's project */
  pausedBy?: string | null
  /** what a template's thread has used through the gateway */
  usage?: { calls: number; tokens: number; cost: number | null } | null
}
export type SandboxState = 'waiting' | 'starting' | 'resuming' | 'running' | 'suspending' | 'suspended' | 'failed' | 'deleted'
/** One row of a sandbox's starting card, as the server wrote it. Text between backticks is code. */
export interface SandboxStep { key: 'pool' | 'container' | 'workspace' | 'harness'; state: 'todo' | 'waiting' | 'running' | 'done' | 'failed'; text: string; secs?: number }
export interface ThreadState { rootId: number; root: Post; replies: Post[]; agents: ThreadAgent[]; startedBy: number }
export interface FeedState { channelId: number | null; posts: Post[]; thread: ThreadState | null }
