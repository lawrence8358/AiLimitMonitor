using System.Diagnostics;
using System.Text.Json;
using AiLimitMonitor.Core.Models;

namespace AiLimitMonitor.Core.Providers;

/// <summary>
/// Runs a user-defined PowerShell command and parses its stdout as usage JSON:
/// <code>
/// {
///   "windows": [ { "label": "5h", "usedPercent": 51.0, "resetsAt": "2026-08-08T00:10:00+08:00" } ],
///   "notes": [ "optional extra lines" ]
/// }
/// </code>
/// A window may use "resetsInSeconds" instead of "resetsAt".
/// </summary>
public sealed class CommandUsageProvider(string name, string command, TimeProvider? time = null) : IUsageProvider
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => name;

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("failed to start process");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                return ProviderUsage.Failed(name, $"command exited with code {process.ExitCode}");
            return ParseOutput(name, stdout, _time.GetUtcNow());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ProviderUsage.Failed(name, $"command failed: {ex.Message}");
        }
    }

    public static ProviderUsage ParseOutput(string name, string output, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            return ProviderUsage.Failed(name, $"invalid JSON from command: {ex.Message}");
        }

        using (doc)
        {
            var windows = new List<UsageWindow>();
            if (doc.RootElement.TryGetProperty("windows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var label = el.TryGetProperty("label", out var l) ? l.GetString() ?? "limit" : "limit";
                    double percent = el.TryGetProperty("usedPercent", out var u) &&
                                     u.ValueKind == JsonValueKind.Number
                        ? u.GetDouble()
                        : 0;

                    DateTimeOffset? resetsAt = null;
                    if (el.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.String &&
                        DateTimeOffset.TryParse(r.GetString(), out var parsed))
                        resetsAt = parsed;
                    else if (el.TryGetProperty("resetsInSeconds", out var s) && s.ValueKind == JsonValueKind.Number)
                        resetsAt = now.AddSeconds(s.GetDouble());

                    windows.Add(new UsageWindow(label, percent, resetsAt));
                }
            }

            var notes = new List<string>();
            if (doc.RootElement.TryGetProperty("notes", out var notesArr) &&
                notesArr.ValueKind == JsonValueKind.Array)
                foreach (var n in notesArr.EnumerateArray())
                    if (n.GetString() is { Length: > 0 } note)
                        notes.Add(note);

            return new ProviderUsage(name, windows, notes);
        }
    }
}
