using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PadLink;

/// <param name="Via">"LAN" or "Tailscale"</param>
public record FoundTv(string Name, string Host, string Via, long RttMs, int DeviceCount)
{
    public override string ToString() =>
        $"{Name} — {Host} ({Via}, {RttMs} ms{(DeviceCount > 0 ? ", controller ready" : "")})";
}

/// <summary>
/// Finds PadLink TVs without any multicast (which Tailscale doesn't carry):
///  - LAN: sweep the local subnet(s) on TCP 3240
///  - Tailscale: ask the local tailscale client for online peers and probe each
/// A host only counts if it answers PadLink's INFO request.
/// </summary>
public static class Discovery
{
    const int MaxParallel = 96;

    public static async Task<List<FoundTv>> ScanAsync(Action<string>? log = null)
    {
        var targets = new Dictionary<string, (string Via, int TimeoutMs)>();
        foreach (var ip in LanCandidates()) targets[ip] = ("LAN", 700);
        var peers = await Task.Run(TailscalePeers);
        foreach (var ip in peers) targets[ip] = ("Tailscale", 2500);
        log?.Invoke($"Scanning {targets.Count - peers.Count} LAN addresses and {peers.Count} Tailscale peers…");

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = targets.Select(async t =>
        {
            await gate.WaitAsync();
            try { return await ProbeAsync(t.Key, t.Value.Via, t.Value.TimeoutMs); }
            finally { gate.Release(); }
        });
        var found = (await Task.WhenAll(tasks)).OfType<FoundTv>().OrderBy(f => f.RttMs).ToList();
        return found;
    }

    /// <summary>Connect, send PadLink INFO, then a USB/IP device list. Null if it isn't a PadLink TV.</summary>
    public static async Task<FoundTv?> ProbeAsync(string host, string via, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var tcp = new TcpClient { NoDelay = true };
            var sw = Stopwatch.StartNew();
            await tcp.ConnectAsync(host, UsbIpClient.Port, cts.Token);
            long rtt = sw.ElapsedMilliseconds;

            var s = tcp.GetStream();
            await s.WriteAsync(new byte[] { 0x01, 0x11, 0x8F, 0x01, 0, 0, 0, 0 }, cts.Token);
            var head = new byte[10];
            await s.ReadExactlyAsync(head, cts.Token);
            if (BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2)) != 0x0F01) return null;
            int len = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(8));
            var name = new byte[len];
            await s.ReadExactlyAsync(name, cts.Token);

            int devices = 0;
            try { devices = (await Task.Run(() => UsbIpClient.List(host, timeoutMs))).Devices.Count; } catch { }
            return new FoundTv(Encoding.UTF8.GetString(name), host, via, rtt, devices);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsTailscaleAddress(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 100 && (b[1] & 0xC0) == 64;
    }

    /// <summary>Every host address on this PC's IPv4 LAN subnets (capped at /22 each).</summary>
    static IEnumerable<string> LanCandidates()
    {
        var result = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork || IsTailscaleAddress(ip)) continue;
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local, no DHCP
                int prefix = Math.Clamp(ua.PrefixLength, 22, 30);
                uint self = BinaryPrimitives.ReadUInt32BigEndian(b);
                uint mask = prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);
                uint net = self & mask, bcast = net | ~mask;
                for (uint h = net + 1; h < bcast; h++)
                {
                    if (h == self) continue;
                    var hb = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(hb, h);
                    result.Add(new IPAddress(hb).ToString());
                }
            }
        }
        return result;
    }

    static string? TailscaleExe()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale"));
        foreach (var d in dirs)
        {
            try
            {
                var p = Path.Combine(d.Trim(), "tailscale.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>IPv4 addresses of online Tailscale peers, from "tailscale status --json".</summary>
    static List<string> TailscalePeers()
    {
        var list = new List<string>();
        var exe = TailscaleExe();
        if (exe == null) return list;
        try
        {
            var psi = new ProcessStartInfo(exe, "status --json")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Peer", out var peers) || peers.ValueKind != JsonValueKind.Object) return list;
            foreach (var peer in peers.EnumerateObject())
            {
                var v = peer.Value;
                if (v.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.False) continue;
                if (!v.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
                foreach (var ip in ips.EnumerateArray())
                {
                    var str = ip.GetString();
                    if (str != null && IPAddress.TryParse(str, out var a) && a.AddressFamily == AddressFamily.InterNetwork)
                        list.Add(str);
                }
            }
        }
        catch { }
        return list;
    }
}
