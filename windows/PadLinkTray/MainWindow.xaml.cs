using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace PadLink;

/// <summary>A found TV as shown in the list.</summary>
public sealed class TvItem(FoundTv tv, bool selected)
{
    public FoundTv Tv { get; } = tv;
    public string Name => Tv.Name;
    public string Detail => Tv.Via == "saved"
        ? $"{Tv.Host}  ·  saved"
        : $"{Tv.Host}  ·  {Tv.Via}  ·  {Tv.RttMs} ms" + (Tv.DeviceCount > 0 ? "  ·  controller plugged in" : "");
    public Visibility SelectedVisibility => selected ? Visibility.Visible : Visibility.Collapsed;
}

public partial class MainWindow : FluentWindow
{
    public event Action? SettingsChanged;
    public event Action? ConnectToggled;
    public event Action? FindClicked;
    public event Action? InstallDriverClicked;
    public event Action? QuitClicked;
    public event Action? HiddenToTray;

    readonly Settings settings;
    List<FoundTv> found = [];
    bool reallyClose;

    public MainWindow(Settings settings)
    {
        this.settings = settings;
        InitializeComponent();
        Loaded += (_, _) => SystemThemeWatcher.Watch(this); // follow Windows light/dark live
        StartupSwitch.IsChecked = Startup.Enabled;
        SyncFromSettings();
    }

    // ---------- called by the controller ----------

    public void SyncFromSettings()
    {
        HostBox.Text = settings.TvHost;
        VendorBox.Text = settings.Vendor;
        CurrentHostText.Text = settings.TvHost.Length > 0
            ? (settings.TvName.Length > 0 ? $"{settings.TvName} at {settings.TvHost}" : $"Using {settings.TvHost}")
            : "Use this if Find TVs can't see your TV";
        ConnectButton.Content = settings.AutoConnect ? "Disconnect" : "Connect";
        ConnectButton.Icon = new SymbolIcon(settings.AutoConnect ? SymbolRegular.PlugDisconnected24 : SymbolRegular.PlugConnected24);
        ConnectButton.Appearance = settings.AutoConnect ? ControlAppearance.Secondary : ControlAppearance.Primary;
        RenderTvList();
    }

    public void SetStatus(LinkState s, string detail)
    {
        (string title, SymbolRegular icon, Color color) = s switch
        {
            LinkState.Attached => ("Controller connected", SymbolRegular.XboxController24, Color.FromRgb(16, 185, 90)),
            LinkState.Connecting => ("Connecting…", SymbolRegular.ArrowSync24, Color.FromRgb(255, 170, 0)),
            LinkState.Waiting => ("Waiting", SymbolRegular.Clock24, Color.FromRgb(255, 170, 0)),
            LinkState.Error => ("Needs attention", SymbolRegular.Warning24, Color.FromRgb(232, 60, 60)),
            _ => ("Not connected", SymbolRegular.PlugDisconnected24, Color.FromRgb(140, 140, 140)),
        };
        StatusTitle.Text = title;
        StatusDetail.Text = detail;
        StatusIcon.Symbol = icon;
        StatusIcon.Foreground = new SolidColorBrush(color);
        StatusBadge.Background = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B));
    }

    public void SetPing(long? ms)
    {
        PingChip.Visibility = ms is null ? Visibility.Collapsed : Visibility.Visible;
        if (ms is long v)
        {
            PingText.Text = $"{v} ms";
            PingChip.ToolTip = $"Network round-trip to the TV. Adds about {Math.Max(1, v / 2)} ms of input lag.";
        }
    }

    public void SetScanning(bool scanning)
    {
        FindButton.IsEnabled = !scanning;
        FindButton.Content = scanning ? "Searching…" : "Find TVs";
        ScanRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning) { NoTvCard.Visibility = Visibility.Visible; NoTvText.Text = "Looking on your network and Tailscale…"; }
        else RenderTvList();
    }

    public void SetFoundTvs(List<FoundTv> tvs)
    {
        found = tvs;
        RenderTvList();
    }

    public void SetDriverStatus(bool ready, bool installing = false)
    {
        DriverText.Text = installing ? "Installing…" : ready ? "Installed (usbip-win2)" : "Not installed. Needed to plug the controller into this PC";
        DriverButton.Visibility = ready || installing ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Log(string line)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        if (LogBox.LineCount > 300) LogBox.Text = string.Join(Environment.NewLine, LogBox.Text.Split(Environment.NewLine)[^200..]);
        LogBox.ScrollToEnd();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void ForceClose()
    {
        reallyClose = true;
        Close();
    }

    // ---------- UI events ----------

    void RenderTvList()
    {
        // Always show the remembered TV, even before a scan found it.
        var items = found.Select(t => new TvItem(t, t.Host == settings.TvHost)).ToList();
        if (settings.TvHost.Length > 0 && !found.Any(t => t.Host == settings.TvHost))
            items.Insert(0, new TvItem(new FoundTv(settings.TvName.Length > 0 ? settings.TvName : "Your TV", settings.TvHost, "saved", 0, 0) { }, true));
        TvList.ItemsSource = items;
        NoTvCard.Visibility = items.Count == 0 || ScanRing.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0 && ScanRing.Visibility != Visibility.Visible)
            NoTvText.Text = "No TV yet. Open PadLink on your TV, then click Find TVs.";
    }

    void ConnectButton_Click(object sender, RoutedEventArgs e) => ConnectToggled?.Invoke();
    void FindButton_Click(object sender, RoutedEventArgs e) => FindClicked?.Invoke();
    void DriverButton_Click(object sender, RoutedEventArgs e) => InstallDriverClicked?.Invoke();
    void Quit_Click(object sender, RoutedEventArgs e) => QuitClicked?.Invoke();

    void TvCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TvItem item || item.Tv.Via == "saved") return;
        settings.TvHost = item.Tv.Host;
        settings.TvName = item.Tv.Name;
        settings.AutoConnect = true;
        Log($"Selected {item.Tv.Name} at {item.Tv.Host} ({item.Tv.Via})");
        SyncFromSettings();
        SettingsChanged?.Invoke();
    }

    void UseHost_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) return;
        if (host != settings.TvHost) settings.TvName = ""; // typed by hand: forget the remembered TV
        settings.TvHost = host;
        settings.AutoConnect = true;
        Log($"Using {host}");
        SyncFromSettings();
        SettingsChanged?.Invoke();
    }

    void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) UseHost_Click(sender, e);
    }

    void SaveVendor_Click(object sender, RoutedEventArgs e)
    {
        settings.Vendor = VendorBox.Text.Trim();
        Log($"Vendor set to {settings.Vendor}");
        SettingsChanged?.Invoke();
    }

    void StartupSwitch_Click(object sender, RoutedEventArgs e)
    {
        try { Startup.Enabled = StartupSwitch.IsChecked == true; }
        catch (Exception ex)
        {
            Log("Could not change startup setting: " + ex.Message);
            StartupSwitch.IsChecked = Startup.Enabled;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!reallyClose)
        {
            e.Cancel = true; // keep running in the tray
            Hide();
            HiddenToTray?.Invoke();
            return;
        }
        base.OnClosing(e);
    }
}
