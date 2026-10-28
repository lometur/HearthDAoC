"""The starter guilds, taken out of the world (owner 2026-10-28, #176: "bots will autoquit or never join the beginner
guilds. in fact we can do away with them if that's easy").

Upstream's server puts each new character in its realm's starter guild (Clan Cotswold, Mularn Protectors, Tir na Nog
Adventurers: the Guild rows with IsStartingGuild set), which StartupGuilds.cs makes at every start while its
starting_guild property is on. server_properties.py writes that property False before every start, so the server makes
none and puts no one in one; this fix removes the ones a world already has:
- every character in one (DOLCharacters) leaves it as /gc quit leaves a guild (Guild.RemovePlayer): GuildID and
  GuildNote emptied, the rank left as it is;
- every gamebot membership of one (offline_world_bot_guild) goes, as the server drops a membership whose guild is gone;
- their GuildRank rows and their Guild rows go.
The characters' backups (DOLCharactersBackup) keep their rows: a restored character whose guild is gone loads with
none (GamePlayer's HandleGuild).

Like mob_fixes.py it has no marker: at every start it removes the starter guilds there are, so a world with none
(after the first start, or if the owner turned the property back on and off) has nothing to do. world_fixes.py calls
apply() inside its transaction, under its own savepoint: if it fails it is rolled back alone, apply() returns only the
"not removed" line, and the other fixes and the start go on.
"""
import datetime

SAVEPOINT = "starter_guilds"
NOT_REMOVED = "Starter guilds: not removed ({}); they stay as they are"
BOT_TABLE = "offline_world_bot_guild"  # AutonomousBotGuilds' OfflineWorldBotGuildRecord


def _columns(conn, table):
    return {name.lower() for _, name, *_ in conn.execute(f'PRAGMA table_info("{table}")')}


def apply(conn, now=None):
    """Remove the starter guilds and take their members out; returns the summary line, the "not removed" line, or
    nothing. Inside an open transaction (world_fixes.py's), nothing here commits."""
    if not {"guildid", "guildname", "isstartingguild"} <= _columns(conn, "Guild"):
        return []  # no Guild table, or not upstream's
    now = now or _now()
    conn.execute(f"SAVEPOINT {SAVEPOINT}")
    try:
        guilds = conn.execute("SELECT GuildID, GuildName FROM Guild WHERE IsStartingGuild <> 0 "
                              "ORDER BY Realm, GuildName").fetchall()
        characters = bots = 0
        ids = [guild_id for guild_id, _ in guilds if guild_id]
        if ids:
            marks = ", ".join("?" * len(ids))
            have = _columns(conn, "DOLCharacters")
            if "guildid" in have:
                sets = ["GuildID=''"] + (["GuildNote=''"] if "guildnote" in have else [])
                stamp = [now] if "lasttimerowupdated" in have else []
                sets += ["LastTimeRowUpdated=?"] * len(stamp)
                characters = conn.execute(f"UPDATE DOLCharacters SET {', '.join(sets)} WHERE GuildID IN ({marks})",
                                          (*stamp, *ids)).rowcount
            if "guildid" in _columns(conn, BOT_TABLE):
                bots = conn.execute(f"DELETE FROM {BOT_TABLE} WHERE GuildID IN ({marks})", ids).rowcount
            if "guildid" in _columns(conn, "GuildRank"):
                conn.execute(f"DELETE FROM GuildRank WHERE GuildID IN ({marks})", ids)
        conn.execute("DELETE FROM Guild WHERE IsStartingGuild <> 0")
    except Exception as e:  # any failure: undo this fix only, and let the other fixes and the start go on
        conn.execute(f"ROLLBACK TO {SAVEPOINT}")
        conn.execute(f"RELEASE {SAVEPOINT}")
        return [NOT_REMOVED.format(str(e) or type(e).__name__)]
    conn.execute(f"RELEASE {SAVEPOINT}")
    return summary([name for _, name in guilds], characters, bots)


def _count(n, what):
    return f"{n} {what}{'' if n == 1 else 's'}"


def summary(names, characters, bots):
    """The start log's line, or nothing when the world had no starter guild."""
    if not names:
        return []
    members = " and ".join(_count(n, what) for n, what in ((characters, "character"), (bots, "gamebot")) if n)
    left = f"{members} left them" if members else "they had no members"
    return [f"Starter guilds: removed {', '.join(names)} and their ranks; {left}"]


def _now():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S")
