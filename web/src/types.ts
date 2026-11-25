export interface Member {
  id: number; kind: 'person' | 'agent'; handle: string; displayName: string; removed: boolean
  role: 'admin' | 'member' | null; ownerId: number | null; about: string | null; online: boolean
  harnessCommand: string[] | null; workspace: string | null; runnerHost: string | null; connected: boolean
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
}
export interface Post {
  id: number; channelId: number; threadId: number | null; authorId: number; body: string
  kind: 'text' | 'tool' | 'ask' | 'notice'; meta: any; streaming: boolean; createdAt: number
  replyCount: number; hasThread: boolean; threadAgents?: string[] | null
  /** the position of the last event that changed this post: of two copies, the higher one is newer */
  position?: number
}
export interface ThreadAgent {
  id: number; handle: string; displayName: string; online: boolean; busy: boolean; queued: number
  harnessCommand: string[]; runnerHost: string | null; ownerId: number
  /** where the thread's work happens, as the agent's runner reported it */
  workspace?: { place: 'worktree' | 'folder' | 'scratch' | 'agent'; branch: string | null; host: string | null; sandboxed: boolean } | null
}
export interface ThreadState { rootId: number; root: Post; replies: Post[]; agents: ThreadAgent[]; startedBy: number }
export interface FeedState { channelId: number | null; posts: Post[]; thread: ThreadState | null }
