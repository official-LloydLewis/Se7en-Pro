from __future__ import annotations

import sys
from pathlib import Path

from PySide6.QtCore import Qt, QTimer
from PySide6.QtGui import QAction, QIcon
from PySide6.QtWidgets import (
    QApplication,
    QCheckBox,
    QComboBox,
    QHBoxLayout,
    QLabel,
    QMainWindow,
    QMessageBox,
    QPushButton,
    QSystemTrayIcon,
    QTabWidget,
    QTextEdit,
    QVBoxLayout,
    QWidget,
)

from .brand import APP_NAME
from .localization import tr
from .models import ConnectionState
from .paths import asset_dir
from .settings import SettingsStore
from .system_proxy import SystemProxyService
from .tunnel import TunnelCoreManager


class MainWindow(QMainWindow):
    def __init__(self) -> None:
        super().__init__()
        self.store = SettingsStore()
        self.settings = self.store.load()
        self.proxy = SystemProxyService()
        self.proxy.restore_if_crashed()
        self.tunnel = TunnelCoreManager(self.settings, self.proxy)
        self.tunnel.on_state.append(self._on_state)
        self.tunnel.on_log.append(self._append_log)

        self.setWindowTitle(APP_NAME)
        icon_path = asset_dir() / "app.ico"
        if icon_path.exists():
            self.setWindowIcon(QIcon(str(icon_path)))
        self.resize(920, 640)

        self.tabs = QTabWidget()
        self.setCentralWidget(self.tabs)
        self.home_tab = QWidget()
        self.settings_tab = QWidget()
        self.logs_tab = QWidget()
        self.tabs.addTab(self.home_tab, "Home")
        self.tabs.addTab(self.settings_tab, tr(self.settings.language, "settings"))
        self.tabs.addTab(self.logs_tab, tr(self.settings.language, "logs"))

        self.status_label = QLabel()
        self.detail_label = QLabel()
        self.toggle_button = QPushButton()
        self.toggle_button.clicked.connect(self._toggle_connection)
        home_layout = QVBoxLayout(self.home_tab)
        home_layout.addWidget(self.status_label)
        home_layout.addWidget(self.detail_label)
        home_layout.addWidget(self.toggle_button)
        home_layout.addStretch(1)

        self.language_combo = QComboBox()
        self.language_combo.addItem("English", "en")
        self.language_combo.addItem("فارسی", "fa")
        self.language_combo.setCurrentIndex(1 if self.settings.language == "fa" else 0)
        self.language_combo.currentIndexChanged.connect(self._language_changed)
        self.system_proxy_check = QCheckBox(tr(self.settings.language, "system_proxy"))
        self.system_proxy_check.setChecked(self.settings.set_system_proxy)
        self.system_proxy_check.toggled.connect(self._system_proxy_changed)
        settings_layout = QVBoxLayout(self.settings_tab)
        row = QHBoxLayout()
        row.addWidget(QLabel(tr(self.settings.language, "language")))
        row.addWidget(self.language_combo)
        settings_layout.addLayout(row)
        settings_layout.addWidget(self.system_proxy_check)
        settings_layout.addStretch(1)

        self.log_box = QTextEdit()
        self.log_box.setReadOnly(True)
        logs_layout = QVBoxLayout(self.logs_tab)
        logs_layout.addWidget(self.log_box)

        self.tray = QSystemTrayIcon(self.windowIcon(), self)
        self.show_action = QAction("Show", self)
        self.show_action.triggered.connect(self.showNormal)
        self.exit_action = QAction("Exit", self)
        self.exit_action.triggered.connect(self._exit_from_tray)
        self.tray_menu = self.menuBar().addMenu("Tray")
        self.tray_menu.addAction(self.show_action)
        self.tray_menu.addAction(self.exit_action)
        self.tray.setToolTip(APP_NAME)
        self.tray.activated.connect(lambda reason: self.showNormal() if reason == QSystemTrayIcon.Trigger else None)
        self.tray.show()

        self._apply_language()
        self._on_state(self.tunnel.state)
        if self.settings.auto_connect:
            QTimer.singleShot(300, self.tunnel.start)

    def closeEvent(self, event) -> None:  # type: ignore[override]
        if self.settings.minimize_to_tray and self.settings.on_close_action != "exit":
            event.ignore()
            self.hide()
            self.tray.showMessage(APP_NAME, "Still running in the system tray.")
            return
        self._shutdown()
        event.accept()

    def _toggle_connection(self) -> None:
        if self.tunnel.state in (ConnectionState.CONNECTED, ConnectionState.CONNECTING):
            self.tunnel.stop()
        else:
            self.tunnel.start()

    def _on_state(self, state: ConnectionState) -> None:
        if state == ConnectionState.CONNECTED:
            self.status_label.setText(tr(self.settings.language, "connected"))
            self.detail_label.setText(f"HTTP 127.0.0.1:{self.tunnel.http_port} • SOCKS 127.0.0.1:{self.tunnel.socks_port}")
            self.toggle_button.setText(tr(self.settings.language, "disconnect"))
        elif state == ConnectionState.CONNECTING:
            self.status_label.setText(tr(self.settings.language, "connecting"))
            self.detail_label.setText("Starting tunnel engine and selecting route")
            self.toggle_button.setText(tr(self.settings.language, "disconnect"))
        else:
            self.status_label.setText(tr(self.settings.language, "disconnected"))
            self.detail_label.setText("")
            self.toggle_button.setText(tr(self.settings.language, "connect"))

    def _append_log(self, line: str) -> None:
        self.log_box.append(line)

    def _language_changed(self) -> None:
        self.settings.language = str(self.language_combo.currentData())
        self.store.save()
        self._apply_language()
        self._on_state(self.tunnel.state)

    def _system_proxy_changed(self, enabled: bool) -> None:
        self.settings.set_system_proxy = enabled
        self.store.save()
        if self.tunnel.state == ConnectionState.CONNECTED:
            if enabled and self.tunnel.http_port > 0:
                self.proxy.set(self.tunnel.http_port)
            else:
                self.proxy.clear()

    def _apply_language(self) -> None:
        rtl = self.settings.language == "fa"
        self.setLayoutDirection(Qt.RightToLeft if rtl else Qt.LeftToRight)
        self.tabs.setTabText(1, tr(self.settings.language, "settings"))
        self.tabs.setTabText(2, tr(self.settings.language, "logs"))
        self.system_proxy_check.setText(tr(self.settings.language, "system_proxy"))

    def _exit_from_tray(self) -> None:
        self._shutdown()
        QApplication.quit()

    def _shutdown(self) -> None:
        try:
            self.tunnel.dispose()
        except Exception as exc:
            QMessageBox.warning(self, APP_NAME, str(exc))
        try:
            self.proxy.clear()
        except Exception:
            pass


def main() -> int:
    app = QApplication(sys.argv)
    app.setApplicationName(APP_NAME)
    window = MainWindow()
    window.show()
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
