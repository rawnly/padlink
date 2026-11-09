namespace PadLink;

/// <summary>The small settings/status window. Closing it just hides it to the tray.</summary>
public sealed class MainForm : Form
{
    public event Action? SettingsChanged;
    public event Action? DetachClicked;
    public event Action? ExitClicked;
    public event Action? FindClicked;

    readonly Settings settings;
    readonly ComboBox hostBox = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
    readonly Button findButton = new() { Text = "Find TVs", AutoSize = true };
    readonly Label hint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly TextBox vendorBox = new() { Width = 70 };
    readonly CheckBox autoBox = new() { Text = "Auto-connect", AutoSize = true };
    readonly CheckBox startupBox = new() { Text = "Start with Windows", AutoSize = true };
    readonly Label statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 16f) };
    readonly Label statusLabel = new() { Text = "Starting…", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
    readonly Label pingLabel = new() { Text = "", AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly TextBox logBox = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9f) };
    bool forceClose;

    public MainForm(Settings settings)
    {
        this.settings = settings;
        Text = "PadLink";
        Icon = Icons.Off;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(600, 420);
        MinimumSize = new Size(560, 340);

        var apply = new Button { Text = "Apply", AutoSize = true };
        var detach = new Button { Text = "Detach", AutoSize = true };
        var exit = new Button { Text = "Exit", AutoSize = true };
        apply.Click += (_, _) => ApplyText();
        detach.Click += (_, _) => DetachClicked?.Invoke();
        exit.Click += (_, _) => ExitClicked?.Invoke();
        hostBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyText(); e.SuppressKeyPress = true; } };
        vendorBox.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplyText(); e.SuppressKeyPress = true; } };
        autoBox.CheckedChanged += (_, _) =>
        {
            if (settings.AutoConnect == autoBox.Checked) return;
            settings.AutoConnect = autoBox.Checked;
            SettingsChanged?.Invoke();
        };
        startupBox.CheckedChanged += (_, _) =>
        {
            try { Startup.Enabled = startupBox.Checked; }
            catch (Exception ex) { Log("Could not change startup setting: " + ex.Message); }
        };

        findButton.Click += (_, _) => FindClicked?.Invoke();
        hostBox.SelectionChangeCommitted += (_, _) =>
        {
            if (hostBox.SelectedItem is not FoundTv tv) return;
            settings.TvHost = tv.Host;
            settings.TvName = tv.Name;
            Log($"Selected {tv.Name} at {tv.Host} ({tv.Via})");
            SettingsChanged?.Invoke();
            BeginInvoke(SyncFromSettings); // show the bare address once the combo has committed
        };

        var status = Row(statusDot, statusLabel);
        var addr = Row(new Label { Text = "TV", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, hostBox, findButton, apply);
        var opts = Row(autoBox, startupBox,
                       new Label { Text = "Vendor", AutoSize = true, Padding = new Padding(8, 5, 0, 0) }, vendorBox, detach, exit);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(status);
        root.Controls.Add(pingLabel);
        root.Controls.Add(addr);
        root.Controls.Add(hint);
        root.Controls.Add(opts);
        root.Controls.Add(logBox);
        Controls.Add(root);

        SyncFromSettings();
        startupBox.Checked = Startup.Enabled;
    }

    static FlowLayoutPanel Row(params Control[] items)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 4) };
        p.Controls.AddRange(items);
        return p;
    }

    void ApplyText()
    {
        var host = hostBox.SelectedItem is FoundTv tv ? tv.Host : hostBox.Text.Trim();
        if (host != settings.TvHost) settings.TvName = ""; // typed by hand: forget the remembered TV
        settings.TvHost = host;
        settings.Vendor = vendorBox.Text.Trim();
        Log($"Saved: TV {settings.TvHost}, vendor {settings.Vendor}");
        SettingsChanged?.Invoke();
    }

    public void SyncFromSettings()
    {
        hostBox.Text = settings.TvHost;
        vendorBox.Text = settings.Vendor;
        autoBox.Checked = settings.AutoConnect;
        hint.Text = settings.TvName.Length > 0
            ? $"Remembered TV: {settings.TvName}. If its address stops answering, PadLink looks for it on the LAN and Tailscale."
            : "Click Find TVs, or type the TV's LAN IP or Tailscale 100.x address.";
    }

    public void SetScanning(bool scanning)
    {
        findButton.Enabled = !scanning;
        findButton.Text = scanning ? "Searching…" : "Find TVs";
    }

    public void SetFoundTvs(List<FoundTv> tvs)
    {
        hostBox.Items.Clear();
        foreach (var tv in tvs) hostBox.Items.Add(tv);
        hostBox.Text = settings.TvHost;
        if (tvs.Count > 0 && Visible) hostBox.DroppedDown = true;
    }

    public void SetStatus(LinkState s, string text)
    {
        statusLabel.Text = text;
        statusDot.ForeColor = s switch
        {
            LinkState.Attached => Color.FromArgb(60, 180, 90),
            LinkState.Waiting or LinkState.Connecting => Color.FromArgb(230, 170, 30),
            LinkState.Error => Color.FromArgb(215, 70, 60),
            _ => Color.Gray,
        };
        Icon = Icons.For(s);
    }

    public void SetPing(long? ms) =>
        pingLabel.Text = ms is long v ? $"Network round-trip to TV: {v} ms  (adds ≈ {Math.Max(1, v / 2)} ms input lag)" : "";

    public void Log(string line)
    {
        logBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (logBox.Lines.Length > 300) logBox.Lines = logBox.Lines[^200..];
    }

    public void ForceClose()
    {
        forceClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!forceClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide(); // keep running in the tray
            return;
        }
        base.OnFormClosing(e);
    }
}
