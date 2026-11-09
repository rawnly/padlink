using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PadLink;

public record RemoteDevice(string BusId, ushort Vid, ushort Pid);

/// <summary>Speaks USB/IP OP_REQ_DEVLIST directly to the TV: no text parsing, and the TCP connect time doubles as a ping.</summary>
public static class UsbIpClient
{
    public const int Port = 3240;

    public static (List<RemoteDevice> Devices, long RttMs) List(string host, int timeoutMs = 2000)
    {
        using var tcp = new TcpClient { NoDelay = true };
        var sw = Stopwatch.StartNew();
        try
        {
            if (!tcp.ConnectAsync(host, Port).Wait(timeoutMs)) throw new TimeoutException("TV not reachable");
        }
        catch (AggregateException e) { throw e.InnerException ?? e; }
        long rtt = sw.ElapsedMilliseconds;

        var s = tcp.GetStream();
        s.ReadTimeout = timeoutMs;
        s.Write([0x01, 0x11, 0x80, 0x05, 0, 0, 0, 0]); // version 1.1.1, OP_REQ_DEVLIST

        var head = ReadExact(s, 12);
        uint n = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(8));
        if (n > 64) throw new InvalidDataException("bad device list");

        var list = new List<RemoteDevice>();
        for (int i = 0; i < n; i++)
        {
            var d = ReadExact(s, 312);
            string busId = Encoding.ASCII.GetString(d, 256, 32).TrimEnd('\0');
            ushort vid = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(300));
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(302));
            ReadExact(s, d[311] * 4); // interface descriptors
            list.Add(new RemoteDevice(busId, vid, pid));
        }
        return (list, rtt);
    }

    static byte[] ReadExact(NetworkStream s, int len)
    {
        var buf = new byte[len];
        int off = 0;
        while (off < len)
        {
            int r = s.Read(buf, off, len - off);
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }
}

/// <summary>Thin wrapper around usbip-win2's usbip.exe (it owns the virtual USB driver).</summary>
public static class UsbipExe
{
    public static string? ExePath { get; private set; } = Find();

    /// <summary>Look again (e.g. right after the bundled installer ran).</summary>
    public static void Refresh() => ExePath = Find();

    static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.AddRange(InstallLocationsFromRegistry());
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "USBip"));
        foreach (var d in dirs)
        {
            try
            {
                var p = Path.Combine(d.Trim(), "usbip.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>InstallLocation of anything named "USBip*" in the uninstall registry (Inno Setup writes it).</summary>
    static IEnumerable<string> InstallLocationsFromRegistry()
    {
        var result = new List<string>();
        try
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var un = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (un == null) return result;
            foreach (var name in un.GetSubKeyNames())
            {
                using var k = un.OpenSubKey(name);
                if (k?.GetValue("DisplayName") is string dn && dn.StartsWith("USBip", StringComparison.OrdinalIgnoreCase)
                    && k.GetValue("InstallLocation") is string loc)
                {
                    result.Add(loc);
                    result.Add(Path.Combine(loc, "bin"));
                }
            }
        }
        catch { }
        return result;
    }

    public static (int Code, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(20000))
        {
            try { p.Kill(); } catch { }
            return (-1, "usbip.exe timed out");
        }
        return (p.ExitCode, (o.Result + e.Result).Trim());
    }

    /// <summary>Strings that identify the TV in "usbip port" output (the name typed plus its resolved IPs).</summary>
    static List<string> HostKeys(string host)
    {
        var keys = new List<string> { host };
        try { keys.AddRange(Dns.GetHostAddresses(host).Select(a => a.ToString())); } catch { }
        return keys;
    }

    public static bool IsAttached(string host)
    {
        var (_, output) = Run("port");
        return HostKeys(host).Any(k => output.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Detach every port imported from this host.</summary>
    public static void DetachHost(string host)
    {
        var keys = HostKeys(host);
        var (_, output) = Run("port");
        string? port = null;
        foreach (var line in output.Split('\n'))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*Port\s+0*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) port = m.Groups[1].Value;
            if (port != null && keys.Any(k => line.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                Run("detach", "-p", port);
                port = null;
            }
        }
    }
}
