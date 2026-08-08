using Microsoft.Win32;

namespace AiLimitMonitor.Tray;

/// <summary>
/// Manages the "start with Windows" registration via HKCU\...\Run, plus a marker file so the
/// first-run prompt is only shown once.
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AiLimitMonitor";

    private static string PromptMarkerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ailimitmonitor", "tray-startup-prompted");

    private static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(ValueName, $"\"{ExecutablePath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Rewrites the Run entry so it still points here after the exe is moved.</summary>
    public static void SyncPath()
    {
        if (IsEnabled)
            SetEnabled(true);
    }

    /// <summary>
    /// On the very first launch, asks whether the app should start with Windows and applies the
    /// answer. Later launches (marker file present) do nothing.
    /// </summary>
    public static void PromptOnFirstRun()
    {
        if (File.Exists(PromptMarkerPath))
            return;

        var answer = MessageBox.Show(
            "要在 Windows 開機時自動啟動 AI Limit Monitor 嗎？\n\n之後可以在系統匣圖示的右鍵選單切換這個設定。",
            "AI Limit Monitor",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);
        if (answer == DialogResult.Yes)
            SetEnabled(true);

        Directory.CreateDirectory(Path.GetDirectoryName(PromptMarkerPath)!);
        File.WriteAllText(PromptMarkerPath, "");
    }
}
