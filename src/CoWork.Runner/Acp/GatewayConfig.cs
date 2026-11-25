using System.Text.Json.Nodes;

namespace CoWork.Runner.Acp;

/// <summary>
/// Inside a sandbox the harness's only model provider is CoWork's gateway, an OpenAI-compatible endpoint that takes the thread token as its key
/// (docs/architecture/provisioning.html). For OpenCode that is a provider in its config: `@ai-sdk/openai-compatible` (OpenCode 2.4.1 bundles it)
/// with the gateway as `baseURL` and `{env:COWORK_THREAD_TOKEN}` as the key, so the token itself is never written to a file.
/// </summary>
public static class GatewayConfig
{
    public const string Provider = "cowork";

    /// <summary>Writes OpenCode's config for the gateway into <paramref name="configHome"/>/opencode/opencode.json (replacing it: the sandbox's
    /// config is the server's to say). Returns the path.</summary>
    public static string WriteOpenCode(string configHome, string gatewayUrl, string model)
    {
        var dir = Path.Combine(configHome, "opencode");
        Directory.CreateDirectory(dir);
        var cfg = new JsonObject
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["autoupdate"] = false,
            ["share"] = "disabled",
            ["enabled_providers"] = new JsonArray(Provider),
            ["provider"] = new JsonObject
            {
                [Provider] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "CoWork gateway",
                    ["options"] = new JsonObject { ["baseURL"] = gatewayUrl.TrimEnd('/'), ["apiKey"] = "{env:COWORK_THREAD_TOKEN}" },
                    ["models"] = new JsonObject { [model] = new JsonObject { ["name"] = model, ["tool_call"] = true } },
                },
            },
            ["model"] = $"{Provider}/{model}",
            ["small_model"] = $"{Provider}/{model}",
        };
        var path = Path.Combine(dir, "opencode.json");
        File.WriteAllText(path, cfg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
