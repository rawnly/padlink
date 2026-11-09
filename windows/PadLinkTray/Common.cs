using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text.Json;
using Microsoft.Win32;

namespace PadLink;

public class Settings
{
    public string TvHost { get; set; } = "";
    /// <summary>Name the TV reported when picked from Find TVs; used to re-find it on another network.</summary>
    public string TvName { get; set; } = "";
    public string Vendor { get; set; } = "28de"; // Valve
    public bool AutoConnect { get; set; } = true;
    public bool TrayHintShown { get; set; }

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

/// <summary>Tray icons: the app logo with a status dot. Same artwork as padlink.ico (tools/make-icon.ps1).</summary>
static class Icons
{
    public static readonly Icon Off = Make(Color.FromArgb(150, 150, 150));
    public static readonly Icon Waiting = Make(Color.FromArgb(255, 185, 0));
    public static readonly Icon Attached = Make(Color.FromArgb(16, 200, 90));
    public static readonly Icon Error = Make(Color.FromArgb(232, 17, 35));

    public static Icon For(LinkState s) => s switch
    {
        LinkState.Attached => Attached,
        LinkState.Waiting or LinkState.Connecting => Waiting,
        LinkState.Error => Error,
        _ => Off,
    };

    static Icon Make(Color dot)
    {
        const int S = 32;
        var bmp = new Bitmap(S, S);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Logo.Draw(g, S);
            float r = S * 0.19f, cx = S - r - 0.5f, cy = S - r - 0.5f;
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, cx - r - 1.5f, cy - r - 1.5f, 2 * r + 3, 2 * r + 3);
            using var fill = new SolidBrush(dot);
            g.FillEllipse(fill, cx - r, cy - r, 2 * r, 2 * r);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

/// <summary>The PadLink logo: white gamepad on a blue rounded square. Drawn on a 32-unit grid.</summary>
static class Logo
{
    public static void Draw(Graphics g, float size)
    {
        float k = size / 32f;
        var state = g.Save();
        g.ScaleTransform(k, k);

        using (var bg = RoundedRect(0.5f, 0.5f, 31f, 31f, 7.5f))
        using (var grad = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32),
                   Color.FromArgb(56, 142, 255), Color.FromArgb(20, 84, 214)))
            g.FillPath(grad, bg);

        using (var pad = new GraphicsPath(FillMode.Winding))
        {
            using (var body = RoundedRect(5f, 10f, 22f, 10.5f, 5f)) pad.AddPath(body, false);
            pad.AddEllipse(4.2f, 12.5f, 9.5f, 12.5f);
            pad.AddEllipse(18.3f, 12.5f, 9.5f, 12.5f);
            using var white = new SolidBrush(Color.White);
            g.FillPath(white, pad);
        }

        using var cut = new SolidBrush(Color.FromArgb(28, 98, 226));
        g.FillRectangle(cut, 7.6f, 14.3f, 4.6f, 1.6f);   // d-pad
        g.FillRectangle(cut, 9.1f, 12.8f, 1.6f, 4.6f);
        g.FillEllipse(cut, 20.6f, 12.6f, 2.2f, 2.2f);    // face buttons
        g.FillEllipse(cut, 18.4f, 15.0f, 2.2f, 2.2f);

        g.Restore(state);
    }

    static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
