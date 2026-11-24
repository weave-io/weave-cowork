namespace CoWork.Contracts;

// What the server and a runner say to each other. One runner connection serves one agent.

public record EnrolRequest(string Code, string Host);
public record EnrolResponse(long AgentId, string Handle, string Token);

/// <summary>Server → runner: deliver one message to the agent's session for a thread.</summary>
public record DeliverMessage(
    long DeliveryId, long ThreadId, long AgentId, string Handle,
    string Instructions, string[] HarnessCommand, string Workspace,
    string Text);

/// <summary>Runner → server: what the agent is doing in a thread.</summary>
public record StreamEvent(
    long ThreadId,
    string Kind,            // text | toolStart | toolEnd | turnEnded | error
    string? Text = null,    // text delta, or the error message
    string? ToolId = null,
    string? Title = null,
    string? ToolKind = null,
    string? Status = null);

public record PermissionOption(string Id, string Name, string Kind);
public record PermissionRequestMsg(long ThreadId, string RequestId, string Title, PermissionOption[] Options);
public record PermissionAnswerMsg(string RequestId, string? OptionId);   // null = deny / cancelled

public static class StreamKinds
{
    public const string Text = "text", ToolStart = "toolStart", ToolEnd = "toolEnd", TurnEnded = "turnEnded", Error = "error";
}
