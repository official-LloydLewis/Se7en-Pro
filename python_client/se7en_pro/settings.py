from __future__ import annotations

import json
from pathlib import Path

from .models import UserSettings
from .paths import data_dir, legacy_data_dir


class SettingsStore:
    def __init__(self) -> None:
        self.path = data_dir() / "settings.json"
        self.legacy_path = legacy_data_dir() / "settings.json"
        self.settings = UserSettings()

    def load(self) -> UserSettings:
        path = self.path if self.path.exists() else self.legacy_path
        if not path.exists():
            self.save()
            return self.settings
        try:
            payload = json.loads(path.read_text(encoding="utf-8"))
            if isinstance(payload, dict):
                self.settings = UserSettings.from_json(payload)
                if path == self.legacy_path:
                    self.save()
        except Exception:
            self.settings = UserSettings()
        return self.settings

    def save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self.settings.to_json(), indent=2), encoding="utf-8")
