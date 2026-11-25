using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoWork.Contracts;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace CoWork.Server;

public record SetupBody(string ServerName, string Username, string DisplayName, string Password);
public record LoginBody(string Username, string Password);
public record SignupBody(string Username, string DisplayName, string Password);
public record InviteBody(int? MaxUses, int? ExpiresInDays);
public record ChannelBody(string? Name, string? Topic, bool? IsPrivate, string? Kind = null, long? ProjectId = null);
public record PostBody(string Body, long? ThreadId);
public record AnswerBody(string? OptionId);
public record RoleBody(string Role);
public record AgentEditBody(string? DisplayName, string? About, string? Instructions, string? HarnessCommand, string Workspace, bool? RecordToolOutput = null);
public record AgentBody(string Handle, string? DisplayName, string? About, string? Instructions, string? HarnessCommand, string Workspace);

public static partial class Api
{
    static readonly Regex HandleRx = new(@"^[a-z0-9][a-z0-9_-]{1,30}$");
    static readonly Regex ChannelRx = new(@"^[a-z0-9][a-z0-9_-]{0,39}$");

    static long Me(HttpContext c) => long.Parse(c.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    static IResult Err(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    static async Task SignIn(HttpContext ctx, long memberId)
    {
        var id = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, memberId.ToString()) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(id), new AuthenticationProperties { IsPersistent = true });
    }

