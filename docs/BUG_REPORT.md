# Se7en Pro stability bug report

Inspection date: 2026-06-01

| File | Issue | Severity | Fix plan / status |
|---|---|---:|---|
| `PsiphonUI/Services/TunnelCoreManager.cs` | `StartAsync`, `StopAsync`, auto-connect, tray actions, and retry tasks could overlap because lifecycle operations were not serialized. Concurrent starts could race before `_process` was assigned and could leave duplicate tunnel-core processes. | High | Added a lifecycle `SemaphoreSlim`, split start/stop into guarded core methods, and kept retry/start/stop behavior serialized. |
| `PsiphonUI/Services/TunnelCoreManager.cs` | A partially started tunnel process was not reliably killed/disposed when a failure happened after `Process` creation. | High | Added failed-start cleanup that kills the process tree and disposes the `Process`. |
| `PsiphonUI/Services/TunnelCoreManager.cs` | Unexpected tunnel-core exit left the WinINet system proxy pointing at stale local ports until a later stop or successful reconnect. | High | Clear the system proxy immediately on unexpected exit before scheduling reconnect. |
| `PsiphonUI/Services/TunnelCoreManager.cs` | `OnProcessExited` read exit code through the mutable `_process` field, so races could report `-1` or inspect the wrong process. | Medium | Use the event sender process for exit-code logging and clear `_process` only when it matches the exited instance. |
| `PsiphonUI/Services/TunnelCoreManager.cs` | A delayed retry or process-exit callback could reconnect after disposal during shutdown/crash cleanup. | High | Added disposal guards so retry scheduling, delayed retry execution, and unexpected-exit reconnect paths stop after `Dispose()`. |
| `PsiphonUI/App.xaml` and `PsiphonUI/App.xaml.cs` | `StartupUri` allowed WPF to construct `MainWindow` outside the explicit DI startup sequence, which risked accessing `App.Services` before initialization. | High | Removed `StartupUri` and create/show `MainWindow` explicitly after services, settings, theme, cleanup, and event handlers are ready. |
| `PsiphonUI/App.xaml.cs` | Cleanup grouped TUN disposal, tunnel stop, and proxy clearing in one `try`; an earlier cleanup exception could skip proxy restoration on shutdown. | High | Split cleanup into independent guarded steps so system proxy `Clear()` is attempted even when other shutdown cleanup fails. |
| `PsiphonUI/App.xaml.cs` | Fatal logs were still written under the old app folder, which fragmented diagnostics after rebranding. | Medium | Moved fatal logs to the branded local app-data folder. |
| `PsiphonUI/Services/SettingsService.cs` | Rebranding the app-data folder would silently lose existing settings from the original folder. | Medium | Added legacy settings migration into the branded settings path. |
| `PsiphonUI/Services/SystemProxyService.cs` | Proxy backup file path used the old app-data folder and could miss backups after a rebrand. | High | Store new backups under the branded folder, migrate legacy backups, and delete backups only after restore operations complete. |
| `PsiphonUI/Services/StartupReaper.cs` | Startup cleanup originally only watched one app-data root. After personalization, stale helper processes from either old or new roots needed cleanup. | Medium | Reaper now scans branded and legacy process roots and removes stale locks in both tunnel-core directories. |
| `PsiphonUI/Services/StartupRegistration.cs` | Windows Run-key name used the old app identity. | Low | Switched the Run-key value to the branded safe name. |
| `PsiphonUI/Services/TrayIconService.cs` and `PsiphonUI/Views/*.xaml` | UI, tray tooltip, close dialog, and About text still showed the old product name. | Medium | Centralized brand constants and updated visible shell/tray/About/close texts. |
| `PsiphonUI/ViewModels/HomeViewModel.cs` | Connection status messages were terse and did not explain cleanup/retry states clearly. | Low | Reworded state messages to make protected, connecting, disconnecting, and error states actionable. |
| `PsiphonUI/ViewModels/SettingsViewModel.cs` | Toggling system proxy while already connected only saved the setting; it did not immediately apply or restore the WinINet proxy. | High | Apply the local HTTP proxy immediately when enabled while connected and restore the previous proxy immediately when disabled. |
| `PsiphonUI/ViewModels/SettingsViewModel.cs` and `PsiphonUI/Views/SettingsPage.xaml` | No visible language setting existed despite a persisted `Language` setting and startup culture hook. | Medium | Added English/Persian language options and Persian RTL support through the main shell flow direction. |
| `PsiphonUI/Themes/Colors.xaml`, `PsiphonUI/Themes/Styles.xaml`, and `PsiphonUI/Services/ThemeService.cs` | Dark surfaces had low separation and the font stack was not Persian-friendly. | Low | Increased dark surface contrast and added Persian-friendly font fallbacks. |
| `PsiphonUI/build.ps1` and `PsiphonUI/PsiphonUI.csproj` | Release output and metadata still used the old executable/product identity. | Medium | Updated assembly/product metadata and build helper output paths for `Se7enPro.exe`. |

## Build note

A local build was attempted as required, but this container does not include the .NET SDK and outbound package/download access is blocked by HTTP 403 responses. The repository is prepared for a Windows release build with `PsiphonUI/build.ps1 -Publish` on a Windows machine with .NET SDK 8 installed.
