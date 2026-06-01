using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace PsiphonUI.Services;

public sealed class StartupReaper : IStartupReaper
{
    private readonly ILogger<StartupReaper> _logger;

    public StartupReaper(ILogger<StartupReaper> logger) => _logger = logger;

    public void ReapStaleProcesses()
    {

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var tunnelCoreRoot = Path.Combine(localAppData, AppBrand.SafeName, "tunnel-core");
        var singBoxRoot = Path.Combine(localAppData, AppBrand.SafeName, "singbox-tun");
        var xrayRootLegacy1 = Path.Combine(localAppData, AppBrand.SafeName, "xray-tun");
        var tempRoot = Path.Combine(Path.GetTempPath(), AppBrand.SafeName);
        var legacyTunnelCoreRoot = Path.Combine(localAppData, "Psiphon", "tunnel-core");
        var legacySingBoxRoot = Path.Combine(localAppData, "Psiphon", "singbox-tun");
        var legacyXrayRoot = Path.Combine(localAppData, "Psiphon", "xray-tun");
        var legacyTempRoot = Path.Combine(Path.GetTempPath(), "Psiphon");

        var roots = new[]
        {
            tunnelCoreRoot, singBoxRoot, xrayRootLegacy1, tempRoot,
            legacyTunnelCoreRoot, legacySingBoxRoot, legacyXrayRoot, legacyTempRoot,
        };

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Process.GetProcesses failed; skipping reaper");
            return;
        }

        var ownPid = -1;
        try { ownPid = Process.GetCurrentProcess().Id; } catch {  }

        var killed = 0;
        foreach (var p in processes)
        {
            try
            {
                if (p.Id == ownPid) continue;

                string? imagePath = null;
                try
                {

                    imagePath = p.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(imagePath)) continue;

                if (!IsUnderAny(imagePath, roots)) continue;

                _logger.LogInformation(
                    "Killing stale child pid {Pid} ({Image})",
                    p.Id,
                    imagePath);

                try
                {
                    p.Kill(entireProcessTree: true);
                    if (!p.WaitForExit(2000))
                    {
                        _logger.LogWarning(
                            "Stale child pid {Pid} did not exit within 2s",
                            p.Id);
                    }
                    else
                    {
                        killed++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to kill stale pid {Pid}", p.Id);
                }
            }
            finally
            {
                try { p.Dispose(); } catch {  }
            }
        }

        TryRemoveStaleLocks(tunnelCoreRoot);
        TryRemoveStaleLocks(legacyTunnelCoreRoot);

        if (killed > 0)
        {
            _logger.LogInformation("Reaper killed {Count} stale child process(es)", killed);
        }
    }

    private static bool IsUnderAny(string path, string[] roots)
    {

        var normalised = NormalisePath(path);
        foreach (var root in roots)
        {
            var nroot = NormalisePath(root);
            if (string.IsNullOrEmpty(nroot)) continue;
            if (normalised.StartsWith(nroot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalised.Equals(nroot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string NormalisePath(string p)
    {
        try
        {
            return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return p;
        }
    }

    private void TryRemoveStaleLocks(string root)
    {
        if (!Directory.Exists(root)) return;

        try
        {
            foreach (var lockFile in Directory.EnumerateFiles(
                         root, "*.lock", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(lockFile);
                    _logger.LogInformation("Removed stale lock {Path}", lockFile);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove stale lock {Path}", lockFile);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Enumerating stale locks under {Root} failed", root);
        }
    }
}
