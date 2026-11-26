using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;

namespace CoWork.Server.Tests;

/// <summary>Brief 11: people sign in through the server's OpenID Connect provider, never with a password. These go through the real
/// OpenID Connect handler against <see cref="FakeIssuer"/>: the redirect to the provider, its callback with a code, the token exchange.</summary>
public class SsoTests
{
    static async Task<JsonElement> Pending(Person p) => (await p.Http.GetFromJsonAsync<JsonElement>("/api/auth/pending")).GetProperty("pending");
    static async Task<JsonElement> Status(TestApp app) => await app.CreateClient().GetFromJsonAsync<JsonElement>("/api/setup/status");

    [Fact]
    public async Task The_first_person_sets_up_the_server_through_the_provider_and_is_admin()
    {
        using var app = new TestApp();
        var st = await Status(app);
        Assert.True(st.GetProperty("needsSetup").GetBoolean()); Assert.True(st.GetProperty("sso").GetBoolean()); Assert.False(st.GetProperty("passwordAccounts").GetBoolean());

        var alex = new Person(app, "alex");
        var next = await app.Sso(alex, new FakeIssuer.Identity("00u1alex", "alex.kim@lumen.test", "Alex Kim", "Alex.Kim@lumen.test"), "setup", serverName: "Lumen Labs");
        Assert.Equal("/", next);
        Assert.Equal(1, app.Issuer.TokenRequests);                                                   // the code was exchanged at the provider
        Assert.Equal(HttpStatusCode.Unauthorized, (await alex.Http.GetAsync("/api/bootstrap")).StatusCode);        // not signed in until set up
        var pending = await Pending(alex);
        Assert.Equal("setup", pending.GetProperty("intent").GetString());
        Assert.Equal("Lumen Labs", pending.GetProperty("serverName").GetString());
        Assert.Equal("alex.kim@lumen.test", pending.GetProperty("email").GetString());
        Assert.Equal("alex-kim", pending.GetProperty("handle").GetString());              // from the provider's username, made to fit
        Assert.Equal("Alex Kim", pending.GetProperty("displayName").GetString());

        var r = await alex.Http.PostAsJsonAsync("/api/setup", new { serverName = "Lumen Labs", handle = "alex", displayName = "Alex" });
        r.EnsureSuccessStatusCode();
        var boot = await alex.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.True(boot.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(JsonValueKind.Null, (await Pending(alex)).ValueKind);                // the pending sign-in is gone
        using var c = app.Sql();
        var login = c.QuerySingle("SELECT issuer, subject, email, member_id FROM logins");
        Assert.Equal(FakeIssuer.Authority, (string)login.issuer); Assert.Equal("00u1alex", (string)login.subject);
        Assert.Equal("alex.kim@lumen.test", (string)login.email); Assert.Equal(boot.GetProperty("meId").GetInt64(), (long)login.member_id);
        Assert.False((await Status(app)).GetProperty("needsSetup").GetBoolean());
    }

    [Fact]
    public async Task A_member_signs_out_and_back_in_as_the_same_member()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var id = rosa.Id;
        (await rosa.Http.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
        var back = await app.SignIn("rosa");
        Assert.Equal(id, (await back.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap")).GetProperty("meId").GetInt64());
        Assert.Equal(2, app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM people"));
    }

    [Fact]
    public async Task Someone_the_provider_knows_but_who_has_no_invite_is_told_and_not_let_in()
    {
        using var app = new TestApp();
        await app.Setup();
        var sam = new Person(app, "sam");
        Assert.Equal("/", await app.Sso(sam, new FakeIssuer.Identity("00u9sam", "sam.patel@elsewhere.test", "Sam Patel", null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.Http.GetAsync("/api/bootstrap")).StatusCode);
        var pending = await Pending(sam);
        Assert.Equal("signin", pending.GetProperty("intent").GetString());
        Assert.Equal("sam.patel@elsewhere.test", pending.GetProperty("email").GetString());
        Assert.Equal("sam-patel", pending.GetProperty("handle").GetString());           // no username from the provider: the email's local part
        // "Use another account" forgets who they were
        (await sam.Http.DeleteAsync("/api/auth/pending")).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await Pending(sam)).ValueKind);
        Assert.Equal(1, app.Sql().ExecuteScalar<int>("SELECT COUNT(*) FROM people"));
    }

    [Fact]
    public async Task Who_someone_is_is_the_issuer_and_subject_never_the_email()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); await app.Join(alex, "rosa", "Rosa");
        // the same email under another subject is somebody else
        var other = new Person(app, "impostor");
        await app.Sso(other, new FakeIssuer.Identity("someone-else", FakeIssuer.For("rosa").Email, "Rosa", "rosa"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.Http.GetAsync("/api/bootstrap")).StatusCode);
        // the same subject from another issuer is somebody else too
        using (var c = app.Sql()) c.Execute("UPDATE logins SET issuer='https://another-provider.test' WHERE subject=@s", new { s = FakeIssuer.For("rosa").Subject });
        var rosa = new Person(app, "rosa");
        await app.Sso(rosa, FakeIssuer.For("rosa"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task A_member_who_opens_an_invite_is_signed_in_and_the_invite_is_not_used()
    {
        using var app = new TestApp();
        var alex = await app.Setup(); await app.Join(alex, "rosa", "Rosa");
        var code = await app.Invite(alex);
        var rosa = new Person(app, "rosa");
        Assert.Equal("/", await app.Sso(rosa, FakeIssuer.For("rosa"), "invite", code));
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
        Assert.Equal(0, app.Sql().ExecuteScalar<int>("SELECT uses FROM invites i JOIN invite_codes ic ON ic.invite_id=i.id WHERE ic.code=@code", new { code }));
    }

    [Fact]
    public async Task Joining_checks_the_handle_and_needs_the_provider()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var code = await app.Invite(alex);
        var nobody = await app.CreateClient().PostAsJsonAsync($"/api/invites/{code}/accept", new { handle = "rosa", displayName = "Rosa" });
        Assert.Equal(HttpStatusCode.Unauthorized, nobody.StatusCode);

        var rosa = new Person(app, "rosa");
        Assert.Equal($"/invite/{code}", await app.Sso(rosa, FakeIssuer.For("rosa"), "invite", code));       // back to the invite, to choose a handle
        var taken = await rosa.Http.PostAsJsonAsync($"/api/invites/{code}/accept", new { handle = "alex", displayName = "Rosa" });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("Someone here is already @alex. Try another.", await taken.Content.ReadAsStringAsync());
        var bad = await rosa.Http.PostAsJsonAsync($"/api/invites/{code}/accept", new { handle = "Rosa Ortega", displayName = "Rosa" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        (await rosa.Http.PostAsJsonAsync($"/api/invites/{code}/accept", new { handle = "rosa", displayName = "Rosa" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task When_the_provider_says_no_nobody_is_signed_in()
    {
        using var app = new TestApp();
        await app.Setup();
        var sam = new Person(app, "sam");
        Assert.Equal("/?sso=failed", await app.Sso(sam, FakeIssuer.For("sam"), refuse: true));
        Assert.Equal(JsonValueKind.Null, (await Pending(sam)).ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await sam.Http.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task There_are_no_passwords()
    {
        using var app = new TestApp();
        await app.Setup();
        var r = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new { username = "alex", password = "correct horse" });
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        using var c = app.Sql();
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='credentials'"));
    }

    [Fact]
    public async Task A_server_without_a_provider_says_so_and_has_no_way_in()
    {
        using var app = new TestApp(sso: false);
        Assert.False((await Status(app)).GetProperty("sso").GetBoolean());
        var r = await app.CreateClient(new() { AllowAutoRedirect = false }).GetAsync("/auth/sso?intent=setup");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/", r.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_server_from_before_single_sign_on_says_its_password_accounts_cant_sign_in()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cowork-legacy-{Guid.NewGuid():N}.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-main-round1.db"), path);
        try
        {
            using var app = new TestApp(existingDb: path);
            var st = await Status(app);
            Assert.False(st.GetProperty("needsSetup").GetBoolean());
            Assert.True(st.GetProperty("passwordAccounts").GetBoolean());
            var rosa = new Person(app, "rosa");
            await app.Sso(rosa, FakeIssuer.For("rosa"));                 // nobody's account is linked by itself
            Assert.Equal(HttpStatusCode.Unauthorized, (await rosa.Http.GetAsync("/api/bootstrap")).StatusCode);
        }
        finally { foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch { } }
    }
}
