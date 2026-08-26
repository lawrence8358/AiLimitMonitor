using System.Runtime.InteropServices;
using System.Text;
using AiLimitMonitor.Core;
using AiLimitMonitor.Core.Config;
using AiLimitMonitor.Core.Rendering;

const string AppTitle = "AI Limit Monitor";
if (OperatingSystem.IsWindows())
    Console.Title = AppTitle;

var once = args.Contains("--once");
var petEnabled = !args.Contains("--no-pet");
string? configPath = ReadOption(args, "--config");
int? intervalOverride = ReadOption(args, "--interval") is { } s && int.TryParse(s, out var i) && i > 0
    ? i
    : null;

var config = ConfigLoader.LoadOrCreate(configPath);
if (config.Providers.Count == 0)
{
    Console.Error.WriteLine($"No providers configured. Edit {configPath ?? ConfigLoader.DefaultPath} and retry.");
    return 1;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AiLimitMonitor/1.3.0");
var service = new UsageMonitorService(ConfigLoader.BuildProviders(config, http));
var renderer = new UsageTextRenderer();
var interval = TimeSpan.FromSeconds(intervalOverride ?? config.RefreshSeconds);

Console.OutputEncoding = Encoding.UTF8;
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

if (once)
{
    var snapshot = await service.FetchAsync(cts.Token);
    Console.WriteLine();
    Console.WriteLine(renderer.Render(snapshot));
    return 0;
}

EnableVirtualTerminal();
// OSC 0 sets the title in VT terminals (Windows Terminal tabs, ssh, …) where
// Console.Title alone may not reach the visible tab.
Console.Write($"\x1b]0;{AppTitle}\x07");
Console.Write(AnsiFrame.EnterAltScreen);
Console.WriteLine("AI Limit Monitor — fetching…");
try
{
    var petTick = 0L;
    while (!cts.IsCancellationRequested)
    {
        var snapshot = await service.FetchAsync(cts.Token);
        var nextFetchAt = snapshot.Timestamp + interval;

        // Redraw every second until the next fetch: the header counts down, the
        // "resets in" durations stay live and the pet advances one step, all from
        // the same cached snapshot.
        while (!cts.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var untilRefresh = nextFetchAt - now;

            var frame = new StringBuilder();
            frame.AppendLine(renderer.Header(snapshot.Timestamp, untilRefresh));
            frame.AppendLine();
            frame.Append(renderer.Render(snapshot with { Timestamp = now }));
            // The border is always drawn so toggling the pet never shifts the layout.
            var text = PetBorder.Wrap(frame.ToString(), petTick++, showPet: petEnabled);
            Console.Write(AnsiFrame.Compose(text));

            if (untilRefresh <= TimeSpan.Zero)
                break;

            try
            {
                var tick = untilRefresh < TimeSpan.FromSeconds(1) ? untilRefresh : TimeSpan.FromSeconds(1);
                await Task.Delay(tick, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
finally
{
    Console.Write(AnsiFrame.LeaveAltScreen);
}
return 0;

static string? ReadOption(string[] args, string option)
{
    var index = Array.IndexOf(args, option);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void EnableVirtualTerminal()
{
    if (!OperatingSystem.IsWindows())
        return;
    var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
    if (GetConsoleMode(handle, out var mode))
        SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
}

[DllImport("kernel32.dll")]
static extern nint GetStdHandle(int nStdHandle);

[DllImport("kernel32.dll")]
static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

[DllImport("kernel32.dll")]
static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
