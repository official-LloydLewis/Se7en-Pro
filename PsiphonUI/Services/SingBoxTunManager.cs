using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PsiphonUI.Models;

namespace PsiphonUI.Services;

public sealed class SingBoxTunManager : ITunManager
{
    private const string TunInterfaceName = "psiphonui_tun";

    private const string CachedSingBoxExeName = "Se7enPro.Helper.exe";

    /// <summary>
    /// Process name of the psiphon-tunnel-core child process.
    /// Must match <see cref="TunnelCoreManager.CachedTunnelExeName"/>.
    /// Traffic from this process is routed directly (bypasses the
    /// TUN → SOCKS loop) so that tunnel-core can freely establish
    /// and rotate Psiphon server connections.
    /// </summary>
    private const string TunnelCoreExeName = "Se7enPro.Tunnel.exe";

    private const int TunMtu = 1420;

    private const string TunGatewayIpv4 = "172.18.0.1";
    private const int TunGatewayPrefixLength = 30;

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SupervisorRestartWindow = TimeSpan.FromMinutes(5);
    private const int SupervisorMaxRestartsInWindow = 5;
    private static readonly TimeSpan SupervisorMaxBackoff = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions WriteIndentedJson = new()
    {
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private readonly ILogger<SingBoxTunManager> _logger;
    private readonly IChildProcessGuard _childGuard;
    private readonly ITunnelCoreManager _tunnel;
    private readonly ISettingsService _settings;

    private readonly object _lock = new();
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;
    private Process? _process;
    private string? _workDir;
    private string? _singBoxLogPath;
    private StreamWriter? _singBoxLogWriter;

    private readonly ConcurrentQueue<string> _recentOutput = new();
    private const int RecentOutputMax = 24;

    private volatile bool _readySeen;
    private TaskCompletionSource<bool>? _readyTcs;

    /// <summary>
    /// The SOCKS port sing-box is currently configured with.
    /// Used to detect port changes across tunnel reconnects.
    /// </summary>
    private int _activeSocksPort;

    public TunState State { get; private set; } = TunState.Off;
    public string? LastError { get; private set; }

    public event EventHandler? StateChanged;

    public SingBoxTunManager(
        ILogger<SingBoxTunManager> logger,
        ITunnelCoreManager tunnel,
        ISettingsService settings,
        IChildProcessGuard childGuard)
    {
        _logger = logger;
        _tunnel = tunnel;
        _settings = settings;
        _childGuard = childGuard;

        _tunnel.StateChanged += OnTunnelStateChanged;
        _settings.SettingsChanged += OnSettingsChanged;
    }

    private async void OnTunnelStateChanged(object? sender, ConnectionState s)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnTunnelStateChanged failed"); }
    }

