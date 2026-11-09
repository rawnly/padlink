using System.Windows;
using Wpf.Ui.Appearance;

namespace PadLink;

public partial class App : Application
{
    Mutex? mutex;
    Controller? controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        mutex = new Mutex(true, "PadLinkTray-single-instance", out bool first);
        if (!first) { Shutdown(); return; }

        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme(); // light/dark like Windows
        controller = new Controller(minimized: e.Args.Contains("--minimized"));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        mutex?.Dispose();
        base.OnExit(e);
    }
}
