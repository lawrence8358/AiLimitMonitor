namespace AiLimitMonitor.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        StartupManager.PromptOnFirstRun();
        StartupManager.SyncPath();
        Application.Run(new TrayAppContext());
    }
}
