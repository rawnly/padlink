using Velopack;

namespace PadLink;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Must run first: Velopack's installer and updater start the exe with special
        // arguments to run these hooks, and Run() exits the process when it handles them.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                try { Startup.Enabled = false; } catch { } // don't leave a Run key pointing at a deleted exe
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
