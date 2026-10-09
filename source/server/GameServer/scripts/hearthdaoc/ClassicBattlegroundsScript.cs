using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DOL.Database;
using DOL.Events;
using DOL.GS.Keeps;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the game wiring of the classic battlegrounds. The frontier porter (OFTeleporter) asks
// PorterDestination where a character wearing the battlegrounds medallion goes, and the realm teleporters'
// [Battlegrounds] choice asks RealmRankRefusal; the gamebots ask BotFits, PartyFits, BotOverCap,
// PartyOverCap, BotFitsItsBattleground and RecordFitsItsBattleground before a battleground goal or trip;
// a character over its battleground's limit is moved out at logout and, after a link death or a crash, a
// moment after its next login; and a captured central keep goes back to level 1. ClassicBattlegrounds
// makes every decision; this class reads the battleground rows and the character's state and carries out
// the outcome.
public static class ClassicBattlegroundsScript
{
    private static readonly Logger Log = LoggerManager.Create(typeof(ClassicBattlegroundsScript));

    // Called by OFTeleporter's CastTimerCallback for a player wearing battlegrounds_necklace. Returns the
    // destination (the caller removes the medallion and moves the player), or null (the medallion stays).
    // The callback runs twice per ceremony, so a refusal is said only when ShouldSayRefusal allows it, and
    // the time is stored only when it is said.
    public static GameLocation PorterDestination(GameNPC porter, GamePlayer player)
    {
        try
        {
            PorterDecision decision = ClassicBattlegrounds.Porter(player.Level, player.RealmLevel, player.RealmPoints,
                (int)player.Realm, Brackets());

            if (decision.Refusal != null)
            {
                long now = GameLoop.GameLoopTime;
                long? lastSaid = player.TempProperties.GetProperty<long?>(ClassicBattlegrounds.RefusedAtKey, null);

                if (ClassicBattlegrounds.ShouldSayRefusal(now, lastSaid))
                {
                    porter.SayTo(player, eChatLoc.CL_ChatWindow, decision.Refusal);
                    player.TempProperties.SetProperty(ClassicBattlegrounds.RefusedAtKey, now);
                }

                return null;
            }

            BattlegroundLanding landing = decision.Destination;
            return landing == null
                ? null
                : new GameLocation(landing.Name, landing.Region, landing.X, landing.Y, landing.Z, landing.Heading);
        }
        catch (Exception ex)
        {
            // The porter goes through every player in range; one player's error must not stop the others.
            Log.Error($"Classic battlegrounds: the porter could not decide for {player?.Name}", ex);
            return null;
        }
    }

    // Called by upstream's BattlegroundTeleportOptions (the realm teleporters' [Battlegrounds] choice, 0.35)
    // once a player of the right level, not a GM, picks a battleground: the refusal to say, or null when the
    // realm rank is under that battleground's cap or the region has no battleground row. Unlike the porter's,
    // it is said every time: the player asked.
    public static string RealmRankRefusal(GamePlayer player, ushort region)
    {
        try
        {
            BattlegroundBracket bracket = Bracket(region);
            return bracket == null ? null : ClassicBattlegrounds.CapRefusal(bracket, player.RealmLevel, player.RealmPoints);
        }
        catch (Exception ex)
        {
            // Closed on an error, like the porter: no teleport.
            Log.Error($"Classic battlegrounds: the teleporter could not check {player?.Name}'s realm rank", ex);
            return "I cannot send you to the battlegrounds right now.";
        }
    }

    // The bot rule (ClassicBattlegrounds.BotFits) for a trip or a camp in this battleground region: the
    // reason a gamebot (or another member of its party) may not go in, or null when it may. A bot already in
    // that region may stay. Called by upstream's bot code next to its level checks.
    public static string BotOverCap(GameLiving bot, ushort region)
    {
        try
        {
            BattlegroundBracket bracket = Bracket(region);
            return ClassicBattlegrounds.BotFits(bracket, RealmLevel(bot), bot.CurrentRegionID == region)
                ? null
                : ClassicBattlegrounds.BotOverCapReason(bracket);
        }
        catch (Exception ex)
        {
            // Closed on an error: the bot does something else.
            LogBotError(bot?.Name, ex);
            return "The battleground's realm rank cap could not be checked";
        }
    }

    public static bool BotFits(GameLiving bot, ushort region)
    {
        return BotOverCap(bot, region) == null;
    }

    // BotOverCap for the bot and, when it plans for its whole party (a shared group camp), every member: the
    // first reason found, or null when they all may go in.
    public static string PartyOverCap(GameBot bot, bool wholeParty, ushort region)
    {
        string reason = BotOverCap(bot, region);
        if (reason != null || !wholeParty || bot.Group == null)
            return reason;

        return bot.Group.GetMembersInTheGroup().Select(member => BotOverCap(member, region))
            .FirstOrDefault(memberReason => memberReason != null);
    }

