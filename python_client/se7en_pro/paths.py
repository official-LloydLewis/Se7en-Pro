from __future__ import annotations

import os
import sys
from pathlib import Path

from .brand import SAFE_NAME


def app_root() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[2]


def local_app_data() -> Path:
    base = os.environ.get("LOCALAPPDATA")
    if base:
        return Path(base)
    return Path.home() / "AppData" / "Local"


def data_dir() -> Path:
    path = local_app_data() / SAFE_NAME
    path.mkdir(parents=True, exist_ok=True)
    return path


def legacy_data_dir() -> Path:
    return local_app_data() / "Psiphon"


def resource_dir() -> Path:
    candidates = [app_root() / "Resources", app_root().parent / "PsiphonUI" / "Resources"]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]


def asset_dir() -> Path:
    candidates = [app_root() / "Assets", app_root().parent / "PsiphonUI" / "Assets"]
    for candidate in candidates:
        if candidate.exists():
            return candidate
    return candidates[0]