    private async void OnSettingsChanged(object? sender, EventArgs e)
    {
        try { await ReconcileAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "OnSettingsChanged failed"); }
    }

    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync();
        try
        {
            var systemWide = _settings.Settings.SystemWideTunneling;
            var tunnelState = _tunnel.State;
            var socksPort = _tunnel.SocksProxyPort;
            var have = State is TunState.Starting or TunState.Running;

            var wantStart = systemWide
                && tunnelState == ConnectionState.Connected
                && socksPort > 0;

            var wantKeep = systemWide
                && tunnelState is ConnectionState.Connected
                                or ConnectionState.Connecting
                && socksPort > 0;

            WriteDiag($"reconcile: systemWide={systemWide} tunnel={tunnelState} "
                    + $"socks={socksPort} wantStart={wantStart} wantKeep={wantKeep} "
                    + $"have={have} activeSocks={_activeSocksPort} state={State}");

            if (wantStart && !have)
            {
                StartSupervisor(socksPort);
            }
            else if (wantStart && have && _activeSocksPort != socksPort)
            {
                WriteDiag($"SOCKS port changed {_activeSocksPort} → {socksPort}; restarting sing-box");
                await StopSupervisorAsync();
                StartSupervisor(socksPort);
            }
            else if (!wantKeep && have)
            {
                await StopSupervisorAsync();
            }
            else if (!wantKeep && State == TunState.Error)
            {
                SetState(TunState.Off, error: null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SingBoxTunManager.ReconcileAsync failed");
            SetError(ex.Message);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void StartSupervisor(int socksPort)
    {
        if (!IsAdministrator())
        {
            SetError("System-wide tunneling needs Administrator privileges. "
                   + "Click the toggle again and choose \"Restart as Administrator\".");
            return;
        }

        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _supervisorCts?.Cancel();
            _supervisorCts = cts;
        }

        _activeSocksPort = socksPort;
        SetState(TunState.Starting, error: null);
        _supervisorTask = Task.Run(() => SuperviseAsync(socksPort, cts.Token));
    }

    private async Task StopSupervisorAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _supervisorCts;
            task = _supervisorTask;
            _supervisorCts = null;
            _supervisorTask = null;
        }

        _activeSocksPort = 0;
        SetState(TunState.Stopping, error: null);

        try { cts?.Cancel(); } catch { }

        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { WriteDiag("supervisor did not exit within 10s after cancel"); }
            catch { }
        }

        await KillProcessAsync();

        cts?.Dispose();
        SetState(TunState.Off, error: null);
    }

    private async Task SuperviseAsync(int socksPort, CancellationToken ct)
    {
        var restarts = 0;
        var windowStart = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (DateTime.UtcNow - windowStart > SupervisorRestartWindow)
            {
                restarts = 0;
                windowStart = DateTime.UtcNow;
            }

            if (restarts >= SupervisorMaxRestartsInWindow)
            {
                SetError($"sing-box keeps exiting (≥{SupervisorMaxRestartsInWindow} times "
                       + $"in {SupervisorRestartWindow.TotalMinutes:0} min). "
                       + $"Last output: {DescribeRecentOutput()}. "
                       + $"Full log: {_singBoxLogPath}");
                return;
            }

            var success = await StartSingBoxAndWaitForReadyAsync(socksPort, ct);
            if (ct.IsCancellationRequested) return;

            if (success)
            {
                SetState(TunState.Running, error: null);
                var startedAt = DateTime.UtcNow;
                await WaitForProcessExitAsync(ct);
                if (ct.IsCancellationRequested) return;

                if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(1))
                {
                    restarts = 0;
                    windowStart = DateTime.UtcNow;
                }
            }

            await KillProcessAsync();

            if (ct.IsCancellationRequested) return;

            restarts++;
            var backoff = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, restarts - 1), SupervisorMaxBackoff.TotalSeconds));
            WriteDiag($"sing-box exited (restart #{restarts}); backing off {backoff.TotalSeconds:0}s before retry");
            SetState(TunState.Starting,
                error: null);

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<bool> StartSingBoxAndWaitForReadyAsync(int socksPort, CancellationToken ct)
    {
        _recentOutput.Clear();
        _readySeen = false;
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            _workDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppBrand.SafeName,
                "singbox-tun");
            Directory.CreateDirectory(_workDir);

            CleanupLegacyXrayWorkDir();

            var diagDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppBrand.SafeName, "logs");
            Directory.CreateDirectory(diagDir);
            _singBoxLogPath = Path.Combine(diagDir, "sing-box-tun.log");
            try
            {
                _singBoxLogWriter = new StreamWriter(
                    new FileStream(_singBoxLogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
                _singBoxLogWriter.WriteLine($"# sing-box TUN session {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't open sing-box log at {Path}", _singBoxLogPath);
                _singBoxLogWriter = null;
            }

            var appDir = AppContext.BaseDirectory;
            var sourceDir = Path.Combine(appDir, "Resources", "sing-box");
            if (!Directory.Exists(sourceDir))
            {
                SetError("Bundled sing-box resources not found next to the app.");
                return false;
            }

            foreach (var pair in new (string Src, string Dst)[]
            {
                ("sing-box.exe", CachedSingBoxExeName),
                ("wintun.dll",   "wintun.dll"),
            })
            {
                var src = Path.Combine(sourceDir, pair.Src);
                if (!File.Exists(src))
                {
                    SetError($"Bundled sing-box resource missing: {pair.Src}");
                    return false;
                }
                var dst = Path.Combine(_workDir, pair.Dst);
                if (!FileCacheHelper.IsCachedCopyUpToDate(src, dst))
                {
                    try { File.Copy(src, dst, overwrite: true); }
                    catch (IOException) when (File.Exists(dst)) { }
                }
            }

            await TryRemoveStaleWintunDeviceAsync();

            var configPath = Path.Combine(_workDir, "config.json");
            var configJson = BuildConfigJson(socksPort, _settings.Settings);

            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            await File.WriteAllTextAsync(configPath, configJson, utf8NoBom, ct);
            try { _singBoxLogWriter?.WriteLine("# config:\n" + configJson); } catch { }

            var exePath = Path.Combine(_workDir, CachedSingBoxExeName);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(configPath);
            psi.ArgumentList.Add("--disable-color");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += OnSingBoxOutput;
            proc.ErrorDataReceived += OnSingBoxOutput;

            try { proc.Start(); }
            catch (Exception ex)
            {
                SetError($"Failed to start sing-box.exe: {ex.Message}");
                return false;
            }

            _childGuard.Adopt(proc);
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock) _process = proc;

            var readyTask = _readyTcs.Task;
            var exitedTask = proc.WaitForExitAsync(CancellationToken.None);
            var timeoutTask = Task.Delay(StartupTimeout, ct);

            var winner = await Task.WhenAny(readyTask, exitedTask, timeoutTask);

            if (winner == exitedTask)
            {
                SetError($"sing-box.exe exited (code={proc.ExitCode}) before TUN came up. "
                       + $"Last output: {DescribeRecentOutput()}. "
                       + $"Full log: {_singBoxLogPath}");
                return false;
            }

            if (winner == timeoutTask && !_readySeen)
            {
                SetError($"sing-box.exe didn't signal readiness within {StartupTimeout.TotalSeconds:0}s. "
                       + $"Last output: {DescribeRecentOutput()}. "
                       + $"Full log: {_singBoxLogPath}");
                return false;
            }

            WriteDiag("sing-box reported started; TUN is up");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetError($"sing-box startup failed: {ex.Message}");
            return false;
        }
    }

    private async Task WaitForProcessExitAsync(CancellationToken ct)
    {
        Process? proc;
        lock (_lock) proc = _process;
        if (proc is null) return;

        try
        {
            await proc.WaitForExitAsync(ct);
            WriteDiag($"sing-box.exe exited (code={proc.ExitCode}) while supervisor was watching");
        }
        catch (OperationCanceledException) { }
    }

    private async Task KillProcessAsync()
    {
        Process? proc;
        StreamWriter? writer;
        lock (_lock)
        {
            proc = _process;
            writer = _singBoxLogWriter;
            _process = null;
            _singBoxLogWriter = null;
        }

        if (proc is not null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await proc.WaitForExitAsync(timeout.Token); } catch { }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "sing-box kill failed"); }
            finally { proc.Dispose(); }
        }

        try
        {
            if (!string.IsNullOrEmpty(_workDir) && Directory.Exists(_workDir))
            {
                Directory.Delete(_workDir, recursive: true);
            }
        }
        catch { }

        try { writer?.Dispose(); } catch { }
    }

    private void OnSingBoxOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        _logger.LogInformation("[sing-box] {Line}", e.Data);
        NoteMilestone(e.Data);
        RememberOutputLine(e.Data);
    }

    private void NoteMilestone(string line)
    {
        if (_readySeen) return;

        if (line.IndexOf("sing-box started", StringComparison.OrdinalIgnoreCase) >= 0
         || line.IndexOf("started (", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _readySeen = true;
            _readyTcs?.TrySetResult(true);
        }
    }

    private void RememberOutputLine(string line)
    {
        _recentOutput.Enqueue(line);
        while (_recentOutput.Count > RecentOutputMax && _recentOutput.TryDequeue(out _)) { }
        try { _singBoxLogWriter?.WriteLine(line); } catch { }
    }

    private string DescribeRecentOutput()
    {
        var lines = _recentOutput.ToArray();
        return lines.Length == 0 ? "(no output)" : string.Join(" | ", lines);
    }

    private void WriteDiag(string line)
    {
        try
        {
            _singBoxLogWriter?.WriteLine($"[diag {DateTime.Now:HH:mm:ss.fff}] {line}");
        }
        catch { }
    }

    private static bool IsAdministrator()
    {
        try
        {
            using var ident = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(ident).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private void CleanupLegacyXrayWorkDir()
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                Path.Combine(localAppData, AppBrand.SafeName, "xray-tun"),
                Path.Combine(localAppData, "Psiphon", "xray-tun"),
            };
            foreach (var legacy in roots)
            {
                if (Directory.Exists(legacy))
                {
                    Directory.Delete(legacy, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "couldn't remove legacy xray-tun work dir");
        }
    }

    private async Task TryRemoveStaleWintunDeviceAsync()
    {
        try
        {
            var guid = ComputeWintunDeviceGuid(TunInterfaceName);
            var instanceId = $"SWD\\Wintun\\{{{guid:D}}}";
            var pnpUtil = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "pnputil.exe");
            if (!File.Exists(pnpUtil))
            {
                WriteDiag($"pnputil not found at {pnpUtil}; skipping pre-cleanup");
                return;
            }
            WriteDiag($"pnputil pre-cleanup: removing instance '{instanceId}' if present");

            var psi = new ProcessStartInfo
            {
                FileName = pnpUtil,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/remove-device");
            psi.ArgumentList.Add(instanceId);

            using var p = Process.Start(psi);
            if (p is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                WriteDiag("pnputil timed out; killing");
                try { p.Kill(entireProcessTree: true); } catch { }
            }

            var stdout = await p.StandardOutput.ReadToEndAsync();
            var stderr = await p.StandardError.ReadToEndAsync();
            WriteDiag($"pnputil exit={p.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}");
        }
        catch (Exception ex)
        {
            WriteDiag($"pnputil spawn failed (continuing): {ex.Message}");
        }
    }

    private static Guid ComputeWintunDeviceGuid(string adapterName)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(adapterName));
        return new Guid(md5);
    }

    internal static string BuildConfigJson(int psiphonSocksPort, UserSettings settings)
    {
        _ = settings;

        var cfg = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = "info",
                ["timestamp"] = true,
            },

            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject
                    {
                        ["tag"] = "remote-dns",
                        ["type"] = "tcp",
                        ["server"] = "1.1.1.1",
                        ["server_port"] = 53,
                        ["detour"] = "psiphon",
                    },
                    new JsonObject
                    {
                        ["tag"] = "remote-dns-fallback",
                        ["type"] = "tcp",
                        ["server"] = "8.8.8.8",
                        ["server_port"] = 53,
                        ["detour"] = "psiphon",
                    }),
                ["final"] = "remote-dns",
                ["strategy"] = "ipv4_only",
                ["disable_cache"] = false,
            },

            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = TunInterfaceName,
                    ["address"] = new JsonArray($"{TunGatewayIpv4}/{TunGatewayPrefixLength}"),
                    ["mtu"] = TunMtu,
                    ["auto_route"] = true,
                    ["strict_route"] = false,
                    ["stack"] = "system",
                    ["endpoint_independent_nat"] = false,
                }),

            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "socks",
                    ["tag"] = "psiphon",
                    ["server"] = "127.0.0.1",
                    ["server_port"] = psiphonSocksPort,
                    ["version"] = "5",
                    ["network"] = "tcp",
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),

            ["route"] = new JsonObject
            {
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "remote-dns",
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject
                    {
                        ["action"] = "reject",
                        ["ip_cidr"] = new JsonArray($"{TunGatewayIpv4}/{TunGatewayPrefixLength}"),
                    },
                    new JsonObject
                    {
                        ["action"] = "reject",
                        ["process_name"] = new JsonArray(CachedSingBoxExeName, "sing-box.exe"),
                    },
                    new JsonObject
                    {
                        ["action"] = "route",
                        ["process_name"] = new JsonArray(TunnelCoreExeName),
                        ["outbound"] = "direct",
                    },
                    new JsonObject { ["action"] = "hijack-dns", ["protocol"] = "dns" },
                    new JsonObject
                    {
                        ["action"] = "hijack-dns",
                        ["network"] = "udp",
                        ["port"] = 53,
                    },
                    new JsonObject
                    {
                        ["action"] = "route",
                        ["ip_is_private"] = true,
                        ["outbound"] = "direct",
                    },
                    new JsonObject
                    {
                        ["action"] = "reject",
                        ["network"] = "udp",
                    }),
                ["final"] = "psiphon",
            },
        };

        return cfg.ToJsonString(WriteIndentedJson);
    }

    private void SetState(TunState s, string? error)
    {
        if (State == s && LastError == error) return;
        State = s;
        LastError = error;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message)
    {
        _logger.LogWarning("SingBoxTunManager: {Message}", message);
        SetState(TunState.Error, message);
    }

    public async ValueTask DisposeAsync()
    {
        _tunnel.StateChanged -= OnTunnelStateChanged;
        _settings.SettingsChanged -= OnSettingsChanged;

        CancellationTokenSource? cts;
        Task? task;
        lock (_lock)
        {
            cts = _supervisorCts;
            task = _supervisorTask;
            _supervisorCts = null;
            _supervisorTask = null;
        }

        try { cts?.Cancel(); } catch { }
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
        await KillProcessAsync();
        cts?.Dispose();
        _reconcileGate.Dispose();
    }
}