    public static bool PartyFits(GameBot bot, bool wholeParty, ushort region)
    {
        return PartyOverCap(bot, wholeParty, region) == null;
    }

    // Whether a gamebot may be given the battleground goal: under the cap of the battleground for its level
    // (upstream's BattlegroundBrackets.ForLevel; no battleground for the level is upstream's rule, not this
    // one). Strict: a bot already inside gets no new battleground goal either.
    public static bool BotFitsItsBattleground(GameBot bot)
    {
        return FitsItsBattleground(bot.Level, RealmLevel(bot), bot.Name);
    }

    // The same for a saved bot record before the bot enters the world (AutonomousBotGoalPolicy's
    // ReconcileSavedAssignment, when it picks a new goal for the record).
    public static bool RecordFitsItsBattleground(OfflineWorldBotRecord record)
    {
        return FitsItsBattleground(record.Level, AutonomousBotRealmPointRewards.RealmLevelFor(record.RealmPoints), record.Name);
    }

    private static bool FitsItsBattleground(int level, int realmLevel, string name)
    {
        try
        {
            BattlegroundBrackets.Bracket own = BattlegroundBrackets.ForLevel(level);
            return own == null || ClassicBattlegrounds.BotFits(Bracket(own.RegionId), realmLevel);
        }
        catch (Exception ex)
        {
            LogBotError(name, ex);
            return false;
        }
    }

    // A gamebot's realm level comes from its realm points (AutonomousBotRealmPointRewards.RealmLevelFor), on the
    // players' table, so the caps mean the same for both.
    private static int RealmLevel(GameLiving living)
    {
        return living switch
        {
            GamePlayer player => player.RealmLevel,
            IGamePlayer bot => bot.RealmLevel,
            _ => 0,
        };
    }

    // Bots ask on their AI turns, so a broken row would log on every turn: only the first error is logged.
    private static int _botErrorLogged;

    private static void LogBotError(string name, Exception ex)
    {
        if (Interlocked.Exchange(ref _botErrorLogged, 1) == 0)
            Log.Error($"Classic battlegrounds: could not check {name}'s realm rank for a battleground (logged once)", ex);
    }

    // The Keep Manager loads the battleground rows before the scripts' Loaded event (GameServer.Start).
    [ScriptLoadedEvent]
    public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        try
        {
            Log.Info(ClassicBattlegrounds.Summary(Brackets()));
        }
        catch (Exception ex)
        {
            Log.Warn("Classic battlegrounds: the battleground rows could not be read: " + ex.Message);
        }

