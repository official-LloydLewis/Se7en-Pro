from __future__ import annotations

import ctypes
import json
import winreg
from dataclasses import asdict, dataclass
from pathlib import Path

from .paths import data_dir, legacy_data_dir

INTERNET_SETTINGS = r"Software\Microsoft\Windows\CurrentVersion\Internet Settings"
INTERNET_OPTION_REFRESH = 37
INTERNET_OPTION_SETTINGS_CHANGED = 39


@dataclass
class ProxyBackup:
    proxyEnable: int = 0
    proxyServer: str = ""
    proxyOverride: str = ""


class SystemProxyService:
    def __init__(self) -> None:
        self.backup_path = data_dir() / "proxy-backup.json"
        self.legacy_backup_path = legacy_data_dir() / "proxy-backup.json"

    def set(self, http_port: int) -> None:
        if http_port <= 0:
            return
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, INTERNET_SETTINGS, 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            if not self._has_backup():
                self._write_backup(self._read_current(key))
            else:
                self._migrate_legacy_backup()
            winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 1)
            winreg.SetValueEx(key, "ProxyServer", 0, winreg.REG_SZ, f"127.0.0.1:{http_port}")
            winreg.SetValueEx(key, "ProxyOverride", 0, winreg.REG_SZ, "<local>")
        self._notify()

    def clear(self) -> None:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, INTERNET_SETTINGS, 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            backup = self._read_backup()
            if backup:
                self._restore(key, backup)
                self._delete_backups()
            else:
                winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, 0)
        self._notify()

    def restore_if_crashed(self) -> None:
        if self._read_backup():
            self.clear()

    def _read_current(self, key: winreg.HKEYType) -> ProxyBackup:
        return ProxyBackup(
            proxyEnable=self._query_int(key, "ProxyEnable", 0),
            proxyServer=self._query_str(key, "ProxyServer", ""),
            proxyOverride=self._query_str(key, "ProxyOverride", ""),
        )

    @staticmethod
    def _query_int(key: winreg.HKEYType, name: str, default: int) -> int:
        try:
            value, _ = winreg.QueryValueEx(key, name)
            return int(value)
        except FileNotFoundError:
            return default

    @staticmethod
    def _query_str(key: winreg.HKEYType, name: str, default: str) -> str:
        try:
            value, _ = winreg.QueryValueEx(key, name)
            return str(value)
        except FileNotFoundError:
            return default

    def _has_backup(self) -> bool:
        return self.backup_path.exists() or self.legacy_backup_path.exists()

    def _write_backup(self, backup: ProxyBackup) -> None:
        self.backup_path.parent.mkdir(parents=True, exist_ok=True)
        self.backup_path.write_text(json.dumps(asdict(backup)), encoding="utf-8")

    def _read_backup(self) -> ProxyBackup | None:
        path = self.backup_path if self.backup_path.exists() else self.legacy_backup_path
        if not path.exists():
            return None
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
            return ProxyBackup(**payload)
        except Exception:
            return None

    def _migrate_legacy_backup(self) -> None:
        if self.backup_path.exists() or not self.legacy_backup_path.exists():
            return
        self.backup_path.parent.mkdir(parents=True, exist_ok=True)
        self.backup_path.write_bytes(self.legacy_backup_path.read_bytes())

    def _restore(self, key: winreg.HKEYType, backup: ProxyBackup) -> None:
        winreg.SetValueEx(key, "ProxyEnable", 0, winreg.REG_DWORD, backup.proxyEnable)
        self._set_or_delete(key, "ProxyServer", backup.proxyServer)
        self._set_or_delete(key, "ProxyOverride", backup.proxyOverride)

    @staticmethod
    def _set_or_delete(key: winreg.HKEYType, name: str, value: str) -> None:
        if value:
            winreg.SetValueEx(key, name, 0, winreg.REG_SZ, value)
        else:
            try:
                winreg.DeleteValue(key, name)
            except FileNotFoundError:
                pass

    def _delete_backups(self) -> None:
        for path in (self.backup_path, self.legacy_backup_path):
            try:
                Path(path).unlink(missing_ok=True)
            except Exception:
                pass

    @staticmethod
    def _notify() -> None:
        wininet = ctypes.WinDLL("wininet", use_last_error=True)
        wininet.InternetSetOptionW(0, INTERNET_OPTION_SETTINGS_CHANGED, 0, 0)
        wininet.InternetSetOptionW(0, INTERNET_OPTION_REFRESH, 0, 0)
