# Se7en Pro Python Client

This directory contains a Python rewrite of the Se7en Pro Windows client shell.
It preserves the same high-level responsibilities as the WPF client:

- launch and supervise `psiphon-tunnel-core.exe`
- save/load branded settings while migrating legacy `%LOCALAPPDATA%\Psiphon` settings
- apply and restore WinINet system proxy settings with branded and legacy backup support
- prevent delayed reconnects after shutdown/dispose
- expose English/Persian language mode with right-to-left layout
- provide a simple tray-aware desktop UI

The tunnel/protocol implementation remains the external `psiphon-tunnel-core.exe` binary.

## Run from source

```powershell
cd python_client
python -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install -r requirements.txt
python -m se7en_pro
```

## Build Windows executable

```powershell
cd python_client
.\build.ps1
```

Expected artifact:

```text
python_client\dist\Se7enPro\Se7enPro.exe
```
