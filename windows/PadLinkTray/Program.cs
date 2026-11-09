using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Win32;

namespace PadLink;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "PadLinkTray-single-instance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(minimized: args.Contains("--minimized")));
    }
}

public class Settings
{
    public string TvHost { get; set; } = "";
    /// <summary>Name the TV reported when picked from Find TVs; used to re-find it on another network.</summary>
    public string TvName { get; set; } = "";
    public string Vendor { get; set; } = "28de"; // Valve
    public bool AutoConnect { get; set; } = true;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PadLink", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("PadLink") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue("PadLink", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("PadLink", false);
        }
    }
}

public enum LinkState { Off, Waiting, Connecting, Attached, Error }

static class Icons
{
    public static readonly Icon Off = Make(Color.FromArgb(140, 140, 140));
    public static readonly Icon Waiting = Make(Color.FromArgb(230, 170, 30));
    public static readonly Icon Attached = Make(Color.FromArgb(60, 180, 90));
    public static readonly Icon Error = Make(Color.FromArgb(215, 70, 60));

    public static Icon For(LinkState s) => s switch
    {
        LinkState.Attached => Attached,
        LinkState.Waiting or LinkState.Connecting => Waiting,
        LinkState.Error => Error,
        _ => Off,
    };

    /// <summary>Little gamepad silhouette in the given color.</summary>
    static Icon Make(Color c)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = new SolidBrush(c);
            using var path = new GraphicsPath();
            path.AddEllipse(1, 9, 14, 18);
            path.AddEllipse(17, 9, 14, 18);
            path.AddRectangle(new Rectangle(7, 9, 18, 12));
            g.FillPath(body, path);
            using var hole = new SolidBrush(Color.FromArgb(30, 30, 30));
            g.FillRectangle(hole, 6, 14, 6, 2);
            g.FillRectangle(hole, 8, 12, 2, 6);
            g.FillEllipse(hole, 21, 13, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
