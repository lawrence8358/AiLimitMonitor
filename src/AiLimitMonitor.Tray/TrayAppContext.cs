using System.Runtime.InteropServices;
using AiLimitMonitor.Core;
using AiLimitMonitor.Core.Config;
using AiLimitMonitor.Core.Models;
using AiLimitMonitor.Core.Providers;
using AiLimitMonitor.Core.Rendering;

namespace AiLimitMonitor.Tray;

/// <summary>
/// Tray icon showing hours until the next limit reset; hovering the icon opens a popup
/// with the same text the console version renders.
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly UsageMonitorService _service;
    private readonly KeepAliveService _keepAlive;
    private readonly KeepAliveSchedule _keepAliveSchedule;
    private readonly MonitorConfig _config;
    private readonly UsageTextRenderer _renderer = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly UsagePopupForm _popup = new();
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer _instanceTimer;
    private MonitorSnapshot? _snapshot;
    private Icon? _currentIcon;
    private bool _fetching;
    private bool _keepAliveEnabled;

    public TrayAppContext(SingleInstance instance)
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "AiLimitMonitor/1.1");
        _config = ConfigLoader.LoadOrCreate();
        var providers = ConfigLoader.BuildProviders(_config, _http);
        _service = new UsageMonitorService(providers);
        _keepAliveSchedule = new KeepAliveSchedule();
        _keepAliveSchedule.Update(_config.KeepAliveSchedule);
        // The log lives next to the executable so users find it without hunting for %USERPROFILE%.
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _keepAlive = new KeepAliveService(providers, Path.Combine(exeDir, "keepalive.log"),
            name => _config.Providers.Find(p => p.Name == name)?.KeepAliveResolved ?? false,
            schedule: _keepAliveSchedule);
        _keepAliveEnabled = _config.KeepAliveEnabled;

        var menu = new ContextMenuStrip();
        menu.Items.Add("立即更新", null, async (_, _) => await RefreshAsync());
        var petItem = new ToolStripMenuItem("顯示寵物") { Checked = true, CheckOnClick = true };
        petItem.CheckedChanged += (_, _) => _popup.PetEnabled = petItem.Checked;
        menu.Items.Add(petItem);
        var keepAliveMenu = new ToolStripMenuItem("5h 到期自動 hello（重新起算）");
        var keepAliveEnableItem = new ToolStripMenuItem("啟用")
        {
            Checked = _keepAliveEnabled,
            CheckOnClick = true,
        };
        keepAliveEnableItem.CheckedChanged += async (_, _) =>
        {
            _keepAliveEnabled = keepAliveEnableItem.Checked;
            _config.KeepAliveEnabled = _keepAliveEnabled;
            ConfigLoader.Save(_config);
            if (_keepAliveEnabled)
                await RefreshAsync();
        };
        keepAliveMenu.DropDownItems.Add(keepAliveEnableItem);
        var scheduleItem = new ToolStripMenuItem(ScheduleMenuText());
        scheduleItem.Click += async (_, _) =>
        {
            using var form = new KeepAliveScheduleForm(_config.KeepAliveSchedule);
            if (form.ShowDialog() != DialogResult.OK)
                return;

            _config.KeepAliveSchedule = form.SelectedSchedule;
            _keepAliveSchedule.Update(_config.KeepAliveSchedule);
            ConfigLoader.Save(_config);
            scheduleItem.Text = ScheduleMenuText();
            if (_keepAliveEnabled)
                await RefreshAsync();
        };
        keepAliveMenu.DropDownItems.Add(scheduleItem);
        keepAliveMenu.DropDownItems.Add(new ToolStripSeparator());
        // One checkbox per platform that supports keep-alive; claude accounts are on by default.
        for (var i = 0; i < providers.Count; i++)
        {
            if (providers[i] is not IKeepAliveProvider)
                continue;
            var providerConfig = _config.Providers.Find(p => p.Name == providers[i].Name);
            if (providerConfig is null)
                continue;
            var providerItem = new ToolStripMenuItem(providerConfig.Name)
            {
                Checked = providerConfig.KeepAliveResolved,
                CheckOnClick = true,
            };
            providerItem.CheckedChanged += (_, _) =>
            {
                providerConfig.KeepAlive = providerItem.Checked;
                ConfigLoader.Save(_config);
            };
            // How much quota the helloes have burned so far, refreshed each time the menu opens.
            keepAliveMenu.DropDownOpening += (_, _) =>
            {
                var total = _keepAlive.TotalTokensFor(providerConfig.Name);
                providerItem.Text = total is null
                    ? providerConfig.Name
                    : $"{providerConfig.Name}（累計 token 輸入 {total.InputTokens:N0}／輸出 {total.OutputTokens:N0}）";
            };
            keepAliveMenu.DropDownItems.Add(providerItem);
        }
        menu.Items.Add(keepAliveMenu);
        var startupItem = new ToolStripMenuItem("開機時自動啟動") { CheckOnClick = true };
        startupItem.CheckedChanged += (_, _) => StartupManager.SetEnabled(startupItem.Checked);
        menu.Items.Add(startupItem);
        menu.Items.Add("結束", null, (_, _) => ExitThread());
        // Re-read on every open: the Run key may have changed outside the app (another instance,
        // Task Manager's Startup tab, etc.).
        menu.Opening += (_, _) => startupItem.Checked = StartupManager.IsEnabled;

        // Text stays empty: the built-in tooltip would pop up over our own popup window.
        _notifyIcon = new NotifyIcon
        {
            Icon = SetIcon(TrayIconRenderer.Render("...")),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseMove += OnTrayMouseMove;
        _notifyIcon.MouseClick += OnTrayMouseClick;

        _refreshTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)TimeSpan.FromSeconds(Math.Max(15, _config.RefreshSeconds)).TotalMilliseconds,
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _refreshTimer.Start();

        // A second launch cannot open its own icon, so it hands the request over to us: show
        // where the running one lives instead of appearing to do nothing.
        _instanceTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _instanceTimer.Tick += (_, _) =>
        {
            if (!instance.WasLaunchAttempted())
                return;
            _notifyIcon.ShowBalloonTip(3000, "AI Limit Monitor",
                "已經在執行中，圖示就在系統匣裡。", ToolTipIcon.Info);
            ShowPopup();
        };
        _instanceTimer.Start();

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_fetching)
            return;
        _fetching = true;
        try
        {
            var snapshot = await _service.FetchAsync(CancellationToken.None);
            _snapshot = snapshot;

            if (_keepAliveEnabled)
                await _keepAlive.CheckAsync(snapshot, CancellationToken.None);

            var next = snapshot.NextReset(snapshot.Timestamp);
            var iconText = next is { } reset
                ? TimeFormat.CompactDuration(reset - snapshot.Timestamp)
                : "?";
            SetIcon(TrayIconRenderer.Render(iconText));

            if (_popup.Visible)
                _popup.UpdateText(_renderer.Render(snapshot));
        }
        catch (Exception ex)
        {
            _popup.UpdateText($"refresh failed: {ex.Message}");
        }
        finally
        {
            _fetching = false;
        }
    }

    private void OnTrayMouseMove(object? sender, MouseEventArgs e) => ShowPopup();

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            ShowPopup();
    }

    private void ShowPopup()
    {
        var text = _snapshot is { } snapshot ? _renderer.Render(snapshot) : "loading…";
        _popup.ShowNear(Cursor.Position, text);
    }

    private string ScheduleMenuText() => _config.KeepAliveSchedule is { } schedule
        ? $"允許呼叫時段…（已設定 {schedule.Rules.Count} 個）"
        : "允許呼叫時段…（不限）";

    private Icon SetIcon(Icon icon)
    {
        if (_notifyIcon is not null)
            _notifyIcon.Icon = icon;
        if (_currentIcon is { } old)
        {
            DestroyIcon(old.Handle);
            old.Dispose();
        }
        _currentIcon = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _instanceTimer.Stop();
        _instanceTimer.Dispose();
        _refreshTimer.Stop();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _popup.Dispose();
        _http.Dispose();
        if (_currentIcon is { } icon)
        {
            DestroyIcon(icon.Handle);
            icon.Dispose();
        }
        base.ExitThreadCore();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}
