using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

public class AuthAndMembershipTests
{
    [Fact]
    public async Task Api_needs_sign_in_and_setup_runs_once()
    {
        using var app = new TestApp();
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/bootstrap")).StatusCode);
        // setup needs someone the provider signed in
        var nobody = await anon.PostAsJsonAsync("/api/setup", new { serverName = "x", handle = "mallory", displayName = "M" });
        Assert.Equal(HttpStatusCode.Unauthorized, nobody.StatusCode);
        await app.Setup();
        var mallory = new Person(app, "mallory");
        await app.Sso(mallory, FakeIssuer.For("mallory"), "setup", serverName: "x");
        var again = await mallory.Http.PostAsJsonAsync("/api/setup", new { serverName = "x", handle = "mallory", displayName = "M" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Non_member_cannot_read_post_or_subscribe_to_a_private_channel()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var priv = await alex.Channel("leads", priv: true);
        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.GetAsync($"/api/channels/{priv}/posts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.PostAsJsonAsync($"/api/channels/{priv}/posts", new { body = "hi" })).StatusCode);

        var hub = rosa.Hub();
        var heard = new List<JsonElement>();
        hub.On<JsonElement>("post-created", p => heard.Add(p));
        await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("Subscribe", priv));
        await alex.Post(priv, "secret");
        await Task.Delay(300);                       // negative check: nothing to wait on
        Assert.Empty(heard);
        // once added, she hears it
        var got = new TaskCompletionSource<JsonElement>();
        hub.On<JsonElement>("post-created", p => got.TrySetResult(p));
        (await alex.Http.PutAsync($"/api/channels/{priv}/members/{rosa.Id}", null)).EnsureSuccessStatusCode();
        await alex.Post(priv, "now you can see this");
        var p = await got.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("now you can see this", p.GetProperty("body").GetString());
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Only_admin_or_creator_can_manage_a_channel_and_authors_come_from_the_caller()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var ch = await alex.Channel("checkout-app");
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.DeleteAsync($"/api/channels/{ch}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PatchAsJsonAsync($"/api/channels/{ch}", new { name = "x" })).StatusCode);
        var post = await rosa.Post(ch, "hello");
        Assert.Equal(rosa.Id, post.GetProperty("authorId").GetInt64());
        var own = await rosa.Channel("rosas");
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.DeleteAsync($"/api/channels/{own}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rosa.Http.PostAsJsonAsync("/api/invites", new { })).StatusCode);
    }

    [Fact]
    public async Task Removed_person_is_signed_out()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
        (await alex.Http.DeleteAsync($"/api/people/{rosa.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
        // and signing in again with single sign-on doesn't let her back in
        var again = new Person(app, "rosa");
        Assert.Equal("/", await app.Sso(again, FakeIssuer.For("rosa")));
        Assert.Equal(HttpStatusCode.Unauthorized, (await again.Http.GetAsync("/api/bootstrap")).StatusCode);
    }
}

public class InviteTests
{
    static async Task<HttpResponseMessage> Accept(TestApp app, string code, string user)
    {
        var p = new Person(app, user);
        await app.Sso(p, FakeIssuer.For(user), "invite", code);
        return await p.Http.PostAsJsonAsync($"/api/invites/{code}/accept", new { handle = user, displayName = user });
    }

    [Fact]
    public async Task Revoked_expired_and_used_up_links_are_refused_with_a_reason()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var revoked = await app.Invite(alex);
        (await alex.Http.DeleteAsync($"/api/invites/{revoked}")).EnsureSuccessStatusCode();
        var r = await Accept(app, revoked, "a1");
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode); Assert.Contains("revoked", await r.Content.ReadAsStringAsync());

        var expired = await app.Invite(alex);
        using (var c = app.Sql()) c.Execute("UPDATE invites SET expires_at=1 WHERE id=(SELECT invite_id FROM invite_codes WHERE code=@expired)", new { expired });
        r = await Accept(app, expired, "a2");
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode); Assert.Contains("expired", await r.Content.ReadAsStringAsync());

        var once = await app.Invite(alex);
        using (var c = app.Sql()) c.Execute("UPDATE invites SET max_uses=1 WHERE id=(SELECT invite_id FROM invite_codes WHERE code=@once)", new { once });
        Assert.Equal(HttpStatusCode.OK, (await Accept(app, once, "a3")).StatusCode);
        r = await Accept(app, once, "a4");
        Assert.Equal(HttpStatusCode.Gone, r.StatusCode); Assert.Contains("used up", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_new_person_is_a_member_not_an_admin()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        using var c = app.Sql();
        Assert.Equal("member", c.ExecuteScalar<string>("SELECT role FROM people WHERE member_id=@id", new { id = rosa.Id }));
    }

    [Fact]
    public async Task An_agent_can_be_edited_by_its_owner_or_an_admin_but_not_by_others()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa"); var sam = await app.Join(alex, "sam", "Sam");
        var made = await (await rosa.Http.PostAsJsonAsync("/api/agents", new { handle = "reviewer", about = "a", instructions = "secret plan", harnessCommand = "opencode acp", workspace = "/w" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = made.GetProperty("agentId").GetInt64();
        Assert.Equal("reviewer", made.GetProperty("handle").GetString());
        var body = new { displayName = "Reviewer", about = "new about", instructions = "new instructions", harnessCommand = "/usr/bin/opencode acp --flag \"two words\"", workspace = "/w2" };
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.Http.PutAsJsonAsync($"/api/agents/{id}", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sam.Http.GetAsync($"/api/agents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rosa.Http.PutAsJsonAsync($"/api/agents/{id}", body with { workspace = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.PutAsJsonAsync($"/api/agents/{id}", body)).StatusCode);       // the owner
        Assert.Equal(HttpStatusCode.OK, (await alex.Http.PutAsJsonAsync($"/api/agents/{id}", body with { about = "by admin" })).StatusCode);   // an admin
        var now = await alex.Http.GetFromJsonAsync<JsonElement>($"/api/agents/{id}");
        Assert.Equal(("by admin", "new instructions", "/w2", "Reviewer"), (now.GetProperty("about").GetString(), now.GetProperty("instructions").GetString(), now.GetProperty("workspace").GetString(), now.GetProperty("displayName").GetString()));
        Assert.Equal("/usr/bin/opencode acp --flag \"two words\"", now.GetProperty("harnessCommand").GetString());
        using var c = app.Sql();
        Assert.Equal("[\"/usr/bin/opencode\",\"acp\",\"--flag\",\"two words\"]", c.ExecuteScalar<string>("SELECT harness_command FROM agents WHERE member_id=" + id));
        var boot = await rosa.Http.GetStringAsync("/api/bootstrap");
        Assert.DoesNotContain("new instructions", boot);        // instructions only go to the owner and admins
    }
}
