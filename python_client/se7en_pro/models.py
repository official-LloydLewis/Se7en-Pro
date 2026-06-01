from __future__ import annotations

from dataclasses import asdict, dataclass
from enum import Enum


class ConnectionState(str, Enum):
    DISCONNECTED = "disconnected"
    CONNECTING = "connecting"
    CONNECTED = "connected"
    DISCONNECTING = "disconnecting"
    ERROR = "error"


@dataclass
class UserSettings:
    theme: str = "dark"
    language: str = "en"
    egress_region: str = ""
    disable_timeouts: bool = False
    local_socks_proxy_port: int = 0
    local_http_proxy_port: int = 0
    allow_lan_connections: bool = False
    set_system_proxy: bool = True
    auto_connect: bool = False
    start_with_windows: bool = False
    minimize_to_tray: bool = True
    on_close_action: str = "ask"
    upstream_proxy: str = ""
    upstream_proxy_scheme: str = "http"
    upstream_proxy_username: str = ""
    upstream_proxy_password: str = ""
    system_wide_tunneling: bool = False
    protocol_mode: str = "auto"
    beast_mode: bool = False
    cdn_fronting_custom_ip_list: str = ""
    cdn_fronting_custom_sni: str = ""
    auto_find_ip_and_sni: bool = False
    save_found_ips_and_sni: bool = False
    lan_proxy_username: str = ""
    lan_proxy_password: str = ""

    def to_json(self) -> dict[str, object]:
        return asdict(self)

    @classmethod
    def from_json(cls, payload: dict[str, object]) -> "UserSettings":
        normalised: dict[str, object] = {}
        for key, value in payload.items():
            snake = []
            for ch in key:
                if ch.isupper():
                    snake.append("_")
                    snake.append(ch.lower())
                else:
                    snake.append(ch)
            normalised["".join(snake)] = value
        fields = cls.__dataclass_fields__
        return cls(**{k: v for k, v in normalised.items() if k in fields})
