#!/usr/bin/env python3
"""Write serverconfig.xml from HEARTHDAOC_* environment variables (see deploy/compose.yml).
UPnP and region-IP detection are always off; the database lives in the data volume."""
import argparse
import ipaddress
import os
import sys
from xml.sax.saxutils import escape

DEFAULTS = {
    "HEARTHDAOC_LISTEN_IP": "0.0.0.0",
    "HEARTHDAOC_PORT": "10301",
    "HEARTHDAOC_UDP_PORT": "10401",
    "HEARTHDAOC_AUTO_ACCOUNTS": "True",
    "HEARTHDAOC_SERVER_NAME": "HearthDAoC",
    "HEARTHDAOC_AUTOSAVE_MINUTES": "5",
    "HEARTHDAOC_BACKUP_KEEP": "7",
}


class ConfigError(Exception):
    pass


def settings(env):
    s = {key: (env.get(key) or default) for key, default in DEFAULTS.items()}
    try:
        ipaddress.IPv4Address(s["HEARTHDAOC_LISTEN_IP"])
    except ValueError:
        raise ConfigError(f"HEARTHDAOC_LISTEN_IP must be an IPv4 address, got {s['HEARTHDAOC_LISTEN_IP']!r}")
    for key in ("HEARTHDAOC_PORT", "HEARTHDAOC_UDP_PORT"):
        if not s[key].isdigit() or not 1 <= int(s[key]) <= 65535:
            raise ConfigError(f"{key} must be a port number from 1 to 65535, got {s[key]!r}")
    auto = s["HEARTHDAOC_AUTO_ACCOUNTS"].strip().lower()
    if auto not in ("true", "false"):
        raise ConfigError(f"HEARTHDAOC_AUTO_ACCOUNTS must be True or False, got {s['HEARTHDAOC_AUTO_ACCOUNTS']!r}")
    s["HEARTHDAOC_AUTO_ACCOUNTS"] = "True" if auto == "true" else "False"
    if not s["HEARTHDAOC_SERVER_NAME"].strip():
        raise ConfigError("HEARTHDAOC_SERVER_NAME must not be empty")
    whole_number(s, "HEARTHDAOC_AUTOSAVE_MINUTES", 1, 60, "minutes")
    # Checked here so a bad value stops the start instead of silently ending the daily backups.
    whole_number(s, "HEARTHDAOC_BACKUP_KEEP", 1, 365, "backups")
    return s


def whole_number(s, key, low, high, unit):
    value = s[key].strip()
    if not value.isdigit() or not low <= int(value) <= high:
        raise ConfigError(f"{key} must be a whole number of {unit} from {low} to {high}, got {s[key]!r}")
    s[key] = str(int(value))


def render(s, data):
    ip = s["HEARTHDAOC_LISTEN_IP"]
    db = escape(os.path.join(data, "world", "opendaoc.sqlite3.db"))
    name = escape(s["HEARTHDAOC_SERVER_NAME"])
    # BusyTimeout (ms) turns on SQLite's own busy handler. Without it System.Data.SQLite gives up a new
    # connection after 3 quick retries, and the brief lock taken when the last pooled connection closes
    # (e.g. after a GC) crashed start-up with "database is locked".
    return f"""<?xml version="1.0" encoding="utf-8"?>
<!-- Generated at container start by /app/bin/gen_config.py. Change deploy/.env instead of this file. -->
<root>
    <Server>
        <Port>{s['HEARTHDAOC_PORT']}</Port>
        <IP>{ip}</IP>
        <RegionIP>{ip}</RegionIP>
        <RegionPort>{s['HEARTHDAOC_UDP_PORT']}</RegionPort>
        <UdpIP>{ip}</UdpIP>
        <UdpPort>{s['HEARTHDAOC_UDP_PORT']}</UdpPort>
        <EnableUPnP>False</EnableUPnP>
        <DetectRegionIP>False</DetectRegionIP>
        <ServerName>{name}</ServerName>
        <ServerNameShort>HearthDAoC</ServerNameShort>
        <LogConfigFile>./config/logconfig.xml</LogConfigFile>
        <ScriptCompilationTarget>./lib/GameServerScripts.dll</ScriptCompilationTarget>
        <ScriptAssemblies> </ScriptAssemblies>
        <EnableCompilation>True</EnableCompilation>
        <AutoAccountCreation>{s['HEARTHDAOC_AUTO_ACCOUNTS']}</AutoAccountCreation>
        <GameType>Normal</GameType>
        <CheatLoggerName>cheats</CheatLoggerName>
        <GMActionLoggerName>gmactions</GMActionLoggerName>
        <InvalidNamesFile>./config/invalidnames.txt</InvalidNamesFile>
        <DBType>SQLITE</DBType>
        <DBConnectionString>Data Source={db};Version=3;Pooling=True;Journal Mode=WAL;Synchronous=Normal;Foreign Keys=True;BusyTimeout=10000;Default Timeout=60</DBConnectionString>
        <DBAutosave>True</DBAutosave>
        <DBAutosaveInterval>{s['HEARTHDAOC_AUTOSAVE_MINUTES']}</DBAutosaveInterval>
        <MetricsEnabled>false</MetricsEnabled>
        <MetricsInterval>60s</MetricsInterval>
        <OtlpEndpoint>http://127.0.0.1:4317</OtlpEndpoint>
    </Server>
</root>
"""


def main(argv=None):
    ap = argparse.ArgumentParser(description="Generate the server's serverconfig.xml from environment variables.")
    ap.add_argument("--data", required=True)
    ap.add_argument("--out", required=True)
    a = ap.parse_args(argv)
    try:
        xml = render(settings(os.environ), a.data)
    except ConfigError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2
    tmp = a.out + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(xml)
    os.replace(tmp, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
