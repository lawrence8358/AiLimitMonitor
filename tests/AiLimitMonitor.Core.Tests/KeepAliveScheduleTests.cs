using AiLimitMonitor.Core;
using AiLimitMonitor.Core.Config;
using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core.Tests;

public class KeepAliveScheduleTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;

    [Fact]
    public void Missing_schedule_is_unrestricted_for_back_compatibility()
    {
        var schedule = new KeepAliveSchedule(Zone);

        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 3, 0)));
    }

    [Fact]
    public void Regular_rule_is_start_inclusive_and_end_exclusive()
    {
        var schedule = Schedule(Rule([DayOfWeek.Monday], "09:00", "17:00"));

        Assert.False(schedule.IsAllowed(At(2026, 8, 10, 8, 59)));
        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 9, 0)));
        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 16, 59)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 10, 17, 0)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 11, 10, 0)));
    }

    [Fact]
    public void Multiple_rules_are_or_ed()
    {
        var schedule = Schedule(
            Rule([DayOfWeek.Monday], "09:00", "10:00"),
            Rule([DayOfWeek.Wednesday], "15:00", "16:00"));

        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 9, 30)));
        Assert.True(schedule.IsAllowed(At(2026, 8, 12, 15, 30)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 11, 9, 30)));
    }

    [Fact]
    public void Rule_is_evaluated_in_the_configured_local_time_zone()
    {
        var taipei = TimeZoneInfo.CreateCustomTimeZone(
            "UTC+8-test", TimeSpan.FromHours(8), "UTC+8-test", "UTC+8-test");
        var schedule = new KeepAliveSchedule(taipei);
        schedule.Update(new KeepAliveScheduleConfig
        {
            Rules = [Rule([DayOfWeek.Monday], "09:00", "10:00")],
        });

        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 1, 30)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 10, 2, 0)));
    }

    [Fact]
    public void Overnight_rule_uses_start_day_and_crosses_sunday_to_monday()
    {
        var schedule = Schedule(Rule([DayOfWeek.Sunday], "23:00", "01:00"));

        Assert.False(schedule.IsAllowed(At(2026, 8, 9, 22, 59)));
        Assert.True(schedule.IsAllowed(At(2026, 8, 9, 23, 0)));
        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 0, 59)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 10, 1, 0)));
        Assert.False(schedule.IsAllowed(At(2026, 8, 11, 0, 30)));
    }

    [Fact]
    public void Empty_invalid_and_equal_rules_fail_closed()
    {
        var schedule = new KeepAliveSchedule(Zone);
        schedule.Update(new KeepAliveScheduleConfig
        {
            Rules =
            [
                new KeepAliveScheduleRuleConfig { Days = [], Start = "09:00", End = "10:00" },
                new KeepAliveScheduleRuleConfig { Days = [DayOfWeek.Monday], Start = "bad", End = "10:00" },
                new KeepAliveScheduleRuleConfig { Days = [DayOfWeek.Monday], Start = "10:00", End = "10:00" },
            ],
        });

        Assert.False(schedule.IsAllowed(At(2026, 8, 10, 9, 30)));
    }

    [Fact]
    public void Update_null_restores_unrestricted_mode()
    {
        var schedule = Schedule(Rule([DayOfWeek.Monday], "09:00", "10:00"));

        schedule.Update(null);

        Assert.True(schedule.IsAllowed(At(2026, 8, 10, 3, 0)));
    }

    [Fact]
    public async Task Service_blocks_outside_schedule_without_log_or_hello()
    {
        var clock = new FixedTimeProvider(At(2026, 8, 10, 8, 0));
        var provider = new FakeKeepAliveProvider();
        var logPath = Path.Combine(Path.GetTempPath(), $"keepalive-{Guid.NewGuid():N}.log");
        var schedule = Schedule(Rule([DayOfWeek.Monday], "09:00", "10:00"));
        try
        {
            var service = new KeepAliveService([provider], logPath, time: clock, schedule: schedule);
            await service.CheckAsync(Snapshot(clock.GetUtcNow()), CancellationToken.None);

            Assert.Equal(0, provider.CallCount);
            Assert.False(File.Exists(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    [Fact]
    public async Task Service_sends_inside_schedule()
    {
        var now = At(2026, 8, 10, 9, 0);
        var clock = new FixedTimeProvider(now);
        var provider = new FakeKeepAliveProvider();
        var logPath = Path.Combine(Path.GetTempPath(), $"keepalive-{Guid.NewGuid():N}.log");
        var schedule = Schedule(Rule([DayOfWeek.Monday], "09:00", "10:00"));
        try
        {
            var service = new KeepAliveService([provider], logPath, time: clock, schedule: schedule);
            await service.CheckAsync(Snapshot(now), CancellationToken.None);

            Assert.Equal(1, provider.CallCount);
            Assert.Contains("hello", File.ReadAllText(logPath));
        }
        finally
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    [Fact]
    public async Task Service_uses_schedule_updates_without_restart()
    {
        var now = At(2026, 8, 10, 8, 30);
        var clock = new FixedTimeProvider(now);
        var provider = new FakeKeepAliveProvider();
        var logPath = Path.Combine(Path.GetTempPath(), $"keepalive-{Guid.NewGuid():N}.log");
        var schedule = Schedule(Rule([DayOfWeek.Monday], "09:00", "10:00"));
        try
        {
            var service = new KeepAliveService([provider], logPath, time: clock, schedule: schedule);
            await service.CheckAsync(Snapshot(now), CancellationToken.None);
            Assert.Equal(0, provider.CallCount);

            schedule.Update(new KeepAliveScheduleConfig
            {
                Rules = [Rule([DayOfWeek.Monday], "08:00", "09:00")],
            });
            await service.CheckAsync(Snapshot(now), CancellationToken.None);

            Assert.Equal(1, provider.CallCount);
        }
        finally
        {
            if (File.Exists(logPath))
                File.Delete(logPath);
        }
    }

    private static KeepAliveSchedule Schedule(params KeepAliveScheduleRuleConfig[] rules)
    {
        var schedule = new KeepAliveSchedule(Zone);
        schedule.Update(new KeepAliveScheduleConfig { Rules = [.. rules] });
        return schedule;
    }

    private static KeepAliveScheduleRuleConfig Rule(List<DayOfWeek> days, string start, string end) =>
        new() { Days = days, Start = start, End = end };

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static MonitorSnapshot Snapshot(DateTimeOffset now) =>
        new(now, [new ProviderUsage("fake", [new UsageWindow("5h", 0, now.AddMinutes(-1))], [])]);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeKeepAliveProvider : IUsageProvider, IKeepAliveProvider
    {
        public string Name => "fake";
        public int CallCount { get; private set; }

        public Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot(DateTimeOffset.UtcNow).Providers[0]);

        public Task<KeepAliveResult> SendHelloAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new KeepAliveResult(true, "hello", "ok"));
        }
    }
}
