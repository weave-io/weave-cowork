using CoWork.Contracts;

namespace CoWork.Runner.Acp;

/// <summary>
/// Brief 12: CoWork's skills as real skills, where a harness looks for them under its home. Only in a sandbox, whose home belongs to the thread:
/// a pinned runner runs as its owner, and writing into their home would change their own harnesses (those agents get the same text from the
/// server's <c>cowork_design_guide</c> tool). ACP has no notion of skills, so which folder each harness reads stops here, in the runner:
/// <list type="bullet">
/// <item><c>.claude/skills/&lt;name&gt;/SKILL.md</c>: Claude Code (its user skills); OpenCode 2.4.1 reads it too;</item>
/// <item><c>.agents/skills/&lt;name&gt;/SKILL.md</c>: Codex (its user skills); OpenCode 2.4.1 reads it too.</item>
/// </list>
/// OpenCode 2.4.1's folders were read from its binary on 26 November 2026 (its own config folder, <c>~/.claude</c> and <c>~/.agents</c>).
/// </summary>
public static class HarnessSkills
{
    public static readonly string[] Folders = { Path.Combine(".claude", "skills"), Path.Combine(".agents", "skills") };

    /// <summary>Writes each skill's SKILL.md under <paramref name="home"/> (again when its text changed). Returns the files written or kept.</summary>
    public static IReadOnlyList<string> Install(string home, IEnumerable<Skills.Skill>? skills = null)
    {
        var done = new List<string>();
        foreach (var skill in skills ?? Skills.All)
            foreach (var folder in Folders)
            {
                var file = Path.Combine(home, folder, skill.Name, "SKILL.md");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if (!File.Exists(file) || File.ReadAllText(file) != skill.Text) File.WriteAllText(file, skill.Text);
                done.Add(file);
            }
        return done;
    }
}
