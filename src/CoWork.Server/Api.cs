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
public record ChannelBody(string? Name, string? Topic, bool? IsPrivate);
public record PostBody(string Body, long? ThreadId);
public record AnswerBody(string? OptionId);
public record RoleBody(string Role);
public record AgentEditBody(string? DisplayName, string? About, string? Instructions, string? HarnessCommand, string Workspace);
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

    static long CreatePerson(Microsoft.Data.Sqlite.SqliteConnection c, IPasswordHasher<MemberRow> hasher, string username, string displayName, string password, string role)
    {
        var id = c.ExecuteScalar<long>("INSERT INTO members(kind, handle, display_name, created_at) VALUES ('person', @h, @n, @now); SELECT last_insert_rowid();",
            new { h = username, n = displayName.Trim(), now = Db.Now() });
        c.Execute("INSERT INTO people VALUES (@id, @ph, @role)", new { id, ph = hasher.HashPassword(new MemberRow(), password), role });
        return id;
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

        api.MapPost("/setup", async (SetupBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher, Realtime rt) =>
        {
            long id;
            using (var c = db.Open())
            {
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM people") > 0) return Err(409, "This server is already set up.");
                if (string.IsNullOrWhiteSpace(b.ServerName) || b.ServerName.Length > 60) return Err(400, "Name the server (up to 60 characters).");
                var bad = ValidateAccount(b.Username, b.DisplayName, b.Password);
                if (bad is not null) return Err(400, bad);
                using var tx = c.BeginTransaction();
                c.Execute("INSERT INTO settings VALUES ('server_name', @n)", new { n = b.ServerName.Trim() }, tx);
                id = CreatePerson(c, hasher, b.Username, b.DisplayName, b.Password, "admin");
                var ch = c.ExecuteScalar<long>("INSERT INTO channels(name, topic, is_private, creator_id, created_at) VALUES ('general', 'Everything that doesn''t have a channel yet', 0, @id, @now); SELECT last_insert_rowid();", new { id, now = Db.Now() }, tx);
                tx.Commit();
            }
            await SignIn(ctx, id);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/auth/login", async (LoginBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher) =>
        {
            using var c = db.Open();
            var row = c.QueryFirstOrDefault<(long Id, string Hash)?>(
                "SELECT m.id, p.password_hash FROM members m JOIN people p ON p.member_id=m.id WHERE m.handle=@u AND m.removed=0", new { u = (b.Username ?? "").Trim() });
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

        api.MapPost("/invites/{code}/accept", async (string code, SignupBody b, HttpContext ctx, Db db, IPasswordHasher<MemberRow> hasher, Realtime rt) =>
        {
            long id;
            using (var c = db.Open())
            {
                var (inv, reason) = CheckInvite(c, code);
                if (reason is not null) return Err(410, reason);
                var bad = ValidateAccount(b.Username, b.DisplayName, b.Password);
                if (bad is not null) return Err(400, bad);
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM members WHERE handle=@u", new { u = b.Username }) > 0) return Err(409, "That username is taken.");
                using var tx = c.BeginTransaction();
                id = CreatePerson(c, hasher, b.Username, b.DisplayName, b.Password, "member");
                c.Execute("UPDATE invites SET uses=uses+1 WHERE code=@code", new { code }, tx);
                tx.Commit();
            }
            await SignIn(ctx, id);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // ---- everything below needs a signed-in person ----
        authed.MapGet("/bootstrap", (HttpContext ctx, Db db, RunnerRegistry runners) =>
        {
            var me = Me(ctx);
            using var c = db.Open();
            var server = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'");
            var members = c.Query<MemberRow>(Q.MemberSelect).Select(m => Q.MemberDto(m, m.Kind == "agent" && runners.IsOnline(m.Id))).ToList();
            var visible = Access.VisibleChannelIds(c, me).ToHashSet();
            var channels = c.Query<ChannelRow>("SELECT * FROM channels ORDER BY name").Where(ch => visible.Contains(ch.Id)).Select(ch => new
            {
                id = ch.Id, name = ch.Name, topic = ch.Topic, isPrivate = ch.IsPrivate, creatorId = ch.CreatorId,
                memberIds = c.Query<long>("SELECT member_id FROM channel_members WHERE channel_id=@id", new { id = ch.Id }).ToList(),
                canManage = Access.CanManage(c, ch, me),
            }).ToList();
            return Results.Ok(new { server = new { name = server }, meId = me, isAdmin = Access.IsAdmin(c, me), members, channels });
        });

        // ---- invite links (admin) ----
        authed.MapGet("/invites", (HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            var rows = c.Query("SELECT i.*, m.display_name AS by_name FROM invites i JOIN members m ON m.id=i.created_by WHERE i.revoked=0 AND i.expires_at>@now ORDER BY i.created_at DESC", new { now = Db.Now() })
                .Select(r => new { code = (string)r.code, createdBy = (long)r.created_by, byName = (string)r.by_name, createdAt = (long)r.created_at, expiresAt = (long)r.expires_at, maxUses = (long)r.max_uses, uses = (long)r.uses });
            return Results.Ok(rows);
        });
        authed.MapPost("/invites", (InviteBody b, HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            var me = Me(ctx);
            if (!Access.IsAdmin(c, me)) return Err(403, "Only admins can do that.");
            var code = Tokens.NewInviteCode();
            var days = Math.Clamp(b.ExpiresInDays ?? 7, 1, 90); var uses = Math.Clamp(b.MaxUses ?? 10, 1, 1000);
            c.Execute("INSERT INTO invites(code, created_by, created_at, expires_at, max_uses) VALUES (@code,@me,@now,@exp,@uses)",
                new { code, me, now = Db.Now(), exp = DateTimeOffset.UtcNow.AddDays(days).ToUnixTimeMilliseconds(), uses });
            return Results.Ok(new { code });
        });
        authed.MapDelete("/invites/{code}", (string code, HttpContext ctx, Db db) =>
        {
            using var c = db.Open();
            if (!Access.IsAdmin(c, Me(ctx))) return Err(403, "Only admins can do that.");
            c.Execute("UPDATE invites SET revoked=1 WHERE code=@code", new { code });
            return Results.Ok(new { ok = true });
        });

        MapChannels(authed);
        MapPosts(authed);
        MapPeople(authed);
        MapAgents(api, authed);
    }

    static (InviteRow? inv, string? reason) CheckInvite(Microsoft.Data.Sqlite.SqliteConnection c, string code)
    {
        var inv = c.QueryFirstOrDefault<InviteRow>("SELECT code, created_by, expires_at, max_uses, uses, revoked FROM invites WHERE code=@code", new { code });
        if (inv is null) return (null, "That invite link doesn't exist.");
        if (inv.Revoked) return (inv, "That invite link was revoked.");
        if (inv.ExpiresAt < Db.Now()) return (inv, "That invite link has expired.");
        if (inv.Uses >= inv.MaxUses) return (inv, "That invite link has been used up.");
        return (inv, null);
    }
}

public class InviteRow
{
    public string Code { get; set; } = "";
    public long CreatedBy { get; set; }
    public long ExpiresAt { get; set; }
    public long MaxUses { get; set; }
    public long Uses { get; set; }
    public bool Revoked { get; set; }
}
