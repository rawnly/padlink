using System.ComponentModel;
using System.Diagnostics;

namespace PadLink;

/// <summary>Checks for the usbip-win2 driver and installs the copy bundled inside PadLink.exe.</summary>
public static class Driver
{
    const string ResourceName = "usbip-setup.exe";

    /// <summary>usbip.exe exists and can talk to the driver ("usbip port" fails if the driver isn't loaded).</summary>
    public static bool IsReady()
    {
        UsbipExe.Refresh();
        if (UsbipExe.ExePath == null) return false;
        try { return UsbipExe.Run("port").Code == 0; }
        catch { return false; }
    }

    /// <summary>Extracts the bundled installer and runs it silently, elevated (one UAC prompt).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PadLink-driver");
        Directory.CreateDirectory(dir);
        var setup = Path.Combine(dir, "USBip-setup-x64.exe");

        using (var res = typeof(Driver).Assembly.GetManifestResourceStream(ResourceName))
        {
            if (res == null) return (false, "Installer is not bundled in this build.");
            using var f = File.Create(setup);
            await res.CopyToAsync(f);
        }

        var psi = new ProcessStartInfo(setup)
        {
            UseShellExecute = true,
            Verb = "runas", // admin prompt
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
        };

        int code;
        try
        {
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            code = p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return (false, "Driver install cancelled (admin permission was declined).");
        }
        finally
        {
            try { File.Delete(setup); } catch { }
        }

        if (await Task.Run(IsReady)) return (true, "USB driver installed.");
        // Inno Setup: 0 = ok, 8 = needs restart; drivers often only load after a reboot.
        return code is 0 or 8
            ? (false, "Driver installed, but Windows needs a restart before it works. Restart, then open PadLink again.")
            : (false, $"Driver installer failed (exit code {code}).");
    }
}
