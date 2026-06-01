using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public sealed class TunnelCoreManager : ITunnelCoreManager, IDisposable
{
    private const int MaxLogLines = 5000;

    private readonly ILogger<TunnelCoreManager> _logger;
    private readonly ISettingsService _settings;
    private readonly ISystemProxyService _systemProxy;
    private readonly IChildProcessGuard _childGuard;
    private readonly object _stateLock = new();
    private readonly List<string> _recentLog = new();
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _workDir;
    private volatile bool _userWantsConnection;
    private CancellationTokenSource? _retryDelayCts;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile bool _disposed;

    public TunnelCoreManager(
        ILogger<TunnelCoreManager> logger,
        ISettingsService settings,
        ISystemProxyService systemProxy,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _settings = settings;
        _systemProxy = systemProxy;
        _childGuard = childGuard;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public int SocksProxyPort { get; private set; }
    public int HttpProxyPort { get; private set; }
    public string ClientRegion { get; private set; } = "";

    public string ConnectedServerRegion { get; private set; } = "";

    public string CurrentRouteIp { get; private set; } = "";
    public string CurrentRouteSni { get; private set; } = "";

    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    private readonly List<string> _availableRegions = new();
    public IReadOnlyList<string> AvailableEgressRegions => _availableRegions.AsReadOnly();

    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_stateLock) return _recentLog.ToArray();
        }
    }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<Notice>? NoticeReceived;
    public event EventHandler<string>? LogLineAppended;
    public event EventHandler? BytesTransferredChanged;
    public event EventHandler? LogCleared;
    public event EventHandler? RouteChanged;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        if (_disposed) return;

        _userWantsConnection = true;
        CancelPendingRestart();

        if (_process is not null && !_process.HasExited)
        {
            _logger.LogInformation("Tunnel is already running");
            return;
        }

        SetState(ConnectionState.Connecting);
        AppendLog("Starting secure tunnel...");

        BytesSent = 0;
        BytesReceived = 0;
        ConnectedServerRegion = "";
        CurrentRouteIp = "";
        CurrentRouteSni = "";
        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
        RouteChanged?.Invoke(this, EventArgs.Empty);

        Process? startedProcess = null;
        try
        {
            _workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppBrand.SafeName,
                "tunnel-core");
            Directory.CreateDirectory(_workDir);

            var exePath = ResolveTunnelCoreExe();
            var configPath = Path.Combine(_workDir, "config.json");
            File.WriteAllText(configPath, BuildConfigJson());

            var serverListPath = WriteEmbeddedServerList();

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WorkingDirectory = _workDir,
            };
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(configPath);
            if (serverListPath is not null)
            {
                psi.ArgumentList.Add("--serverList");
                psi.ArgumentList.Add(serverListPath);
            }

            startedProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            startedProcess.OutputDataReceived += (_, e) => OnLineReceived(e.Data, stderr: false);
            startedProcess.ErrorDataReceived += (_, e) => OnLineReceived(e.Data, stderr: true);
            startedProcess.Exited += OnProcessExited;

            if (!startedProcess.Start())
            {
                throw new InvalidOperationException("Failed to start psiphon-tunnel-core.exe");
            }

            _process = startedProcess;
            _childGuard.Adopt(startedProcess);

            startedProcess.BeginOutputReadLine();
            startedProcess.BeginErrorReadLine();

            _logger.LogInformation("psiphon-tunnel-core started (pid {Pid})", startedProcess.Id);
            AppendLog("Tunnel engine started. Waiting for a secure route...");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start tunnel");
            AppendLog($"Could not start the tunnel engine: {ex.Message}");
            CleanupFailedStart(startedProcess);
            _process = null;
            _systemProxy.Clear();
            if (_userWantsConnection)
            {
                AppendLog("Retrying automatically in a few seconds...");
                SetState(ConnectionState.Connecting);
                ScheduleAutoRestart(TimeSpan.FromSeconds(5));
            }
            else
            {
                SetState(ConnectionState.Disconnected);
            }
        }
    }

    private void CleanupFailedStart(Process? process)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception killEx)
        {
            _logger.LogWarning(killEx, "Failed to clean up partially started tunnel process");
        }
        finally
        {
            process.Dispose();
        }
    }

    public async Task RestartAsync()
    {

        if (State != ConnectionState.Connected && State != ConnectionState.Connecting)
        {
            return;
        }

        await StopAsync();
        await StartAsync();
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        _userWantsConnection = false;
        CancelPendingRestart();

        var proc = _process;
        if (proc is null || proc.HasExited)
        {
            _process = null;
            SetState(ConnectionState.Disconnected);
            return;
        }

        SetState(ConnectionState.Disconnecting);
        AppendLog("Stopping tunnel and cleaning local proxy settings...");

        try
        {

            try { proc.StandardInput.Close(); } catch {  }

            if (!await WaitForExitAsync(proc, TimeSpan.FromSeconds(5)))
            {
                _logger.LogWarning("Tunnel did not exit gracefully; killing");
                try { proc.Kill(entireProcessTree: true); } catch {  }
                await WaitForExitAsync(proc, TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            _cts?.Cancel();
            _process = null;

            if (_settings.Settings.SetSystemProxy)
            {
                _systemProxy.Clear();
            }

            ConnectedServerRegion = "";
            CurrentRouteIp = "";
            CurrentRouteSni = "";
            BytesSent = 0;
            BytesReceived = 0;
            BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
            RouteChanged?.Invoke(this, EventArgs.Empty);

            SetState(ConnectionState.Disconnected);

            lock (_stateLock) _recentLog.Clear();
            LogCleared?.Invoke(this, EventArgs.Empty);

            AppendLog("Stopped tunnel");
        }
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await p.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return p.HasExited;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var exitedProcess = sender as Process;
        var exitCode = -1;
        try { exitCode = exitedProcess?.ExitCode ?? -1; } catch { }
        _logger.LogInformation("psiphon-tunnel-core exited with code {Code}", exitCode);

        if (ReferenceEquals(_process, exitedProcess))
        {
            _process = null;
        }

        if (State == ConnectionState.Disconnecting)
        {
            return;
        }

        _systemProxy.Clear();

        if (_disposed)
        {
            SetState(ConnectionState.Disconnected);
            return;
        }

        if (_userWantsConnection)
        {
            AppendLog("Tunnel engine stopped unexpectedly; reconnecting...");
            SetState(ConnectionState.Connecting);
            ScheduleAutoRestart(TimeSpan.FromSeconds(3));
        }
        else
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    private void ScheduleAutoRestart(TimeSpan delay)
    {
        if (_disposed) return;

        CancelPendingRestart();
        var cts = new CancellationTokenSource();
        _retryDelayCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_userWantsConnection || _disposed) return;
            try { await StartAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-restart attempt failed");
                if (_userWantsConnection)
                {
                    ScheduleAutoRestart(TimeSpan.FromSeconds(10));
                }
            }
        });
    }

    private void CancelPendingRestart()
    {
        var cts = _retryDelayCts;
        _retryDelayCts = null;
        if (cts is null) return;
        try { cts.Cancel(); } catch {  }
        try { cts.Dispose(); } catch {  }
    }

    private void OnLineReceived(string? line, bool stderr)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            var notice = JsonSerializer.Deserialize<Notice>(line);
            if (notice is not null && !string.IsNullOrEmpty(notice.NoticeType))
            {
                HandleNotice(notice);
                NoticeReceived?.Invoke(this, notice);
                var pretty = LogSanitizer.FormatNotice(notice.NoticeType, notice.Data);
                if (!string.IsNullOrEmpty(pretty))
                {
                    AppendLog(pretty!);
                }
                return;
            }
        }
        catch
        {

        }

        if (stderr)
        {
            AppendLog(LogSanitizer.Scrub(line));
        }
    }

    private void HandleNotice(Notice notice)
    {
        switch (notice.NoticeType)
        {
            case "Tunnels":
                {
                    var count = notice.Data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number
                        ? c.GetInt32()
                        : 0;
                    if (count > 0)
                    {
                        SetState(ConnectionState.Connected);
                        if (_settings.Settings.SetSystemProxy && HttpProxyPort > 0)
                        {
                            _systemProxy.Set(HttpProxyPort);
                        }
                    }
                    else if (State == ConnectionState.Connected)
                    {
                        SetState(ConnectionState.Connecting);
                    }
                    break;
                }

            case "ListeningSocksProxyPort":
                if (notice.Data.TryGetProperty("port", out var sp) && sp.ValueKind == JsonValueKind.Number)
                {
                    SocksProxyPort = sp.GetInt32();
                }
                break;

            case "ListeningHttpProxyPort":
                if (notice.Data.TryGetProperty("port", out var hp) && hp.ValueKind == JsonValueKind.Number)
                {
                    HttpProxyPort = hp.GetInt32();
                }
                break;

            case "ClientRegion":
                if (notice.Data.TryGetProperty("region", out var cr) && cr.ValueKind == JsonValueKind.String)
                {
                    ClientRegion = cr.GetString() ?? "";
                }
                break;

            case "ConnectedServerRegion":

                if (notice.Data.TryGetProperty("serverRegion", out var srv) && srv.ValueKind == JsonValueKind.String)
                {
                    ConnectedServerRegion = srv.GetString() ?? "";
                }
                break;

            case "AvailableEgressRegions":
                if (notice.Data.TryGetProperty("regions", out var regs) && regs.ValueKind == JsonValueKind.Array)
                {
                    _availableRegions.Clear();
                    foreach (var r in regs.EnumerateArray())
                    {
                        if (r.ValueKind == JsonValueKind.String)
                        {
                            var s = r.GetString();
                            if (!string.IsNullOrEmpty(s)) _availableRegions.Add(s);
                        }
                    }
                }
                break;

            case "BytesTransferred":
                {

                    var changed = false;
                    if (notice.Data.TryGetProperty("sent", out var bs) && bs.ValueKind == JsonValueKind.Number)
                    {
                        var d = bs.GetInt64();
                        if (d > 0) { BytesSent += d; changed = true; }
                    }
                    if (notice.Data.TryGetProperty("received", out var br) && br.ValueKind == JsonValueKind.Number)
                    {
                        var d = br.GetInt64();
                        if (d > 0) { BytesReceived += d; changed = true; }
                    }
                    if (changed)
                    {
                        BytesTransferredChanged?.Invoke(this, EventArgs.Empty);
                    }
                    break;
                }

            case "ActiveTunnel":
                if (notice.Data.TryGetProperty("dialAddress", out var da) && da.ValueKind == JsonValueKind.String)
                {
                    var dialAddr = UnescapeRouteToken(da.GetString() ?? "");
                    var protocol = notice.Data.TryGetProperty("protocol", out var proto) && proto.ValueKind == JsonValueKind.String
                        ? proto.GetString() : "";
                    var ipChanged = false;
                    var sniChanged = false;
                    if (dialAddr.Length > 0)
                    {
                        AppendLog($"Route: {dialAddr} via {protocol}");
                        var newIp = ExtractIpFromDialAddress(dialAddr);
                        if (!string.IsNullOrEmpty(newIp) && newIp != CurrentRouteIp)
                        {
                            CurrentRouteIp = newIp;
                            ipChanged = true;
                        }
                    }
                    if (notice.Data.TryGetProperty("meekSNIServerName", out var ms) && ms.ValueKind == JsonValueKind.String)
                    {
                        var sni = UnescapeRouteToken(ms.GetString() ?? "");
                        if (sni != CurrentRouteSni)
                        {
                            CurrentRouteSni = sni;
                            sniChanged = true;
                        }
                    }
                    if (ipChanged || sniChanged)
                    {
                        MaybePersistFoundRoute();
                        RouteChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
                break;

            case "Info":
                if (notice.Data.TryGetProperty("message", out var im) && im.ValueKind == JsonValueKind.String)
                {
                    var msg = im.GetString() ?? "";
                    var (foundIp, foundSni) = TryParseCdnScanFound(msg);
                    if (!string.IsNullOrEmpty(foundIp))
                    {
                        var anyChange = false;
                        if (foundIp != CurrentRouteIp)
                        {
                            CurrentRouteIp = foundIp;
                            anyChange = true;
                        }
                        if (!string.IsNullOrEmpty(foundSni) && foundSni != CurrentRouteSni)
                        {
                            CurrentRouteSni = foundSni!;
                            anyChange = true;
                        }
                        if (anyChange)
                        {
                            MaybePersistFoundRoute();
                            RouteChanged?.Invoke(this, EventArgs.Empty);
                        }
                    }
                }
                break;
        }
    }

    private static string ExtractIpFromDialAddress(string dialAddress)
    {
        var hostPart = dialAddress;
        var colonIdx = dialAddress.LastIndexOf(':');
        if (colonIdx > 0) hostPart = dialAddress[..colonIdx];
        return hostPart.Trim('[', ']');
    }

    private static readonly System.Text.RegularExpressions.Regex CdnScanFoundRegex =
        new(@"cdn fronting scan found \(ip:\s*([^\s,)]+),\s*sni:\s*([^\s,)]+)\)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string Ip, string Sni) TryParseCdnScanFound(string msg)
    {
        var m = CdnScanFoundRegex.Match(msg);
        if (!m.Success) return ("", "");
        return (UnescapeRouteToken(m.Groups[1].Value), UnescapeRouteToken(m.Groups[2].Value));
    }

    private static string UnescapeRouteToken(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Replace("\\", "");
    }

    private void MaybePersistFoundRoute()
    {
        var settings = _settings.Settings;
        if (!settings.SaveFoundIpsAndSni) return;
        if (string.IsNullOrEmpty(CurrentRouteIp) && string.IsNullOrEmpty(CurrentRouteSni)) return;

        var changed = false;
        if (!string.IsNullOrEmpty(CurrentRouteIp))
        {
            var newList = AppendUniqueLine(settings.CdnFrontingCustomIpList, CurrentRouteIp);
            if (newList != settings.CdnFrontingCustomIpList)
            {
                settings.CdnFrontingCustomIpList = newList;
                changed = true;
            }
        }
        if (!string.IsNullOrEmpty(CurrentRouteSni))
        {
            var newSnis = AppendUniqueLine(settings.CdnFrontingCustomSni, CurrentRouteSni);
            if (newSnis != settings.CdnFrontingCustomSni)
            {
                settings.CdnFrontingCustomSni = newSnis;
                changed = true;
            }
        }
        if (changed)
        {
            _settings.Save();
            AppendLog($"Saved route: ip={CurrentRouteIp} sni={CurrentRouteSni}");
        }
    }

    private static string AppendUniqueLine(string current, string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed)) return current;
        var lines = (current ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var ln in lines)
        {
            if (string.Equals(ln.Trim(), trimmed, StringComparison.OrdinalIgnoreCase))
                return current ?? "";
        }
        var existing = (current ?? "").TrimEnd('\r', '\n');
        return existing.Length == 0 ? trimmed : existing + Environment.NewLine + trimmed;
    }

    private string BuildConfigJson()
    {
        var s = _settings.Settings;
        var dataRoot = Path.Combine(_workDir!, "data");
        Directory.CreateDirectory(dataRoot);

        var cfg = new JsonObject
        {
            ["ClientPlatform"] = $"{EmbeddedValues.ClientPlatform}_{Environment.OSVersion.Version}",
            ["ClientVersion"] = EmbeddedValues.ClientVersion,
            ["PropagationChannelId"] = EmbeddedValues.PropagationChannelId,
            ["SponsorId"] = EmbeddedValues.SponsorId,
            ["RemoteServerListURLs"] = JsonNode.Parse(EmbeddedValues.RemoteServerListUrlsJson),
            ["ObfuscatedServerListRootURLs"] = JsonNode.Parse(EmbeddedValues.ObfuscatedServerListRootUrlsJson),
            ["RemoteServerListSignaturePublicKey"] = EmbeddedValues.RemoteServerListSignaturePublicKey,
            ["ServerEntrySignaturePublicKey"] = EmbeddedValues.ServerEntrySignaturePublicKey,
            ["DataRootDirectory"] = dataRoot,
            ["MigrateDataStoreDirectory"] = dataRoot,
            ["UseIndistinguishableTLS"] = true,
            ["EmitDiagnosticNotices"] = true,
            ["EmitDiagnosticNetworkParameters"] = true,
            ["EmitServerAlerts"] = true,

            ["EmitBytesTransferred"] = true,
            ["FeedbackUploadURLs"] = JsonNode.Parse(EmbeddedValues.FeedbackUploadUrlsJson),
            ["FeedbackEncryptionPublicKey"] = EmbeddedValues.FeedbackEncryptionPublicKey,
            ["EnableFeedbackUpload"] = true,

            ["EstablishTunnelTimeoutSeconds"] = 0,

            ["LocalHttpProxyPort"] = SanitizeListenPort(s.LocalHttpProxyPort),
            ["LocalSocksProxyPort"] = SanitizeListenPort(s.LocalSocksProxyPort),
        };

        if (s.AllowLanConnections)
        {
            cfg["ListenInterface"] = "any";

            if (!string.IsNullOrEmpty(s.LanProxyUsername) &&
                !string.IsNullOrEmpty(s.LanProxyPassword))
            {
                cfg["LocalProxyUsername"] = s.LanProxyUsername;
                cfg["LocalProxyPassword"] = s.LanProxyPassword;
            }
        }

        if (!string.IsNullOrEmpty(s.EgressRegion))
        {
            cfg["EgressRegion"] = s.EgressRegion;
        }

        if (s.DisableTimeouts)
        {
            cfg["NetworkLatencyMultiplierLambda"] = 0.1;
        }

        var upstreamProxyUrl = string.IsNullOrWhiteSpace(s.UpstreamProxy)
            ? GetSystemHttpProxy()
            : NormalizeProxyUrl(s.UpstreamProxy);
        cfg["UpstreamProxyUrl"] = upstreamProxyUrl;

        if (!string.IsNullOrEmpty(upstreamProxyUrl))
        {
            AppendLog($"Using upstream proxy: {LogSanitizer.Scrub(upstreamProxyUrl)}");
        }

        ApplyAdvancedTunnelConfig(cfg, s);

        return cfg.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void ApplyAdvancedTunnelConfig(JsonObject cfg, Models.UserSettings s)
    {
        if (s.BeastMode)
        {
            cfg["AggressiveEstablishment"] = true;
        }

        switch (s.ProtocolMode)
        {
            case "cdn_fronting":

                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "FRONTED-MEEK-CDN-OSSH",
                    "FRONTED-MEEK-CDN-HTTP-OSSH",
                    "FRONTED-MEEK-CDN-QUIC-OSSH");
                cfg["DisableTactics"] = true;

                var hasUserIpList = !string.IsNullOrWhiteSpace(s.CdnFrontingCustomIpList);
                var includeBuiltInDefaults = s.AutoFindIpAndSni || !hasUserIpList;

                cfg["FrontedMeekDialOverrides"] = CdnFrontingBuilder.BuildDialOverrides(
                    s.CdnFrontingCustomIpList,
                    s.CdnFrontingCustomSni,
                    includeBuiltInDefaults);
                cfg["FrontedMeekDialOverridesProbability"] = 1.0;
                cfg["FrontedMeekCDNScanUseBuiltInSpec"] = s.AutoFindIpAndSni;
                break;

            case "direct":

                cfg["LimitTunnelProtocols"] = new JsonArray(
                    "SSH", "OSSH", "TLS-OSSH",
                    "UNFRONTED-MEEK-OSSH",
                    "UNFRONTED-MEEK-HTTPS-OSSH",
                    "UNFRONTED-MEEK-SESSION-TICKET-OSSH",
                    "QUIC-OSSH", "SHADOWSOCKS-OSSH",
                    "FRONTED-MEEK-OSSH",
                    "FRONTED-MEEK-CDN-OSSH",
                    "FRONTED-MEEK-HTTP-OSSH",
                    "FRONTED-MEEK-CDN-HTTP-OSSH",
                    "FRONTED-MEEK-QUIC-OSSH",
                    "FRONTED-MEEK-CDN-QUIC-OSSH");
                cfg["DisableTactics"] = true;
                break;

            case "auto":
            default:

                break;
        }
    }

    private const string CachedTunnelExeName = "Se7enPro.Tunnel.exe";

    private string ResolveTunnelCoreExe()
    {

        var appDir = AppContext.BaseDirectory;

        var bundled = Path.Combine(appDir, "Resources", "psiphon-tunnel-core.exe");
        if (!File.Exists(bundled))
        {

            bundled = Path.Combine(appDir, "psiphon-tunnel-core.exe");
            if (!File.Exists(bundled))
            {
                throw new FileNotFoundException(
                    "psiphon-tunnel-core.exe not found next to " + AppBrand.Name,
                    bundled);
            }
        }

        var copyTo = Path.Combine(_workDir!, CachedTunnelExeName);

        foreach (var stale in Directory.EnumerateFiles(_workDir!, "*.exe"))
        {
            if (string.Equals(Path.GetFileName(stale), CachedTunnelExeName,
                              StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try { File.Delete(stale); } catch {  }
        }

        if (!FileCacheHelper.IsCachedCopyUpToDate(bundled, copyTo))
        {
            try
            {
                File.Copy(bundled, copyTo, overwrite: true);
            }
            catch (IOException)
            {

                if (!File.Exists(copyTo))
                {
                    throw;
                }
            }
        }

        return copyTo;
    }

    private string? WriteEmbeddedServerList()
    {
        try
        {
            var plain = SecretStore.DecryptResource("PsiphonUI.Resources.server_entries.bin");
            var dest = Path.Combine(_workDir!, "server_entries.txt");
            File.WriteAllBytes(dest, plain);
            Array.Clear(plain, 0, plain.Length);
            return dest;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Embedded server list unavailable; tunnel-core will rely on remote server list fetch");
            return null;
        }
    }

    private static int SanitizeListenPort(int port)
        => port is >= 1 and <= 65535 ? port : 0;

    private static string NormalizeProxyUrl(string url)
    {
        url = url.Trim();
        if (string.IsNullOrEmpty(url)) return "";

        if (url.Contains("://")) return url;

        return $"http://{url}";
    }

    private static string GetSystemHttpProxy()
    {
        try
        {
            var systemProxy = System.Net.WebRequest.GetSystemWebProxy();

            var probe = new Uri("https://example.com/");
            var proxyUri = systemProxy.GetProxy(probe);

            if (proxyUri is null || proxyUri.Equals(probe) || systemProxy.IsBypassed(probe))
                return "";

            return $"http://{proxyUri.Host}:{proxyUri.Port}";
        }
        catch
        {
            return "";
        }
    }

    private void SetState(ConnectionState s)
    {
        if (State == s) return;
        State = s;
        StateChanged?.Invoke(this, s);
    }

    private void AppendLog(string line)
    {
        lock (_stateLock)
        {
            _recentLog.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_recentLog.Count > MaxLogLines)
            {
                _recentLog.RemoveRange(0, _recentLog.Count - MaxLogLines);
            }
        }
        LogLineAppended?.Invoke(this, line);
    }

    public void Dispose()
    {
        _disposed = true;
        _userWantsConnection = false;
        CancelPendingRestart();
        try { _cts?.Cancel(); } catch {  }
        try { _process?.Kill(entireProcessTree: true); } catch {  }
        _process?.Dispose();
        _cts?.Dispose();
        _lifecycleGate.Dispose();
    }
}
