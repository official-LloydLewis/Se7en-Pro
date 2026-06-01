from __future__ import annotations

import json
import os
import re
import subprocess
import threading
import time
from pathlib import Path
from typing import Callable

from .models import ConnectionState, UserSettings
from .paths import data_dir, resource_dir
from .system_proxy import SystemProxyService

NoticeHandler = Callable[[dict[str, object]], None]
StateHandler = Callable[[ConnectionState], None]
LogHandler = Callable[[str], None]


class TunnelCoreManager:
    def __init__(self, settings: UserSettings, system_proxy: SystemProxyService) -> None:
        self.settings = settings
        self.system_proxy = system_proxy
        self.state = ConnectionState.DISCONNECTED
        self.http_port = 0
        self.socks_port = 0
        self._process: subprocess.Popen[str] | None = None
        self._lock = threading.RLock()
        self._disposed = False
        self._user_wants_connection = False
        self._retry_timer: threading.Timer | None = None
        self.on_state: list[StateHandler] = []
        self.on_log: list[LogHandler] = []
        self.on_notice: list[NoticeHandler] = []

    def start(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._user_wants_connection = True
            self._cancel_retry_locked()
            if self._process and self._process.poll() is None:
                return
            self._set_state(ConnectionState.CONNECTING)
            self._log("Starting secure tunnel...")
            try:
                work_dir = data_dir() / "tunnel-core"
                work_dir.mkdir(parents=True, exist_ok=True)
                config_path = work_dir / "config.json"
                config_path.write_text(json.dumps(self._build_config(), indent=2), encoding="utf-8")
                exe_path = self._resolve_tunnel_exe(work_dir)
                args = [str(exe_path), "--config", str(config_path)]
                server_list = self._server_list_path(work_dir)
                if server_list:
                    args += ["--serverList", str(server_list)]
                self._process = subprocess.Popen(
                    args,
                    cwd=work_dir,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
                )
            except Exception as exc:
                self.system_proxy.clear()
                self._log(f"Could not start the tunnel engine: {exc}")
                self._schedule_retry_locked(5)
                return
            threading.Thread(target=self._read_stream, args=(self._process.stdout, False), daemon=True).start()
            threading.Thread(target=self._read_stream, args=(self._process.stderr, True), daemon=True).start()
            threading.Thread(target=self._wait_for_exit, args=(self._process,), daemon=True).start()

    def stop(self) -> None:
        with self._lock:
            self._user_wants_connection = False
            self._cancel_retry_locked()
            process = self._process
            if not process or process.poll() is not None:
                self._process = None
                self.system_proxy.clear()
                self._set_state(ConnectionState.DISCONNECTED)
                return
            self._set_state(ConnectionState.DISCONNECTING)
            self._log("Stopping tunnel and restoring proxy settings...")
            try:
                if process.stdin:
                    process.stdin.close()
            except Exception:
                pass
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
        finally:
            with self._lock:
                if self._process is process:
                    self._process = None
                self.system_proxy.clear()
                self._set_state(ConnectionState.DISCONNECTED)

    def dispose(self) -> None:
        with self._lock:
            self._disposed = True
            self._user_wants_connection = False
            self._cancel_retry_locked()
            process = self._process
            self._process = None
        if process and process.poll() is None:
            try:
                process.kill()
            except Exception:
                pass
        self.system_proxy.clear()

    def _wait_for_exit(self, process: subprocess.Popen[str]) -> None:
        code = process.wait()
        with self._lock:
            if self._process is process:
                self._process = None
            self.system_proxy.clear()
            if self._disposed:
                self._set_state(ConnectionState.DISCONNECTED)
                return
            if self.state == ConnectionState.DISCONNECTING:
                return
            self._log(f"Tunnel engine exited ({code}); reconnecting...")
            if self._user_wants_connection:
                self._set_state(ConnectionState.CONNECTING)
                self._schedule_retry_locked(3)
            else:
                self._set_state(ConnectionState.DISCONNECTED)

    def _read_stream(self, stream: object, stderr: bool) -> None:
        if stream is None:
            return
        for line in stream:  # type: ignore[operator]
            text = str(line).strip()
            if not text:
                continue
            if not self._try_notice(text) and stderr:
                self._log(text)

    def _try_notice(self, line: str) -> bool:
        try:
            notice = json.loads(line)
        except json.JSONDecodeError:
            return False
        if not isinstance(notice, dict):
            return False
        for callback in self.on_notice:
            callback(notice)
        notice_type = str(notice.get("noticeType") or notice.get("NoticeType") or "")
        data = notice.get("data") if isinstance(notice.get("data"), dict) else notice
        if notice_type == "ListeningHttpProxyPort":
            self.http_port = int(data.get("port", 0))
        elif notice_type == "ListeningSocksProxyPort":
            self.socks_port = int(data.get("port", 0))
        elif notice_type == "Tunnels" and int(data.get("count", 0)) > 0:
            self._set_state(ConnectionState.CONNECTED)
            if self.settings.set_system_proxy and self.http_port > 0:
                self.system_proxy.set(self.http_port)
        return True

    def _schedule_retry_locked(self, delay: int) -> None:
        if self._disposed:
            return
        self._cancel_retry_locked()
        self._retry_timer = threading.Timer(delay, self.start)
        self._retry_timer.daemon = True
        self._retry_timer.start()

    def _cancel_retry_locked(self) -> None:
        if self._retry_timer:
            self._retry_timer.cancel()
            self._retry_timer = None

    def _set_state(self, state: ConnectionState) -> None:
        if self.state == state:
            return
        self.state = state
        for callback in self.on_state:
            callback(state)

    def _log(self, line: str) -> None:
        stamp = time.strftime("%H:%M:%S")
        for callback in self.on_log:
            callback(f"{stamp} {line}")

    def _resolve_tunnel_exe(self, work_dir: Path) -> Path:
        source = resource_dir() / "psiphon-tunnel-core.exe"
        if not source.exists():
            source = Path(os.getcwd()) / "psiphon-tunnel-core.exe"
        if not source.exists():
            raise FileNotFoundError("psiphon-tunnel-core.exe not found")
        cached = work_dir / "Se7enPro.Tunnel.exe"
        if not cached.exists() or source.stat().st_mtime > cached.stat().st_mtime:
            cached.write_bytes(source.read_bytes())
        return cached

    def _server_list_path(self, work_dir: Path) -> Path | None:
        plain = resource_dir() / "server_entries.txt"
        if not plain.exists():
            return None
        target = work_dir / "server_entries.txt"
        target.write_bytes(plain.read_bytes())
        return target

    def _build_config(self) -> dict[str, object]:
        config: dict[str, object] = {
            "LocalHttpProxyPort": self.settings.local_http_proxy_port,
            "LocalSocksProxyPort": self.settings.local_socks_proxy_port,
            "EmitBytesTransferred": True,
        }
        if self.settings.egress_region:
            config["EgressRegion"] = self.settings.egress_region
        if self.settings.disable_timeouts:
            config["DisableTimeouts"] = True
        if self.settings.upstream_proxy:
            config["UpstreamProxyUrl"] = self.settings.upstream_proxy
        if self.settings.protocol_mode == "direct":
            config["DisableTactics"] = True
        if self.settings.protocol_mode == "cdn_fronting":
            ips = re.split(r"[\s,;]+", self.settings.cdn_fronting_custom_ip_list.strip())
            config["FrontedMeekDialOverrides"] = [{"DialAddress": ip} for ip in ips if ip]
        return config
