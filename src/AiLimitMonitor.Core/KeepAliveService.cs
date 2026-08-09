using System.Text;
using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;

namespace AiLimitMonitor.Core;

/// <summary>What the keep-alive check decided for one provider at one point in time.</summary>
public enum KeepAliveDecision
{
    /// <summary>Nothing to do (active window, no 5h plan, quota exhausted, or fetch failed).</summary>
    None,

    /// <summary>The 5h window is idle at 0% — send a hello so the clock restarts.</summary>
    SendHello,

    /// <summary>The 5h window is not running but shows usage — someone is (or was just)
    /// using it, so no hello is needed; log the current usage for traceability.</summary>
    SkipInUse,
}

/// <summary>
/// After each usage fetch, sends a minimal "hello" to every provider whose 5h window has
/// elapsed and sits at 0% used, so the 5h clock starts counting again immediately. When the
/// window is not running but shows usage, a skip line is logged instead — otherwise "someone
/// is using it" would be indistinguishable from "nothing happened". Every action is appended
/// to a log file as a single tab-separated line: time, provider, request, response.
/// </summary>
public sealed class KeepAliveService(
    IReadOnlyList<IUsageProvider> providers,
    string logPath,
    Func<string, bool>? providerFilter = null,
    TimeProvider? time = null)
{
    /// <summary>Minimum gap between two hello attempts for the same provider — the usage
    /// endpoint can lag behind a just-sent hello and would otherwise trigger a resend.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<int, DateTimeOffset> _lastAttempt = [];

    public string LogPath => logPath;

    public async Task CheckAsync(MonitorSnapshot snapshot, CancellationToken cancellationToken)
    {
        var count = Math.Min(providers.Count, snapshot.Providers.Count);
        for (var i = 0; i < count; i++)
        {
            if (providers[i] is not IKeepAliveProvider keepAlive)
                continue;
            if (providerFilter is not null && !providerFilter(providers[i].Name))
                continue;

            var usage = snapshot.Providers[i];
            var now = _time.GetUtcNow();
            var decision = Evaluate(usage, now, out var usedPercent);
            if (decision == KeepAliveDecision.None)
                continue;
            if (_lastAttempt.TryGetValue(i, out var last) && now - last < Cooldown)
                continue;
            _lastAttempt[i] = now;

            KeepAliveResult result;
            if (decision == KeepAliveDecision.SkipInUse)
            {
                result = new KeepAliveResult(true, "(skip)",
                    $"使用中（5h 已用 {usedPercent:0.#}%），有人使用不需呼叫");
            }
            else
            {
                try
                {
                    result = await keepAlive.SendHelloAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = new KeepAliveResult(false, "hello", ex.Message);
                }
            }
            AppendLog(now, usage.Name, result);
        }
    }

    /// <summary>
    /// Decides what to do for one provider. SendHello requires: the fetch succeeded, the plan
    /// actually reports a 5h window (weekly-only plans — e.g. codex team — are never pinged),
    /// no window sits at an unreset 100% (the hello would be rejected and only spam the log),
    /// the 5h window is not currently running (no reset time, or the reset time has passed),
    /// and it reads 0% used. A non-running 5h window with usage &gt; 0 yields SkipInUse so the
    /// log shows why no call was made.
    /// </summary>
    public static KeepAliveDecision Evaluate(ProviderUsage usage, DateTimeOffset now, out double usedPercent)
    {
        usedPercent = 0;
        if (usage.Error is not null)
            return KeepAliveDecision.None;

        UsageWindow? fiveHour = null;
        foreach (var w in usage.Windows)
        {
            if (w.UsedPercent >= 100 && (w.ResetsAt is null || w.ResetsAt > now))
                return KeepAliveDecision.None;
            if (w.Label != "5h")
                continue;
            if (w.ResetsAt is { } resetsAt && resetsAt > now)
                return KeepAliveDecision.None;
            fiveHour = w;
        }
        if (fiveHour is null)
            return KeepAliveDecision.None;

        usedPercent = fiveHour.UsedPercent;
        return fiveHour.UsedPercent > 0 ? KeepAliveDecision.SkipInUse : KeepAliveDecision.SendHello;
    }

    private void AppendLog(DateTimeOffset now, string providerName, KeepAliveResult result)
    {
        var line = FormatLogLine(now.ToLocalTime(), providerName, result);
        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine, Encoding.UTF8);
        }
        catch (IOException)
        {
            // Logging must never take the monitor down (locked file, read-only dir, …).
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>"[2026/08/08 15:04:05 +08:00] claude: hello → HTTP 200: Hello! …" — always one line.</summary>
    public static string FormatLogLine(DateTimeOffset localTime, string providerName, KeepAliveResult result)
    {
        var stamp = localTime.ToString("yyyy/MM/dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);
        return $"[{stamp}] {Sanitize(providerName, 80)}: " +
               $"{Sanitize(result.RequestContent, 200)} → {Sanitize(result.ResponseContent, 500)}";
    }

    /// <summary>Collapses all whitespace runs (incl. newlines/tabs) to one space and truncates.</summary>
    public static string Sanitize(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength));
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            if (sb.Length >= maxLength)
                break;
        }
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }
}