        GameEventMgr.AddHandler(GamePlayerEvent.Quit, OnQuit);
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, OnGameEntered);
        GameEventMgr.AddHandler(KeepEvent.KeepTaken, OnKeepTaken);
    }

    [ScriptUnloadedEvent]
    public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.Quit, OnQuit);
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, OnGameEntered);
        GameEventMgr.RemoveHandler(KeepEvent.KeepTaken, OnKeepTaken);
    }

    // GamePlayer.Quit sends this before Delete() runs upstream's own logout check (CleanupOnDisconnect),
    // which then finds the character outside the battleground. After /quit the character is saved, so the
    // move sticks; after a link death it was saved before, and the login check moves it instead. Bots are
    // GameBot, a GameNPC, so they never get here. A handler that throws is logged by the event chain.
    private static void OnQuit(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is GamePlayer player && OverLimit(player, out _))
            MoveOut(player);
    }

    // Upstream's own login check skips the battlegrounds (Region.IsRvR leaves out 250-253), so this one runs
    // while upstream's switch for it, teleport_login_bg_level_exceeded, is on. GameEntered fires inside
    // PlayerInit, before "player init finished", hence the delay.
    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player
            || !Properties.TELEPORT_LOGIN_BG_LEVEL_EXCEEDED
            || !OverLimit(player, out _))
            return;

        ushort region = player.CurrentRegionID;
        int attempts = 0;
        new ECSGameTimer(player, timer => MoveOutIfStillOver(player, region, ++attempts),
            ClassicBattlegrounds.LoginCheckDelayMs);
    }

    // Only if the player is still in the game on the same client, still in that battleground and still over
    // its limit. A client that isn't Playing yet (a slow zone load) is checked again a second later, up to
    // LoginCheckAttempts times in all.
    private static int MoveOutIfStillOver(GamePlayer player, ushort region, int attempt)
    {
        try
        {
            GameClient client = player.Client;
            if (player.ObjectState != GameObject.eObjectState.Active || client.Player != player)
                return 0;

            if (client.ClientState != GameClient.eClientState.Playing)
            {
                if (attempt < ClassicBattlegrounds.LoginCheckAttempts)
                    return ClassicBattlegrounds.LoginCheckDelayMs;

                Log.Info($"Classic battlegrounds: {player.Name} was not playing yet; login check skipped");
                return 0;
            }

            if (player.CurrentRegionID != region
                || !OverLimit(player, out BattlegroundBracket bracket))
                return 0;

            bool atBind = MoveOut(player);

            // MoveOut only sends a character outside 250-253, so one still here was moved nowhere (the bind
            // point move failed and ExitBattleground found no Teleport row): no message, only a warning.
            if (player.CurrentRegionID == region)
                Log.Warn($"Classic battlegrounds: could not move {player.Name} out of region {region}");
            else
                player.Out.SendMessage(ClassicBattlegrounds.OutgrownMessage(bracket, atBind), eChatType.CT_System,
                    eChatLoc.CL_SystemWindow);
        }
        catch (Exception ex)
        {
            // A timer that throws sends its owner to the character screen, so this one only logs.
            Log.Error($"Classic battlegrounds: could not move {player.Name} out of the battleground", ex);
        }

        return 0;
    }

    // A capture resets the keep to starting_keep_level (AbstractGameKeep.Reset) and then raises this, with no
    // sender. A battleground's central keep goes back to level 1, so its guards keep their levels.
    private static void OnKeepTaken(DOLEvent e, object sender, EventArgs args)
    {
        if (args is not KeepEventArgs { Keep: { } keep }
            || !ClassicBattlegrounds.ShouldResetKeepLevel(keep.Region, keep.IsPortalKeep, keep.Level))
            return;

        keep.ChangeLevel(1);

        // Reset left the gates at level 4 health, and ChangeLevel rescales only a door that knows its old
        // maximum, which one loaded from a Door row doesn't.
        foreach (GameKeepDoor door in keep.Doors.Values)
        {
            door.Health = door.MaxHealth;
            door.SaveIntoDatabase();
        }

        keep.SaveIntoDatabase();
    }

    // The battleground rows the Keep Manager loaded at start, in ClassicBattlegrounds.Regions order.
    private static List<BattlegroundBracket> Brackets()
    {
        List<BattlegroundBracket> brackets = new();

        foreach (ushort region in ClassicBattlegrounds.Regions)
        {
            BattlegroundBracket bracket = Bracket(region);
            if (bracket != null)
                brackets.Add(bracket);
        }

        return brackets;
    }

    // One of the four regions' battleground row, or null (no row, not one of the four, or no running server,
    // as in upstream's unit tests that reach the bot hooks). The cap is the realm points of MaxRealmLevel:
    // REALMPOINTS_FOR_LEVEL[MaxRealmLevel], 0 without a cap. A MaxRealmLevel past the table's end can't be
    // reached, since RealmLevel stops at its last entry.
    private static BattlegroundBracket Bracket(ushort region)
    {
        if (!ClassicBattlegrounds.IsBattleground(region) || GameServer.Instance == null)
            return null;

        DbBattleground row = GameServer.KeepManager.GetBattleground(region);
        if (row == null)
            return null;

        long[] points = GamePlayer.REALMPOINTS_FOR_LEVEL;
        long cap = row.MaxRealmLevel == 0 ? 0 : points[Math.Min((int)row.MaxRealmLevel, points.Length - 1)];
        return new BattlegroundBracket(region, ClassicBattlegrounds.Names[region], row.MinLevel, row.MaxLevel,
            row.MaxRealmLevel, cap);
    }

    // The character's state now. A character without an account counts as privilege level 0, never over.
    private static bool OverLimit(GamePlayer player, out BattlegroundBracket bracket)
    {
        return ClassicBattlegrounds.IsOverLimit(player.Client?.Account?.PrivLevel ?? 0, player.CurrentRegionID,
            player.Level, player.RealmLevel, Brackets(), out bracket);
    }

    // To the bind point when it is a real place outside the battlegrounds, otherwise (or when that move
    // fails) to the realm's home portal keep: Castle Sauvage, Svasud Faste or Druim Ligen. Returns whether
    // the character went to its bind point.
    private static bool MoveOut(GamePlayer player)
    {
        Region bindRegion = WorldMgr.GetRegion((ushort)player.BindRegion);
        bool hasZone = bindRegion?.GetZone(player.BindXpos, player.BindYpos) != null;

        if (ClassicBattlegrounds.GoesToBind(bindRegion != null, hasZone, player.BindRegion)
            && player.MoveTo((ushort)player.BindRegion, player.BindXpos, player.BindYpos, player.BindZpos,
                (ushort)player.BindHeading))
            return true;

        GameServer.KeepManager.ExitBattleground(player);
        return false;
    }
}
