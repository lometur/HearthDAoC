using System;
using System.Collections.Generic;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the game wiring of the classic battlegrounds. The frontier porter (OFTeleporter) asks
// PorterDestination where a character wearing the battlegrounds medallion goes; a character over its
// battleground's limit is moved out at logout and, after a link death or a crash, a moment after its next
// login; and a captured central keep goes back to level 1. ClassicBattlegrounds makes every decision; this
// class reads the battleground rows and the character's state and carries out the outcome.
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
        new ECSGameTimer(player, timer => MoveOutIfStillOver(player, region), ClassicBattlegrounds.LoginCheckDelayMs);
    }

    // Only if the player is still in the game on the same client, still in that battleground and still over
    // its limit.
    private static int MoveOutIfStillOver(GamePlayer player, ushort region)
    {
        try
        {
            GameClient client = player.Client;
            if (player.ObjectState != GameObject.eObjectState.Active
                || client.Player != player
                || client.ClientState != GameClient.eClientState.Playing
                || player.CurrentRegionID != region
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
        keep.SaveIntoDatabase();
    }

    // The battleground rows the Keep Manager loaded at start, in ClassicBattlegrounds.Regions order. The cap
    // is the realm points of MaxRealmLevel: REALMPOINTS_FOR_LEVEL[MaxRealmLevel], 0 without a cap. A
    // MaxRealmLevel past the table's end can't be reached, since RealmLevel stops at its last entry.
    private static List<BattlegroundBracket> Brackets()
    {
        List<BattlegroundBracket> brackets = new();
        long[] points = GamePlayer.REALMPOINTS_FOR_LEVEL;

        foreach (ushort region in ClassicBattlegrounds.Regions)
        {
            DbBattleground row = GameServer.KeepManager.GetBattleground(region);
            if (row == null)
                continue;

            long cap = row.MaxRealmLevel == 0 ? 0 : points[Math.Min((int)row.MaxRealmLevel, points.Length - 1)];
            brackets.Add(new BattlegroundBracket(region, ClassicBattlegrounds.Names[region], row.MinLevel,
                row.MaxLevel, row.MaxRealmLevel, cap));
        }

        return brackets;
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
