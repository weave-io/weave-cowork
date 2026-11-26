namespace CoWork.Contracts;

/// <summary>
/// Brief 13: skills CoWork gives every agent, whatever its harness. Each is one <c>SKILL.md</c> (the standard format: YAML front matter with a
/// name and a description, then markdown) embedded here once. The runner installs it where a sandbox's harness looks for skills; the server's
/// MCP tool returns it to any agent, so a harness without skills, or a pinned runner whose home CoWork doesn't own, gets the same text.
/// </summary>
public static class Skills
{
    public sealed record Skill(string Name, string Text)
    {
        /// <summary>The markdown after the front matter: what an agent reads when it asks for the guide.</summary>
        public string Body
        {
            get
            {
                if (!Text.StartsWith("---\n", StringComparison.Ordinal)) return Text;
                var end = Text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
                return end < 0 ? Text : Text[(end + 5)..].TrimStart('\n');
            }
        }
    }

    /// <summary>cowork-design: how to design a page shown with cowork_show (adapted from Weave Fleet's fleet-design).</summary>
    public static Skill Design { get; } = Load("cowork-design");

    public static IReadOnlyList<Skill> All { get; } = new[] { Design };

    private static Skill Load(string name)
    {
        using var s = typeof(Skills).Assembly.GetManifestResourceStream($"skills/{name}/SKILL.md")
            ?? throw new InvalidOperationException($"The {name} skill isn't embedded.");
        using var r = new StreamReader(s);
        return new Skill(name, r.ReadToEnd().Replace("\r\n", "\n"));
    }
}
