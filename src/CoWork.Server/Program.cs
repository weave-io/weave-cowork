using System.Security.Claims;
using CoWork.Server;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
var dataDir = builder.Configuration["COWORK_DATA"] ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
var dbPath = builder.Configuration["COWORK_DB"] ?? Path.Combine(dataDir, "cowork.db");
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://localhost:5880");

var db = new Db(dbPath);
var migrated = db.Migrate();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<Realtime>();
builder.Services.AddSingleton<VoicePresence>();
builder.Services.AddSingleton(VoiceOptions.From(builder.Configuration));
builder.Services.AddSingleton<IInlineProjection, DirectoryProjection>();        // projections run in this order, inside the transaction that appended the event
builder.Services.AddSingleton<IInlineProjection, ProjectProjection>();
builder.Services.AddSingleton<IInlineProjection, ThreadProjection>();
builder.Services.AddSingleton<IInlineProjection, SandboxProjection>();
builder.Services.AddSingleton<IInlineProjection, ArtifactProjection>();
builder.Services.AddSingleton<IEventStore, SqliteEventStore>();
builder.Services.AddSingleton<IAsyncProjection, AuditProjection>();          // async projections: after the commit, from a checkpoint, rebuildable from zero
builder.Services.AddSingleton<IAsyncProjection, UnreadProjection>();
builder.Services.AddSingleton<IAsyncProjection, ActivityProjection>();
builder.Services.AddSingleton<IAsyncProjection, SearchProjection>();
builder.Services.AddSingleton<ProjectionRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProjectionRunner>());
builder.Services.AddSingleton<LiveQueue>();
builder.Services.AddSingleton<ThreadViews>();
builder.Services.AddHostedService<LivePublisher>();
builder.Services.AddSingleton<RunnerRegistry>();
builder.Services.AddSingleton<PoolRegistry>();
builder.Services.AddSingleton<SandboxRegistry>();
builder.Services.AddSingleton<Sandboxes>();
builder.Services.AddHostedService<SandboxReaper>();
builder.Services.AddSingleton<Gateway>();
builder.Services.AddSingleton<Mcp>();
// artifacts: the bytes under the data directory (beside the database when only COWORK_DB is given)
builder.Services.AddSingleton(ArtifactOptions.From(builder.Configuration,
    builder.Configuration["COWORK_DATA"] ?? (builder.Configuration["COWORK_DB"] is not null ? Path.GetDirectoryName(Path.GetFullPath(dbPath))! : dataDir)));
builder.Services.AddSingleton<ArtifactStore>();
builder.Services.AddSingleton<ArtifactSigner>();
builder.Services.AddSingleton<Artifacts>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<MemberRow>, Microsoft.AspNetCore.Identity.PasswordHasher<MemberRow>>();
builder.Services.AddSignalR().AddHubOptions<RunnerHub>(o =>
{
    // a runner that dies hard (or whose connection is dropped silently, e.g. behind a port proxy) is noticed within seconds
    o.KeepAliveInterval = TimeSpan.FromSeconds(3);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
    o.MaximumReceiveMessageSize = 1024 * 1024;       // a tool's output (up to 64 kB) rides on one message
}).AddHubOptions<PoolHub>(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(3);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
});
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "cowork";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var id = long.Parse(ctx.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
            using var c = ctx.HttpContext.RequestServices.GetRequiredService<Db>().Open();
            if (!Access.IsActivePerson(c, id)) { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(); }
        };
    });

var app = builder.Build();

// a server that kept its state in tables (before the events) is written down as events once, and checked, before anything else reads it
LegacyImporter.Run(db, app.Services.GetRequiredService<IEventStore>(), m => app.Logger.LogInformation("{Message}", m));

// a server that had people before the unread view existed: everything written so far counts as read, once
if (migrated.Any(m => m.EndsWith("005_views.sql")) && db.Open().Using(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM people") > 0))
    app.Services.GetRequiredService<IEventStore>().Append(new[] { new NewEvent("server", "UnreadBaselineSet", new { }) });

// a turn that started and never ended was cut off by a crash or a restart: the thread says so
app.Services.GetRequiredService<Dispatcher>().RecoverInterruptedTurns();

// runners, sandboxes and pools authenticate with a token, stored only as a hash. Each kind of token opens one door: a runner's token and a
// sandbox's thread token the runner hub (the thread token for its own thread only), a pool's token the pool hub. The gateway checks thread
// tokens itself.
app.Use(async (ctx, next) =>
{
    var runner = ctx.Request.Path.StartsWithSegments("/hubs/runner");
    var pool = ctx.Request.Path.StartsWithSegments("/hubs/pool");
    if (runner || pool)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ") ? h[7..] : ctx.Request.Query["access_token"].ToString();
        var ok = false;
        if (!string.IsNullOrEmpty(token))
        {
            using var c = ctx.RequestServices.GetRequiredService<Db>().Open();
            var hash = Tokens.Hash(token);
            if (runner)
            {
                if (c.QueryFirstOrDefault<long?>("SELECT rs.agent_id FROM runner_secrets rs JOIN members m ON m.id=rs.agent_id WHERE rs.token_hash=@hash AND m.removed=0", new { hash }) is { } agentId)
                { ctx.Items["agentId"] = agentId; ok = true; }
                else if (ThreadTokens.Find(c, hash) is { } t)
                { ctx.Items["agentId"] = t.AgentId; ctx.Items["threadId"] = t.ThreadId; ctx.Items["sandboxId"] = t.SandboxId; ok = true; }
            }
            else if (c.QueryFirstOrDefault<long?>("SELECT ps.pool_id FROM pool_secrets ps JOIN pools p ON p.id=ps.pool_id WHERE ps.token_hash=@hash AND p.removed=0", new { hash }) is { } poolId)
            { ctx.Items["poolId"] = poolId; ok = true; }
        }
        if (!ok) { ctx.Response.StatusCode = 401; return; }
    }
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

var wwwroot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(wwwroot))
{
    var fp = new PhysicalFileProvider(wwwroot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
}

app.MapHub<AppHub>("/hubs/app");
app.MapHub<RunnerHub>("/hubs/runner");
app.MapHub<PoolHub>("/hubs/pool");
Gateway.Map(app);
Mcp.Map(app);
Artifacts.Map(app);
Api.Map(app);
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs") || ctx.Request.Path.StartsWithSegments("/gateway")) { ctx.Response.StatusCode = 404; return; }
    var index = Path.Combine(wwwroot, "index.html");
    if (!File.Exists(index)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("The web app isn't built. Run `bun run build` in web/."); return; }
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(index);
});

app.Run();

public partial class Program { }

static class DbExtensions { public static T Using<T>(this Microsoft.Data.Sqlite.SqliteConnection c, Func<Microsoft.Data.Sqlite.SqliteConnection, T> f) { using (c) return f(c); } }
