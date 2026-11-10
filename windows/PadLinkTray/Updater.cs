using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace PadLink;

/// <summary>
/// Over-the-air updates through Velopack, with the packages stored on GitHub Releases.
/// Only installed copies update. A portable or `dotnet run` build reports itself as a dev build and never updates.
/// </summary>
sealed class Updater
{
    public const string RepoUrl = "https://github.com/rawnly/padlink";

    readonly UpdateManager mgr = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    VelopackAsset? downloaded;
    bool busy;

    public bool IsInstalled => mgr.IsInstalled;

    public string CurrentVersion => mgr.IsInstalled && mgr.CurrentVersion is { } v
        ? v.ToString()
        : (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?")
            .Split('+')[0]; // drop the commit hash the SDK appends

    /// <summary>An update that is downloaded and waiting to be applied, if any.</summary>
    public VelopackAsset? Pending => downloaded ?? (mgr.IsInstalled ? mgr.UpdatePendingRestart : null);

    /// <summary>Checks GitHub and downloads a newer version. Returns it once it's ready to apply, or null.</summary>
    public async Task<VelopackAsset?> CheckAndDownloadAsync()
    {
        if (!mgr.IsInstalled || busy) return Pending;
        busy = true;
        try
        {
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return Pending;
            await mgr.DownloadUpdatesAsync(info);
            downloaded = info.TargetFullRelease;
            return downloaded;
        }
        finally { busy = false; }
    }

    /// <summary>Exits now, installs the pending update and starts the new version.</summary>
    public void ApplyAndRestart(VelopackAsset update) => mgr.ApplyUpdatesAndRestart(update);

    /// <summary>Installs the pending update silently once this process has exited, without restarting.</summary>
    public void ApplyOnExit(VelopackAsset update) => mgr.WaitExitThenApplyUpdates(update, silent: true, restart: false);
}
