using AiLimitMonitor.Core.Config;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Tests;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("ailimit-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void CreateDefault_discovers_claude_dirs_and_codex()
    {
        WriteFile(".claude", ".credentials.json");
        WriteFile(".claude-5x", ".credentials.json");
        WriteFile(".claude-empty", "unrelated.txt"); // no credentials -> skipped
        WriteFile(".codex", "auth.json");

        var config = ConfigLoader.CreateDefault(_tempDir);

        Assert.Equal(["claude", "claude-5x", "codex"], config.Providers.Select(p => p.Name));
        Assert.Equal(["claude", "claude", "codex"], config.Providers.Select(p => p.Type));
    }

    [Fact]
    public void CreateDefault_returns_empty_when_nothing_found()
    {
        var config = ConfigLoader.CreateDefault(_tempDir);

        Assert.Empty(config.Providers);
    }

    [Fact]
    public void LoadOrCreate_round_trips_saved_default()
    {
        WriteFile(".claude", ".credentials.json");
        var configPath = Path.Combine(_tempDir, "cfg", "config.json");

        // First call generates a default file (scanning the real home dir), second call loads it.
        var created = ConfigLoader.LoadOrCreate(configPath);
        var loaded = ConfigLoader.LoadOrCreate(configPath);

        Assert.True(File.Exists(configPath));
        Assert.Equal(created.RefreshSeconds, loaded.RefreshSeconds);
        Assert.Equal(created.Providers.Select(p => p.Name), loaded.Providers.Select(p => p.Name));
    }

    [Fact]
    public void Save_round_trips_schedule_and_writes_day_names()
    {
        var configPath = Path.Combine(_tempDir, "config.json");
        var config = new MonitorConfig
        {
            KeepAliveSchedule = new KeepAliveScheduleConfig
            {
                Rules =
                [
                    new KeepAliveScheduleRuleConfig
                    {
                        Days = [DayOfWeek.Sunday, DayOfWeek.Monday],
                        Start = "23:00",
                        End = "01:00",
                    },
                ],
            },
        };

        ConfigLoader.Save(config, configPath);
        var json = File.ReadAllText(configPath);
        var loaded = ConfigLoader.LoadOrCreate(configPath);

        Assert.Contains("\"days\"", json);
        Assert.Contains("\"Sunday\"", json);
        Assert.Contains("\"Monday\"", json);
        Assert.DoesNotContain("\"days\": [\n        0", json.Replace("\r\n", "\n"));
        Assert.Equal([DayOfWeek.Sunday, DayOfWeek.Monday],
            loaded.KeepAliveSchedule!.Rules[0].Days);
        Assert.Equal("23:00", loaded.KeepAliveSchedule.Rules[0].Start);
        Assert.Equal("01:00", loaded.KeepAliveSchedule.Rules[0].End);
    }

    [Fact]
    public void LoadOrCreate_missing_schedule_remains_null_for_back_compatibility()
    {
        var configPath = Path.Combine(_tempDir, "legacy.json");
        File.WriteAllText(configPath, "{ \"refreshSeconds\": 30, \"providers\": [] }");

        var loaded = ConfigLoader.LoadOrCreate(configPath);

        Assert.Null(loaded.KeepAliveSchedule);
        Assert.Equal(30, loaded.RefreshSeconds);
    }

    [Fact]
    public void BuildProviders_creates_matching_provider_types()
    {
        var config = new MonitorConfig
        {
            Providers =
            [
                new ProviderConfig { Type = "claude", Name = "claude", ConfigDir = @"C:\x\.claude" },
                new ProviderConfig { Type = "codex", Name = "codex", AuthPath = @"C:\x\auth.json" },
                new ProviderConfig { Type = "command", Name = "custom", Command = "my-usage" },
            ],
        };

        using var http = new HttpClient();
        var providers = ConfigLoader.BuildProviders(config, http);

        Assert.Collection(providers,
            p => Assert.IsType<ClaudeUsageProvider>(p),
            p => Assert.IsType<CodexUsageProvider>(p),
            p => Assert.IsType<CommandUsageProvider>(p));
        Assert.Equal(["claude", "codex", "custom"], providers.Select(p => p.Name));
    }

    [Fact]
    public void BuildProviders_rejects_incomplete_config()
    {
        var config = new MonitorConfig
        {
            Providers = [new ProviderConfig { Type = "claude", Name = "broken" }],
        };

        using var http = new HttpClient();
        Assert.Throws<InvalidOperationException>(() => ConfigLoader.BuildProviders(config, http));
    }

    [Fact]
    public void ExpandHome_replaces_leading_tilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, ".claude-5x"), ConfigLoader.ExpandHome("~/.claude-5x"));
        Assert.Equal(@"C:\plain\path", ConfigLoader.ExpandHome(@"C:\plain\path"));
    }

    private void WriteFile(string dir, string file)
    {
        var path = Path.Combine(_tempDir, dir);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, file), "{}");
    }
}
