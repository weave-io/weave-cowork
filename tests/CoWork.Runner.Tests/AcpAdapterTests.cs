using System.Text.Json.Nodes;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

public class AcpMappingTests
{
    static JsonNode J(string s) => JsonNode.Parse(s)!;

    [Fact]
    public void Message_chunks_become_text_events()
    {
        var e = AcpAdapter.Map(7, J("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hi"}}""")).Single();
        Assert.Equal((7L, StreamKinds.Text, "Hi"), (e.ThreadId, e.Kind, e.Text));
    }

    [Fact]
    public void Tool_rows_say_what_was_touched()
    {
        var e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"read","kind":"read","locations":[{"path":"src/Api.cs"}]}""")).First();
        Assert.Equal("read src/Api.cs", e.Title);
        e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"glob","kind":"search","rawInput":{"pattern":"**/*.csproj"}}""")).First();
        Assert.Equal("glob **/*.csproj", e.Title);
    }

    [Fact]
    public void A_tool_call_with_no_locations_is_still_described()
    {
        var e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"bash","locations":[],"rawInput":{"command":"ls"}}""")).First();
        Assert.Equal("bash ls", e.Title);
    }

    [Fact]
    public void Tool_calls_start_then_end_and_thoughts_are_ignored()
    {
        var start = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Read a.txt","kind":"read","status":"in_progress"}""")).Single();
        Assert.Equal((StreamKinds.ToolStart, "c1", "Read a.txt", "read", "running"), (start.Kind, start.ToolId, start.Title, start.ToolKind, start.Status));
        var end = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}""")).Single();
        Assert.Equal((StreamKinds.ToolEnd, "c1", "completed"), (end.Kind, end.ToolId, end.Status));
        Assert.Empty(AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"in_progress"}""")));
        Assert.Empty(AcpAdapter.Map(1, J("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"hmm"}}""")));
    }
}

public class AcpAdapterTests
{
    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public readonly TaskCompletionSource<PermissionRequestMsg> Asked = new();
        public string? Answer = "allow";
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) { Asked.TrySetResult(r); return Task.FromResult(Answer); }
        public async Task WaitTurnEnd(long thread, int already = 0)
        {
            for (var i = 0; i < 600 && Events.Count(e => e.ThreadId == thread && e.Kind == StreamKinds.TurnEnded) <= already; i++) await Task.Delay(50);
        }
    }

    static DeliverMessage Msg(long thread, string text, string instructions = "Be brief.") =>
        new(1, thread, 1, "reviewer", instructions, new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") }, Path.GetTempPath(), text);

    [Fact]
    public async Task A_turn_streams_text_and_tool_rows_then_ends()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(5, "hello"));
        await sink.WaitTurnEnd(5);
        var kinds = sink.Events.Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { StreamKinds.Text, StreamKinds.ToolStart, StreamKinds.ToolEnd, StreamKinds.Text, StreamKinds.TurnEnded }, kinds);
    }

    [Fact]
    public async Task Instructions_go_in_the_first_prompt_of_a_session_only_and_threads_get_own_sessions()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(1, "ECHO one"));
        await adapter.Deliver(Msg(1, "ECHO two"));
        await adapter.Deliver(Msg(2, "ECHO three"));
        await sink.WaitTurnEnd(2);
        string Said(long t, int i) => sink.Events.Where(e => e.ThreadId == t && e.Kind == StreamKinds.Text).ElementAt(i).Text!;
        Assert.Contains("Be brief.", Said(1, 0));
        Assert.DoesNotContain("Be brief.", Said(1, 1));
        Assert.Contains("Be brief.", Said(2, 0));      // a new thread is a new session
    }

    [Fact]
    public async Task A_permission_request_goes_to_the_sink_and_the_answer_goes_back_to_the_agent()
    {
        var sink = new Sink { Answer = "allow" };
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(3, "PERMISSION please"));
        var asked = await sink.Asked.Task;
        Assert.Equal(("Run ls", 2), (asked.Title, asked.Options.Length));
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.Text && e.Text!.Contains("\"selected\"") && e.Text.Contains("\"allow\""));

        var deny = new Sink { Answer = null };
        await using var adapter2 = new AcpAdapter(deny);
        await adapter2.Deliver(Msg(4, "PERMISSION please"));
        await deny.WaitTurnEnd(4);
        Assert.Contains(deny.Events, e => e.Kind == StreamKinds.Text && e.Text!.Contains("cancelled"));
    }

    static string StorePath() => Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}", "sessions.json");

    [Fact]
    public async Task A_restarted_runner_loads_each_threads_saved_session_without_replaying_it()
    {
        var path = StorePath();
        var first = new Sink();
        await using (var a1 = new AcpAdapter(first, new SessionStore(path))) await a1.Deliver(Msg(1, "ECHO before"));
        var firstText = first.Events.Single(e => e.Kind == StreamKinds.Text).Text!;
        Assert.Contains("Be brief.", firstText);
        var savedId = new SessionStore(path).Get(1);
        Assert.NotNull(savedId);

        var second = new Sink();                                  // a "restart": a new adapter and a new harness process, the same state file
        await using (var a2 = new AcpAdapter(second, new SessionStore(path))) await a2.Deliver(Msg(1, "ECHO after"));
        await second.WaitTurnEnd(1);
        var text = Assert.Single(second.Events, e => e.Kind == StreamKinds.Text).Text!;     // the harness's REPLAYED chunk was dropped
        Assert.Contains("LOADED:" + savedId, text);
        Assert.DoesNotContain("Be brief.", text);                  // it's the same session: no instructions again
        Assert.Equal(savedId, new SessionStore(path).Get(1));
    }

    [Fact]
    public async Task If_a_saved_session_will_not_load_a_new_one_starts_and_is_saved()
    {
        var path = StorePath();
        new SessionStore(path).Set(1, "bad-session");
        var sink = new Sink();
        await using (var a = new AcpAdapter(sink, new SessionStore(path))) await a.Deliver(Msg(1, "ECHO hi"));
        var text = Assert.Single(sink.Events, e => e.Kind == StreamKinds.Text).Text!;
        Assert.Contains("Be brief.", text);
        Assert.DoesNotContain("LOADED", text);
        Assert.NotEqual("bad-session", new SessionStore(path).Get(1));
    }

    [Fact]
    public async Task A_harness_that_cannot_start_ends_the_turn_with_an_error()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        var m = Msg(9, "hi") with { HarnessCommand = new[] { "definitely-not-a-real-command-xyz" } };
        await adapter.Deliver(m);
        await sink.WaitTurnEnd(9);
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.Error);
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.TurnEnded);
    }
}

public class ConfigTests
{
    [Fact]
    public void Saved_agents_round_trip_and_the_file_is_private()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}", "agents.json");
        var cfg = new RunnerConfig(path);
        cfg.Add(new SavedAgent("http://localhost:5880", 3, "reviewer", "tok"));
        cfg.Add(new SavedAgent("http://localhost:5880", 3, "reviewer", "tok2"));     // re-enrolling replaces
        var all = cfg.Load();
        Assert.Equal("tok2", Assert.Single(all).Token);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        cfg.Remove(all[0]);
        Assert.Empty(cfg.Load());
    }
}