    static string? ValidateAccount(string username, string displayName, string password)
    {
        if (!HandleRx.IsMatch(username)) return "Usernames are 2–31 characters: lowercase letters, numbers, - and _.";
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 60) return "Add a display name (up to 60 characters).";
        if (password is null || password.Length < 8) return "Passwords need at least 8 characters.";
        return null;
    }

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");
        var authed = api.MapGroup("").RequireAuthorization();

        // ---- setup, sign in, sign out ----
        api.MapGet("/setup/status", (Db db) =>
        {
            using var c = db.Open();
            var needs = c.ExecuteScalar<int>("SELECT COUNT(*) FROM people") == 0;
            var name = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'");
            return Results.Ok(new { needsSetup = needs, serverName = name });
        });

        api.MapPost("/setup", async (SetupBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher, IEventStore store) =>
        {
            using (var c = db.Open())
            {
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM people") > 0) return Err(409, "This server is already set up.");
                if (string.IsNullOrWhiteSpace(b.ServerName) || b.ServerName.Length > 60) return Err(400, "Name the server (up to 60 characters).");
                var bad = ValidateAccount(b.Username, b.DisplayName, b.Password);
                if (bad is not null) return Err(400, bad);
            }
            var hash = hasher.HashPassword(new MemberRow(), b.Password);
            long id = 0; var already = false;
            store.Append(d =>
            {
                already = d.Scalar<int>("SELECT COUNT(*) FROM people") > 0;            // two setups at once: the second one loses
                if (already) return Array.Empty<NewEvent>();
                id = d.NextId("members"); var general = d.NextId("channels");
                d.Execute("INSERT OR REPLACE INTO credentials VALUES (@id,@hash)", new { id, hash });
                return new[]
                {
                    new NewEvent("server", "ServerSetUp", new { serverName = b.ServerName.Trim() }, id),
                    new NewEvent("server", "PersonJoined", new { memberId = id, handle = b.Username, displayName = b.DisplayName.Trim(), role = "admin" }, id, CausedByIndex: 0),
                    new NewEvent($"channel-{general}", "ChannelCreated", new { channelId = general, name = "general", topic = "Everything that doesn't have a channel yet", isPrivate = false, kind = "text", creatorId = id }, id, CausedByIndex: 0),
                };
            });
            if (already) return Err(409, "This server is already set up.");
            await SignIn(ctx, id);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/auth/login", async (LoginBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher) =>
        {
            using var c = db.Open();
            var row = c.QueryFirstOrDefault<(long Id, string Hash)?>(
                "SELECT m.id, cr.password_hash FROM members m JOIN people p ON p.member_id=m.id JOIN credentials cr ON cr.member_id=m.id WHERE m.handle=@u AND m.removed=0", new { u = (b.Username ?? "").Trim() });
            if (row is null) { hasher.HashPassword(new MemberRow(), "x"); return Err(401, "Wrong username or password."); }
            var res = hasher.VerifyHashedPassword(new MemberRow(), row.Value.Hash, b.Password ?? "");
            if (res == PasswordVerificationResult.Failed) return Err(401, "Wrong username or password.");
            await SignIn(ctx, row.Value.Id);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/auth/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(new { ok = true }); });

        // ---- invite links (public part) ----
        api.MapGet("/invites/{code}", (string code, Db db) =>
        {
            using var c = db.Open();
            var (inv, reason) = CheckInvite(c, code);
            var name = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'");
            var by = inv is null ? null : c.ExecuteScalar<string>("SELECT display_name FROM members WHERE id=@id", new { id = inv.CreatedBy });
            return Results.Ok(new { valid = reason is null, reason, serverName = name, invitedBy = by });
        });

        api.MapPost("/invites/{code}/accept", async (string code, SignupBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher, Realtime rt, IEventStore store) =>
        {
            using (var c = db.Open())
            {
                var (_, reason) = CheckInvite(c, code);
                if (reason is not null) return Err(410, reason);
                var bad = ValidateAccount(b.Username, b.DisplayName, b.Password);
                if (bad is not null) return Err(400, bad);
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM members WHERE handle=@u", new { u = b.Username }) > 0) return Err(409, "That username is taken.");
            }
            var hash = hasher.HashPassword(new MemberRow(), b.Password);
            long id = 0; string? refused = null; var status = 0;
            store.Append(d =>
            {
                refused = null;
                var inv = d.Query<InviteRow>(InviteSql, new { code }).FirstOrDefault();       // again, inside the write: two people may be using the last place
                var (_, reason) = CheckInvite(inv);
                if (reason is not null) { refused = reason; status = 410; return Array.Empty<NewEvent>(); }
                if (d.Scalar<int>("SELECT COUNT(*) FROM members WHERE handle=@u", new { u = b.Username }) > 0) { refused = "That username is taken."; status = 409; return Array.Empty<NewEvent>(); }
                id = d.NextId("members");
                d.Execute("INSERT OR REPLACE INTO credentials VALUES (@id,@hash)", new { id, hash });
                return new[]
                {
                    new NewEvent("server", "PersonJoined", new { memberId = id, handle = b.Username, displayName = b.DisplayName.Trim(), role = "member", inviteId = inv!.Id }, id),
                    new NewEvent("server", "InviteUsed", new { inviteId = inv.Id, memberId = id }, id, CausedByIndex: 0),
                };
            });
            if (refused is not null) return Err(status, refused);
            await SignIn(ctx, id);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // ---- everything below needs a signed-in person ----
        authed.MapGet("/bootstrap", (HttpContext ctx, Db db, RunnerRegistry runners, VoicePresence voice, VoiceOptions vo, IEventStore store) =>
        {
            var me = Me(ctx);
            var position = store.Head();          // read first: a browser catches up from here, so nothing after it can be missed
            using var c = db.Open();
            var server = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'");
            var members = c.Query<MemberRow>(Q.MemberSelect).Select(m => Q.MemberDto(m, m.Kind == "agent" && runners.IsOnline(m.Id))).ToList();
            var visible = Access.VisibleChannelIds(c, me).ToHashSet();
            var channels = c.Query<ChannelRow>("SELECT * FROM channels ORDER BY name").Where(ch => visible.Contains(ch.Id)).Select(ch => new
            {
                id = ch.Id, name = ch.Name, kind = ch.Kind, topic = ch.Topic, isPrivate = ch.IsPrivate, creatorId = ch.CreatorId,
                memberIds = c.Query<long>("SELECT member_id FROM channel_audience WHERE channel_id=@id ORDER BY member_id", new { id = ch.Id }).ToList(),
                projectId = ch.ProjectId,
                canManage = Access.CanManage(c, ch, me),
            }).ToList();
            var isAdmin = Access.IsAdmin(c, me);
            var projects = c.Query<ProjectRow>("SELECT * FROM projects ORDER BY name").Where(p => Access.CanSeeProject(c, p.Id, me))
                .Select(p => new { id = p.Id, name = p.Name, about = p.About, role = Access.ProjectRole(c, p.Id, me), canEdit = Access.CanEditProject(c, p.Id, me) }).ToList();
            var presence = voice.Snapshot().Where(kv => visible.Contains(kv.Key)).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            return Results.Ok(new { position, server = new { name = server }, meId = me, isAdmin, members, channels, projects,
                voice = new { enabled = vo.Enabled, url = vo.Url }, voicePresence = presence });
        });

        // ---- invite links (admin) ----
        authed.MapGet("/invites", (HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            var rows = c.Query(@"SELECT ic.code, i.created_by, i.created_at, i.expires_at, i.max_uses, i.uses, m.display_name AS by_name
                    FROM invites i JOIN invite_codes ic ON ic.invite_id=i.id JOIN members m ON m.id=i.created_by
                    WHERE i.revoked=0 AND i.expires_at>@now ORDER BY i.created_at DESC, i.id DESC", new { now = Db.Now() })
                .Select(r => new { code = (string)r.code, createdBy = (long)r.created_by, byName = (string)r.by_name, createdAt = (long)r.created_at, expiresAt = (long)r.expires_at, maxUses = (long)r.max_uses, uses = (long)r.uses });
            return Results.Ok(rows);
        });
        authed.MapPost("/invites", (InviteBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            using (var c = db.Open()) if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            var me = Me(ctx);
            var code = Tokens.NewInviteCode();
            var days = Math.Clamp(b.ExpiresInDays ?? 7, 1, 90); var uses = Math.Clamp(b.MaxUses ?? 10, 1, 1000);
            store.Append(d =>
            {
                var id = d.NextId("invites");
                d.Execute("INSERT OR REPLACE INTO invite_codes(invite_id, code) VALUES (@id,@code)", new { id, code });       // the code is a secret: it stays out of the event
                return new[] { new NewEvent("server", "InviteCreated", new { inviteId = id, createdBy = me, expiresAt = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeMilliseconds(), maxUses = uses }, me) };
            });
            return Results.Ok(new { code });
        });
        authed.MapDelete("/invites/{code}", (string code, HttpContext ctx, Db db, IEventStore store) =>
        {
            using var c = db.Open();
            var me = Me(ctx);
            if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            if (c.ExecuteScalar<long?>("SELECT invite_id FROM invite_codes WHERE code=@code", new { code }) is { } inviteId)
                store.Append(new[] { new NewEvent("server", "InviteRevoked", new { inviteId }, me) });
            return Results.Ok(new { ok = true });
        });

        MapChannels(authed);
        MapPosts(authed);
        MapPeople(authed);
        MapAgents(api, authed);
        MapViews(authed);
        MapProjects(authed);
    }

    const string InviteSql = "SELECT i.id, ic.code, i.created_by, i.expires_at, i.max_uses, i.uses, i.revoked FROM invite_codes ic JOIN invites i ON i.id=ic.invite_id WHERE ic.code=@code";

    static (InviteRow? inv, string? reason) CheckInvite(Microsoft.Data.Sqlite.SqliteConnection c, string code) => CheckInvite(c.QueryFirstOrDefault<InviteRow>(InviteSql, new { code }));

    static (InviteRow? inv, string? reason) CheckInvite(InviteRow? inv)
    {
        if (inv is null) return (null, "That invite link doesn't exist.");
        if (inv.Revoked) return (inv, "That invite link was revoked.");
        if (inv.ExpiresAt < Db.Now()) return (inv, "That invite link has expired.");
        if (inv.Uses >= inv.MaxUses) return (inv, "That invite link has been used up.");
        return (inv, null);
    }
}

public class InviteRow
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public long CreatedBy { get; set; }
    public long ExpiresAt { get; set; }
    public long MaxUses { get; set; }
    public long Uses { get; set; }
    public bool Revoked { get; set; }
}
