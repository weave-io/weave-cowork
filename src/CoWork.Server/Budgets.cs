using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>
/// A project's budget for its team templates, enforced by the gateway: a month and a thread at most, each in tokens and in dollars. Dollars
/// count only what the provider reports as cost; a provider that reports none (GitHub Copilot) is held by the token caps. A lead can raise one
/// thread's cap (it gets the project's per-thread cap again). Pinned agents use their owners' own access and aren't counted.
/// </summary>
public static class Budgets
{
    public static string Month(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    public static string MonthName(string month) => DateTime.ParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM", CultureInfo.InvariantCulture);

    public sealed class Caps { public long? MonthTokens { get; set; } public double? MonthUsd { get; set; } public long? ThreadTokens { get; set; } public double? ThreadUsd { get; set; } }
    public sealed class Use { public long Tokens { get; set; } public double? Cost { get; set; } }
    public sealed record Reached(string Scope, string Unit, double Limit, double Used, string Text);

    public static Caps Of(SqliteConnection c, long projectId) =>
        c.QueryFirstOrDefault<Caps>("SELECT month_tokens, month_usd, thread_tokens, thread_usd FROM budgets WHERE project_id=@projectId", new { projectId }) ?? new Caps();

    public static Use ThreadUse(SqliteConnection c, long threadId) =>
        c.QueryFirst<Use>("SELECT COALESCE(SUM(prompt_tokens + completion_tokens),0) AS Tokens, SUM(cost) AS Cost FROM model_usage WHERE thread_id=@threadId", new { threadId });

    public static Use MonthUse(SqliteConnection c, long projectId, string month) =>
        c.QueryFirst<Use>("SELECT COALESCE(SUM(tokens),0) AS Tokens, SUM(cost) AS Cost FROM project_usage WHERE project_id=@projectId AND month=@month", new { projectId, month });

    /// <summary>The thread's own caps: the project's per-thread caps plus what leads added to this thread.</summary>
    public static (long? Tokens, double? Usd) ThreadCaps(SqliteConnection c, long threadId, Caps caps)
    {
        var extra = c.QueryFirstOrDefault<(long Tokens, double Usd)>("SELECT extra_tokens, extra_usd FROM thread_budgets WHERE thread_id=@threadId", new { threadId });
        return (caps.ThreadTokens is { } t ? t + extra.Tokens : null, caps.ThreadUsd is { } u ? u + extra.Usd : null);
    }

    public static string Count(double n, string unit) => unit == "usd" ? "$" + n.ToString(n < 1 ? "0.00##" : "N2", CultureInfo.InvariantCulture) : ((long)n).ToString("N0", CultureInfo.InvariantCulture) + " tokens";

    /// <summary>The first cap this thread has reached, or null while it may go on.</summary>
    public static Reached? Check(SqliteConnection c, long threadId, long projectId, long now)
    {
        var caps = Of(c, projectId);
        var project = c.ExecuteScalar<string>("SELECT name FROM projects WHERE id=@projectId", new { projectId }) ?? "the project";
        var thread = ThreadUse(c, threadId);
        var (tTokens, tUsd) = ThreadCaps(c, threadId, caps);
        if (tTokens is { } tt && thread.Tokens >= tt) return new("thread", "tokens", tt, thread.Tokens, $"Reached this thread's cap of {Count(tt, "tokens")}. A lead can raise it.");
        if (tUsd is { } tu && (thread.Cost ?? 0) >= tu) return new("thread", "usd", tu, thread.Cost ?? 0, $"Reached this thread's cap of {Count(tu, "usd")}. A lead can raise it.");
        var month = Month(now);
        var used = MonthUse(c, projectId, month);
        if (caps.MonthTokens is { } mt && used.Tokens >= mt) return new("month", "tokens", mt, used.Tokens, $"Reached {project}'s cap of {Count(mt, "tokens")} for {MonthName(month)}. A lead can raise it in the project's settings.");
        if (caps.MonthUsd is { } mu && (used.Cost ?? 0) >= mu) return new("month", "usd", mu, used.Cost ?? 0, $"Reached {project}'s cap of {Count(mu, "usd")} for {MonthName(month)}. A lead can raise it in the project's settings.");
        return null;
    }
}
