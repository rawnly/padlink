namespace PadLink;

/// <summary>Tray icon + background loop: find the controller on the TV, attach it, keep it attached.</summary>
public sealed class TrayApp : ApplicationContext
{
    readonly Settings settings = Settings.Load();
    readonly NotifyIcon tray;
    readonly MainForm form;
    readonly ToolStripMenuItem autoItem;
    LinkState state = (LinkState)(-1);
    string stateText = "";
    string lastAttachError = "";
    bool exiting;
    bool scanning;
    bool autoScanned;
    DateTime lastRoamScan = DateTime.MinValue;
    CancellationTokenSource wake = new();

    public TrayApp(bool minimized)
    {
        form = new MainForm(settings);
        form.SettingsChanged += () => { settings.Save(); lastAttachError = ""; WakeLoop(); };
        form.DetachClicked += async () => await DetachAsync();
        form.ExitClicked += async () => await ExitAsync();
        form.FindClicked += async () => await ScanAsync();

        autoItem = new ToolStripMenuItem("Auto-connect", null, (_, _) =>
        {
            settings.AutoConnect = !settings.AutoConnect;
            settings.Save();
            form.SyncFromSettings();
            WakeLoop();
        });
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PadLink", null, (_, _) => ShowForm());
        menu.Items.Add(autoItem);
        menu.Items.Add("Detach now", null, async (_, _) => await DetachAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Install USB driver", null, async (_, _) => await InstallDriverAsync());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) => autoItem.Checked = settings.AutoConnect;

        tray = new NotifyIcon { Icon = Icons.Off, Text = "PadLink", Visible = true, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };

        if (!minimized || string.IsNullOrWhiteSpace(settings.TvHost)) ShowForm();
        _ = StartAsync();
    }

    bool driverReady;
    bool installing;

    async Task StartAsync()
    {
        driverReady = await Task.Run(Driver.IsReady);
        if (!driverReady) await OfferDriverInstallAsync();
        await LoopAsync();
    }

    async Task OfferDriverInstallAsync()
    {
        if (installing) return;
        ShowForm();
        var answer = MessageBox.Show(form,
            "PadLink needs its USB driver (usbip-win2) to plug the controller into this PC.\n\n" +
            "Install it now? Windows will ask for administrator permission once.",
            "PadLink: install USB driver", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            form.Log("USB driver not installed. Use the tray menu \"Install USB driver\" when ready.");
            return;
        }
        await InstallDriverAsync();
    }

    async Task InstallDriverAsync()
    {
        if (installing) return;
        installing = true;
        SetState(LinkState.Connecting, "Installing USB driver…");
        try
        {
            var (ok, msg) = await Driver.InstallAsync();
            driverReady = ok;
            form.Log(msg);
            if (!ok) MessageBox.Show(form, msg, "PadLink", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally { installing = false; WakeLoop(); }
    }

    void ShowForm()
    {
        form.Show();
        if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
        form.Activate();
    }

    void WakeLoop() => wake.Cancel();

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
            SetState(LinkState.Off, "Click Find TVs or enter the TV address");
            return;
        }
        if (!driverReady) { SetState(LinkState.Error, "USB driver not installed (tray menu → Install USB driver)"); return; }
        if (!settings.AutoConnect) { SetState(LinkState.Off, "Auto-connect is off"); form.SetPing(null); return; }
        if (!ushort.TryParse(settings.Vendor, System.Globalization.NumberStyles.HexNumber, null, out var vendor))
        { SetState(LinkState.Error, "Vendor id must be hex, e.g. 28de"); return; }

        List<RemoteDevice> devices;
        try
        {
            var (list, rtt) = await Task.Run(() => UsbIpClient.List(host));
            devices = list;
            form.SetPing(rtt);
        }
        catch
        {
            form.SetPing(null);
            if (await TryRoamAsync(host)) return;
            SetState(LinkState.Waiting, "TV not reachable (is PadLink running on it?)");
            return;
        }

        var dev = devices.FirstOrDefault(d => d.Vid == vendor);
        if (dev == null) { SetState(LinkState.Waiting, "TV online, controller not plugged in"); return; }

        if (await Task.Run(() => UsbipExe.IsAttached(host)))
        {
            SetState(LinkState.Attached, $"Connected  {dev.Vid:x4}:{dev.Pid:x4}  bus {dev.BusId}");
            return;
        }

        SetState(LinkState.Connecting, $"Attaching bus {dev.BusId}…");
        var (code, output) = await Task.Run(() => UsbipExe.Run("attach", "-r", host, "-b", dev.BusId));
        if (code == 0)
        {
            lastAttachError = "";
            SetState(LinkState.Attached, $"Connected  {dev.Vid:x4}:{dev.Pid:x4}  bus {dev.BusId}");
        }
        else
        {
            if (output != lastAttachError) form.Log("Attach failed: " + output);
            lastAttachError = output;
            SetState(LinkState.Error, "Attach failed (see log)");
        }
    }

    async Task<List<FoundTv>> ScanAsync()
    {
        if (scanning) return [];
        scanning = true;
        form.SetScanning(true);
        try
        {
            var tvs = await Discovery.ScanAsync(form.Log);
            form.Log(tvs.Count == 0
                ? "No PadLink TVs found (is the PadLink app installed and running on the TV?)"
                : "Found: " + string.Join("  |  ", tvs));
            form.SetFoundTvs(tvs);
            return tvs;
        }
        finally
        {
            scanning = false;
            form.SetScanning(false);
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
        SetState(LinkState.Waiting, $"{settings.TvName} not at {oldHost}, searching LAN + Tailscale…");
        var tv = (await ScanAsync()).FirstOrDefault(t => t.Name == settings.TvName && t.Host != oldHost);
        if (tv == null) return false;
        await Task.Run(() => UsbipExe.DetachHost(oldHost));
        form.Log($"Found {tv.Name} at {tv.Host} via {tv.Via}, switching");
        Pick(tv);
        return true;
    }

    void Pick(FoundTv tv)
    {
        settings.TvHost = tv.Host;
        settings.TvName = tv.Name;
        settings.Save();
        form.SyncFromSettings();
        WakeLoop();
    }

    void SetState(LinkState s, string text)
    {
        if (s == state && text == stateText) return;
        if (s == LinkState.Attached && state != LinkState.Attached)
            tray.ShowBalloonTip(2000, "PadLink", "Controller connected", ToolTipIcon.None);
        state = s;
        stateText = text;
        tray.Icon = Icons.For(s);
        var tip = "PadLink: " + text;
        tray.Text = tip.Length > 63 ? tip[..63] : tip;
        form.SetStatus(s, text);
        form.Log(text);
    }

    async Task DetachAsync()
    {
        var host = settings.TvHost.Trim();
        if (host.Length == 0 || UsbipExe.ExePath == null) return;
        if (settings.AutoConnect)
        {
            settings.AutoConnect = false; // otherwise the loop re-attaches 3 s later
            settings.Save();
            form.SyncFromSettings();
        }
        await Task.Run(() => UsbipExe.DetachHost(host));
        form.Log("Detached");
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
        form.ForceClose();
        ExitThread();
    }
}
