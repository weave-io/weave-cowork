namespace CoWork.Contracts;

// What the server and a runner say to each other. One runner connection serves one agent.

public record EnrolRequest(string Code, string Host);
public record EnrolResponse(long AgentId, string Handle, string Token);

/// <summary>Server → runner: deliver one message to the agent's session for a thread.</summary>
public record DeliverMessage(
    long DeliveryId, long ThreadId, long AgentId, string Handle,
    string Instructions, string[] HarnessCommand, string Workspace,
    string Text,
    bool RecordToolOutput = false,    // the agent's switch "Record full tool output": only then does the runner send a tool's output text
    long? ProjectId = null,           // the thread's channel is in this project: the harness keeps its data for it apart (a data folder per agent and project)
    ThreadWorkspace? ThreadWorkspace = null,    // where the thread works, when its channel is in a project; else `Workspace` (the agent's own folder)
    string? SetupCommand = null);               // a sandbox: the project's setup command, run once per thread before the first prompt

/// <summary>Where a thread in a project works. Kind: repository (cloned once per agent and repository, a worktree per thread on
/// <see cref="Branch"/>), folder (a git folder gets a worktree too; any other folder is used as it is), none (an empty folder per thread).</summary>
public record ThreadWorkspace(
    long? WorkspaceId, string Name, string Kind,
    string? Url, string? BaseBranch,   // repository: what to clone and the project's branch (nobody pushes to it)
    string? Path,                      // folder: where it is on the runner's machine
    string Branch);                    // the thread's own branch: cowork/<thread-slug>

/// <summary>Runner → server: what the agent is doing in a thread.</summary>
public record StreamEvent(
    long ThreadId,
    string Kind,            // text | toolStart | toolUpdate | toolEnd | turnEnded | error | workspace
    string? Text = null,    // text delta, or the error message
    string? ToolId = null,
    string? Title = null,
    string? ToolKind = null,
    string? Status = null,
    long? OutputBytes = null,       // toolEnd: how much the tool printed, and its SHA-256 (always sent)
    string? OutputSha256 = null,
    string? Output = null,          // toolEnd: the text itself, only when the agent records full tool output
    long? WorkspaceId = null);      // workspace: the project workspace it prepared (Status = worktree | folder | scratch | agent, Title = branch, Text = where)

public record PermissionOption(string Id, string Name, string Kind);
public record PermissionRequestMsg(long ThreadId, string RequestId, string Title, PermissionOption[] Options);
public record PermissionAnswerMsg(string RequestId, string? OptionId);   // null = deny / cancelled

public static class StreamKinds
{
    public const string Text = "text", ToolStart = "toolStart", ToolEnd = "toolEnd", ToolUpdate = "toolUpdate", TurnEnded = "turnEnded", Error = "error", Workspace = "workspace";
}

// ---- pools and sandboxes ------------------------------------------------------------------------------------------
// A pool is a runner in pool mode: it serves no agent itself. It enrols with a one-time code, and its token may only receive the commands
// below and report state. The commands take the shape of Agent Substrate's actor API (create, resume, suspend, delete; network rules and
// credential references as data), so another provider is a translation, not a redesign.

public record PoolEnrolRequest(string Code, string Host, int Slots, long MemoryMb);
/// <param name="ServerId">The server's own id: with the pool's id it makes the names and labels of what the pool creates unique to this server.</param>
public record PoolEnrolResponse(long PoolId, string Name, string Token, string? ServerId = null);

/// <summary>Pool → server when it connects: how much room it has and which sandboxes it is running now (by id).</summary>
public record PoolHello(int Slots, long MemoryMb, string[] Running);

/// <summary>What a sandbox starts from and runs: the template's image and harness, the model it asks the gateway for.</summary>
public record SandboxTemplate(string Image, string[] HarnessCommand, string? Model);

/// <summary>The thread's workspace inside the sandbox: a repository is copied from the pool's mirror onto <see cref="Branch"/>.</summary>
public record SandboxWorkspace(string Kind, string? Name, string? Url, string? BaseBranch, string Branch);    // Kind: repository | none

public record SandboxLimits(long MemoryMb, double? Cpus = null);

/// <summary>Network rules in the shape of Substrate's EgressPolicy: hosts a sandbox may reach and, for a host, headers the proxy replaces with a
/// secret it holds (the sandbox sends a placeholder). Round 1 carries them; enforcing them is the egress proxy's job.</summary>
public record EgressPolicy(EgressRule[] Rules);
public record EgressRule(string[] Hosts, ReplaceHeader[]? ReplaceHeaders = null);
public record ReplaceHeader(string Header, string? Prefix, string CredentialRef);

/// <summary>Create or resume one sandbox: the thread's container. <see cref="ThreadToken"/> is minted for this start only: the runner in the
/// container connects with it, and it is the sandbox's key to the model gateway. Ids say whose it is; the pool labels what it makes with them.</summary>
public record SandboxSpec(
    string SandboxId, long ThreadId, long AgentId, string Handle, long? ProjectId,
    SandboxTemplate Template, SandboxWorkspace Workspace, SandboxLimits Limits, EgressPolicy Network,
    string ThreadToken);

public record CreateSandbox(SandboxSpec Spec);
public record ResumeSandbox(SandboxSpec Spec);
public record SuspendSandbox(string SandboxId);
/// <summary>Delete the sandbox's volume (the thread is done and its retention passed). With <see cref="KeepIfChanged"/> the pool first looks at the
/// thread's branch in the volume (with <see cref="Image"/>): changes to tracked files, or commits not on <see cref="BaseBranch"/>, keep it.</summary>
public record DeleteSandbox(string SandboxId, bool KeepIfChanged = false, string? Image = null, string? BaseBranch = null);

/// <summary>Pool → server: a sandbox's state. <see cref="WorkspaceMs"/> and <see cref="ContainerMs"/>: how long preparing the volume and starting
/// the container took (for the thread's starting rows).</summary>
public record SandboxStateReport(string SandboxId, string State, string? Reason = null, long? WorkspaceMs = null, long? ContainerMs = null);

/// <summary>Server → pool: let the sandbox reach a host it was refused, for one connection or for the rest of the thread (someone allowed it).</summary>
public record AllowEgress(string SandboxId, string Host, bool Once);

/// <summary>Pool → server: the sandbox tried to reach a host that isn't on its list; the proxy refused it.</summary>
public record EgressRefusal(string SandboxId, string Host, int Port);

public static class SandboxStates
{
    public const string Suspended = "suspended", Resuming = "resuming", Running = "running", Suspending = "suspending", Failed = "failed", Deleted = "deleted", Kept = "kept";
}

// ---- artifacts (brief 9) ----------------------------------------------------------------------------------------------
/// <summary>Server → runner: the agent called <c>cowork_show</c>. Read <see cref="Path"/> in the thread's workspace (and, for a page, the folder it's
/// in) and upload it to <c>/api/runner/artifacts/{RequestId}</c> with the runner's token (a sandbox's: its thread token).</summary>
public record CollectArtifact(string RequestId, long ThreadId, string Path);

public static class ArtifactLimits
{
    /// <summary>At most this much per artifact version: the file and the files beside it.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;
    public const int MaxFiles = 500;
}
