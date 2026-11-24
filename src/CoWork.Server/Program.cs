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
db.Migrate();
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<Realtime>();
builder.Services.AddSingleton<RunnerRegistry>();
builder.Services.AddSingleton<Dispatcher>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Identity.IPasswordHasher<MemberRow>, Microsoft.AspNetCore.Identity.PasswordHasher<MemberRow>>();
builder.Services.AddSignalR();
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

// runners authenticate with a token, stored only as a hash
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/hubs/runner"))
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ") ? h[7..] : ctx.Request.Query["access_token"].ToString();
        long? agentId = null;
        if (!string.IsNullOrEmpty(token))
        {
            using var c = ctx.RequestServices.GetRequiredService<Db>().Open();
            agentId = c.QueryFirstOrDefault<long?>("SELECT a.member_id FROM agents a JOIN members m ON m.id=a.member_id WHERE a.token_hash=@h AND m.removed=0", new { h = Tokens.Hash(token) });
        }
        if (agentId is null) { ctx.Response.StatusCode = 401; return; }
        ctx.Items["agentId"] = agentId.Value;
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
Api.Map(app);
app.MapFallback(async ctx =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs")) { ctx.Response.StatusCode = 404; return; }
    var index = Path.Combine(wwwroot, "index.html");
    if (!File.Exists(index)) { ctx.Response.StatusCode = 404; await ctx.Response.WriteAsync("The web app isn't built. Run `bun run build` in web/."); return; }
    ctx.Response.ContentType = "text/html";
    await ctx.Response.SendFileAsync(index);
});

app.Run();

public partial class Program { }
