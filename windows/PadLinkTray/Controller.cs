using System.Windows;
using Forms = System.Windows.Forms;

namespace PadLink;

/// <summary>Tray icon + background loop: find the controller on the TV, attach it, keep it attached.</summary>
public sealed class Controller
{
    readonly Settings settings = Settings.Load();
    readonly Updater updater = new();
    readonly Forms.NotifyIcon tray;
    readonly MainWindow window;
    readonly Forms.ToolStripMenuItem connectItem;
    readonly Forms.ToolStripMenuItem driverItem;
    readonly Forms.ToolStripMenuItem updateItem;
    LinkState state = (LinkState)(-1);
    string stateText = "";
    string lastAttachError = "";
    bool exiting;
    bool scanning;
    bool autoScanned;
    bool driverReady;
    bool installing;
    bool updateAnnounced;
    DateTime lastRoamScan = DateTime.MinValue;
    CancellationTokenSource wake = new();

    public Controller(bool minimized)
    {
        window = new MainWindow(settings);
        window.SettingsChanged += () => { settings.Save(); lastAttachError = ""; WakeLoop(); };
        window.ConnectToggled += async () => await ToggleConnectAsync();
        window.FindClicked += async () => await ScanAsync();
        window.InstallDriverClicked += async () => await InstallDriverAsync();
        window.QuitClicked += async () => await ExitAsync();
        window.UpdateClicked += async () =>
        {
            if (updater.Pending != null) RestartToUpdate();
            else await CheckForUpdatesAsync(manual: true);
        };
        window.HiddenToTray += () =>
        {
            if (settings.TrayHintShown) return;
            settings.TrayHintShown = true;
            settings.Save();
            tray?.ShowBalloonTip(3000, "PadLink is still running", "It keeps your controller connected from the tray. Right-click the icon to quit.", Forms.ToolTipIcon.None);
        };

        connectItem = new Forms.ToolStripMenuItem("Disconnect", null, async (_, _) => await ToggleConnectAsync());
        driverItem = new Forms.ToolStripMenuItem("Install USB driver", null, async (_, _) => await InstallDriverAsync());
        updateItem = new Forms.ToolStripMenuItem("Restart to update", null, (_, _) => RestartToUpdate());
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open PadLink", null, (_, _) => window.ShowAndActivate());
        menu.Items.Add(connectItem);
        menu.Items.Add(driverItem);
        menu.Items.Add(updateItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) =>
        {
            connectItem.Text = settings.AutoConnect ? "Disconnect" : "Connect";
            driverItem.Visible = !driverReady;
            var pending = updater.Pending;
            updateItem.Visible = pending != null;
            if (pending != null) updateItem.Text = $"Restart to update to {pending.Version}";
        };

        tray = new Forms.NotifyIcon { Icon = Icons.Off, Text = "PadLink", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.ShowAndActivate(); };

        if (!minimized || string.IsNullOrWhiteSpace(settings.TvHost)) window.ShowAndActivate();
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        _ = UpdateLoopAsync();
        window.SetDriverStatus(false, installing: false);
        driverReady = await Task.Run(Driver.IsReady);
        window.SetDriverStatus(driverReady);
        if (!driverReady) await OfferDriverInstallAsync();
        if (settings.TvHost.Length > 0) _ = ScanAsync(); // fill the TV list in the background
        await LoopAsync();
    }

