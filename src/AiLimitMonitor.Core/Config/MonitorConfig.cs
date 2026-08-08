using System.Text.Json;
using System.Text.Json.Serialization;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Config;

public sealed class ProviderConfig
{
    /// <summary>"claude", "codex", or "command".</summary>
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>claude: directory containing .credentials.json (e.g. ~/.claude, ~/.claude-5x).</summary>
    public string? ConfigDir { get; set; }

    /// <summary>codex: path to auth.json.</summary>
    public string? AuthPath { get; set; }

    /// <summary>command: PowerShell command whose stdout is usage JSON.</summary>
    public string? Command { get; set; }
}

public sealed class MonitorConfig
{
    public int RefreshSeconds { get; set; } = 60;
    public List<ProviderConfig> Providers { get; set; } = [];
}

// Source-generated serialization keeps the config working in trimmed single-file publishes,
// where reflection-based System.Text.Json would be stripped.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MonitorConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext;

public static class ConfigLoader
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ailimitmonitor", "config.json");

    /// <summary>Loads the config, generating and saving a default one on first run.</summary>
    public static MonitorConfig LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            return JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.MonitorConfig)
                ?? new MonitorConfig();

        var config = CreateDefault(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, ConfigJsonContext.Default.MonitorConfig));
        return config;
    }

    /// <summary>
    /// Scans the home directory: every ".claude*" directory containing .credentials.json becomes a
    /// claude provider (so extra accounts like ~/.claude-5x are picked up automatically), and
    /// ~/.codex/auth.json becomes the codex provider.
    /// </summary>
    public static MonitorConfig CreateDefault(string homeDir)
    {
        var config = new MonitorConfig();

        if (Directory.Exists(homeDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(homeDir, ".claude*").Order())
            {
                if (!File.Exists(Path.Combine(dir, ".credentials.json")))
                    continue;
                config.Providers.Add(new ProviderConfig
                {
                    Type = "claude",
                    Name = Path.GetFileName(dir).TrimStart('.'),
                    ConfigDir = dir,
                });
            }
        }

        var codexAuth = Path.Combine(homeDir, ".codex", "auth.json");
        if (File.Exists(codexAuth))
            config.Providers.Add(new ProviderConfig { Type = "codex", Name = "codex", AuthPath = codexAuth });

        return config;
    }

    public static List<IUsageProvider> BuildProviders(MonitorConfig config, HttpClient http)
    {
        var providers = new List<IUsageProvider>();
        foreach (var p in config.Providers)
        {
            switch (p.Type.ToLowerInvariant())
            {
                case "claude" when p.ConfigDir is { Length: > 0 }:
                    providers.Add(new ClaudeUsageProvider(p.Name, ExpandHome(p.ConfigDir), http));
                    break;
                case "codex" when p.AuthPath is { Length: > 0 }:
                    providers.Add(new CodexUsageProvider(p.Name, ExpandHome(p.AuthPath), http));
                    break;
                case "command" when p.Command is { Length: > 0 }:
                    providers.Add(new CommandUsageProvider(p.Name, p.Command));
                    break;
                default:
                    throw new InvalidOperationException(
                        $"invalid provider config \"{p.Name}\": type \"{p.Type}\" with missing required field");
            }
        }
        return providers;
    }

    public static string ExpandHome(string path) =>
        path.StartsWith("~", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                path.TrimStart('~', '/', '\\'))
            : path;
}
