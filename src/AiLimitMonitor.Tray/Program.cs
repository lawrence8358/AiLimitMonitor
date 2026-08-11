namespace AiLimitMonitor.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // One icon per session: a second launch nudges the running instance and quits, so the
        // check comes before anything that shows UI or touches the registry.
        using var instance = SingleInstance.Acquire();
        if (instance is null)
            return;

        StartupManager.PromptOnFirstRun();
        StartupManager.SyncPath();
        Application.Run(new TrayAppContext(instance));
    }
}
