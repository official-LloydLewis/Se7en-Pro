from __future__ import annotations

STRINGS = {
    "en": {
        "connect": "Connect",
        "disconnect": "Disconnect",
        "connected": "Protected",
        "disconnected": "Ready to connect",
        "connecting": "Securing connection…",
        "settings": "Settings",
        "logs": "Logs",
        "language": "Language",
        "system_proxy": "Set system proxy",
    },
    "fa": {
        "connect": "اتصال",
        "disconnect": "قطع اتصال",
        "connected": "محافظت شده",
        "disconnected": "آماده اتصال",
        "connecting": "در حال برقراری اتصال امن…",
        "settings": "تنظیمات",
        "logs": "گزارش‌ها",
        "language": "زبان",
        "system_proxy": "تنظیم پراکسی سیستم",
    },
}


def tr(language: str, key: str) -> str:
    lang = "fa" if language == "fa" else "en"
    return STRINGS[lang].get(key, STRINGS["en"].get(key, key))