    async Task OfferDriverInstallAsync()
    {
        if (installing) return;
        window.ShowAndActivate();
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Install the USB driver?",
            Content = "PadLink needs its USB driver (usbip-win2) to plug the controller into this PC.\n\n" +
                      "Windows will ask for administrator permission once.",
            PrimaryButtonText = "Install",
            CloseButtonText = "Not now",
            Owner = window,
        };
        if (await box.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            window.Log("USB driver not installed. Use Settings → USB driver → Install when ready.");
            return;
        }
        await InstallDriverAsync();
    }

    async Task InstallDriverAsync()
    {
        if (installing) return;
        installing = true;
        window.SetDriverStatus(false, installing: true);
        SetState(LinkState.Connecting, "Installing the USB driver…");
        try
        {
            var (ok, msg) = await Driver.InstallAsync();
            driverReady = ok;
            window.Log(msg);
            if (!ok)
            {
                var box = new Wpf.Ui.Controls.MessageBox { Title = "PadLink", Content = msg, CloseButtonText = "OK", Owner = window };
                await box.ShowDialogAsync();
            }
        }
        finally
        {
            installing = false;
            window.SetDriverStatus(driverReady);
            WakeLoop();
        }
    }

    void WakeLoop() => wake.Cancel();

    // ---------- updates (Velopack + GitHub Releases) ----------

    async Task UpdateLoopAsync()
    {
        if (!updater.IsInstalled)
        {
            window.SetUpdateStatus($"Version {updater.CurrentVersion}, development build. Install PadLink from GitHub Releases to get automatic updates.", null);
            return;
        }
        ShowUpdateState();
        await Task.Delay(TimeSpan.FromSeconds(10)); // let the controller connect first
        while (!exiting)
        {
            await CheckForUpdatesAsync(manual: false);
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        window.SetUpdateStatus($"Version {updater.CurrentVersion}. Checking for updates…", "Check now", enabled: false);
        try
        {
            await updater.CheckAndDownloadAsync();
            ShowUpdateState();
        }
        catch (Exception ex)
        {
            if (manual) window.Log("Update check failed: " + ex.Message);
            window.SetUpdateStatus($"Version {updater.CurrentVersion}. Couldn't check for updates.", "Check now");
        }
    }

    void ShowUpdateState()
    {
        if (updater.Pending is { } p)
        {
            window.SetUpdateStatus($"Version {p.Version} is ready. Restart to install it, or it installs when you quit PadLink.", "Restart now", primary: true);
            if (updateAnnounced) return;
            updateAnnounced = true;
            window.Log($"Update {p.Version} downloaded");
            tray.ShowBalloonTip(3000, "PadLink update ready", $"Version {p.Version} installs the next time PadLink restarts.", Forms.ToolTipIcon.None);
        }
        else window.SetUpdateStatus($"Version {updater.CurrentVersion}. You're up to date.", "Check now");
    }

    /// <summary>
    /// Restart into the new version. The controller stays attached: the USB/IP link lives in the driver,
    /// and the new instance picks it up as already connected.
    /// </summary>
    void RestartToUpdate()
    {
        if (updater.Pending is not { } p || exiting) return;
        exiting = true;
        tray.Visible = false;
        tray.Dispose();
        try { updater.ApplyAndRestart(p); } // exits this process
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Couldn't install the update: " + ex.Message, "PadLink");
            Application.Current.Shutdown();
        }
    }

    async Task LoopAsync()
    {
        while (!exiting)
        {
            try { await TickAsync(); }
            catch (Exception ex) { SetState(LinkState.Error, ex.Message); }

            wake = new CancellationTokenSource();
            try { await Task.Delay(3000, wake.Token); } catch (TaskCanceledException) { }
        }
    }

    async Task TickAsync()
    {
        if (installing) return;
        var host = settings.TvHost.Trim();
        if (host.Length == 0)
        {
            if (!autoScanned)
            {
                autoScanned = true; // first run: try to find the TV by ourselves
                var tvs = await ScanAsync();
                if (tvs.Count > 0) { Pick(tvs[0]); return; }
            }
            SetState(LinkState.Off, "Pick your TV below to get started.");
            return;
        }
        if (!driverReady) { SetState(LinkState.Error, "The USB driver isn't installed yet. Install it in Settings below."); return; }
        if (!settings.AutoConnect) { SetState(LinkState.Off, $"Click Connect to use the controller from {TvLabel()}."); window.SetPing(null); return; }
        if (!ushort.TryParse(settings.Vendor, System.Globalization.NumberStyles.HexNumber, null, out var vendor))
        { SetState(LinkState.Error, "The vendor id in Advanced must be hex, e.g. 28de."); return; }

        List<RemoteDevice> devices;
        try
        {
            var (list, rtt) = await Task.Run(() => UsbIpClient.List(host));
            devices = list;
            window.SetPing(rtt);
        }
        catch
        {
            window.SetPing(null);
            if (await TryRoamAsync(host)) return;
            SetState(LinkState.Waiting, $"Can't reach {TvLabel()}. Is it on, with PadLink open?");
            return;
        }

        var dev = devices.FirstOrDefault(d => d.Vid == vendor);
        if (dev == null) { SetState(LinkState.Waiting, $"{TvLabel()} is online. Plug the controller into the TV."); return; }

        if (await Task.Run(() => UsbipExe.IsAttached(host)))
        {
            SetState(LinkState.Attached, $"Playing through {TvLabel()}");
            return;
        }

        SetState(LinkState.Connecting, $"Plugging in the controller from {TvLabel()}…");
        var (code, output) = await Task.Run(() => UsbipExe.Run("attach", "-r", host, "-b", dev.BusId));
        if (code == 0)
        {
            lastAttachError = "";
            SetState(LinkState.Attached, $"Playing through {TvLabel()}");
        }
        else
        {
            if (output != lastAttachError) window.Log("Attach failed: " + output);
            lastAttachError = output;
            SetState(LinkState.Error, "Couldn't plug in the controller. Details in Activity below.");
        }
    }

    string TvLabel() => settings.TvName.Length > 0 ? settings.TvName : settings.TvHost;

    async Task<List<FoundTv>> ScanAsync()
    {
        if (scanning) return [];
        scanning = true;
        window.SetScanning(true);
        try
        {
            var tvs = await Discovery.ScanAsync(window.Log);
            window.Log(tvs.Count == 0
                ? "No PadLink TVs found (is the PadLink app installed and running on the TV?)"
                : "Found: " + string.Join("  |  ", tvs));
            window.SetFoundTvs(tvs);
            return tvs;
        }
        finally
        {
            scanning = false;
            window.SetScanning(false);
        }
    }

    /// <summary>
    /// The remembered TV stopped answering at its address (e.g. you left home, so the LAN IP is gone):
    /// look for a TV with the same name on the LAN or Tailscale and switch to it.
    /// </summary>
    async Task<bool> TryRoamAsync(string oldHost)
    {
        if (settings.TvName.Length == 0 || scanning || DateTime.UtcNow - lastRoamScan < TimeSpan.FromSeconds(20)) return false;
        lastRoamScan = DateTime.UtcNow;
        SetState(LinkState.Waiting, $"{settings.TvName} isn't at {oldHost}. Searching your network and Tailscale…");
        var tv = (await ScanAsync()).FirstOrDefault(t => t.Name == settings.TvName && t.Host != oldHost);
        if (tv == null) return false;
        await Task.Run(() => UsbipExe.DetachHost(oldHost));
        window.Log($"Found {tv.Name} at {tv.Host} via {tv.Via}, switching");
        Pick(tv);
        return true;
    }

    void Pick(FoundTv tv)
    {
        settings.TvHost = tv.Host;
        settings.TvName = tv.Name;
        settings.Save();
        window.SyncFromSettings();
        WakeLoop();
    }

    void SetState(LinkState s, string text)
    {
        if (s == state && text == stateText) return;
        if (s == LinkState.Attached && state != LinkState.Attached)
            tray.ShowBalloonTip(2000, "PadLink", "Controller connected", Forms.ToolTipIcon.None);
        state = s;
        stateText = text;
        tray.Icon = Icons.For(s);
        var tip = "PadLink: " + text;
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        window.SetStatus(s, text);
        window.Log(text);
    }

    async Task ToggleConnectAsync()
    {
        if (settings.AutoConnect)
        {
            settings.AutoConnect = false; // otherwise the loop re-attaches 3 s later
            settings.Save();
            window.SyncFromSettings();
            var host = settings.TvHost.Trim();
            if (host.Length > 0 && UsbipExe.ExePath != null)
                await Task.Run(() => UsbipExe.DetachHost(host));
            window.Log("Disconnected");
        }
        else
        {
            settings.AutoConnect = true;
            settings.Save();
            window.SyncFromSettings();
        }
        WakeLoop();
    }

    async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        var host = settings.TvHost.Trim();
        if (host.Length > 0 && UsbipExe.ExePath != null)
            await Task.Run(() => UsbipExe.DetachHost(host));
        tray.Visible = false;
        tray.Dispose();
        if (updater.Pending is { } update)
        {
            try { updater.ApplyOnExit(update); } catch { } // installs quietly after we exit
        }
        window.ForceClose();
        Application.Current.Shutdown();
    }
}
