using System;
using System.Collections.Generic;
using System.Linq;
using DOL.Database;
using DOL.Events;
using DOL.GS.PacketHandler;
using DOL.GS.ServerProperties;
using DOL.Logging;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the game wiring of the Shrouded Isles start choice. A few seconds after a qualifying
// character's first entry into the world, the server asks with the client's two-button dialog whether to
// begin in its realm's Shrouded Isles town. SiStartChoice makes every decision; this class reads the
// character's state, sends the question and carries out the answer. Off by default; HearthDAoC turns it on
// through HEARTHDAOC_SI_START_CHOICE.
public static class SiStartChoiceScript
{
    [ServerProperty("server", "si_start_choice",
        "Ask new level 1 characters of the classic races whether to start in their realm's Shrouded Isles town (Caer Gothwaite, Aegirhamn, Grove of Domnann)",
        false)]
    public static bool SI_START_CHOICE;

    // GameEntered fires before the server sends "player init finished", the patch notes and the starter
    // help, so the question waits this long.
    private const int AskDelay = 2000;

    private static readonly Logger Log = LoggerManager.Create(typeof(SiStartChoiceScript));

    // One delegate instance, so the callback can tell whether it is still the player's pending dialog.
    private static readonly CustomDialogResponse ResponseCallback = OnResponse;

    // Realm -> its SI town's arrival point, read from the world's Teleport rows at script load.
    private static IReadOnlyDictionary<int, SiStartDestination> _destinations =
        new Dictionary<int, SiStartDestination>();

    [ScriptLoadedEvent]
    public static void OnScriptLoaded(DOLEvent e, object sender, EventArgs args)
    {
        Dictionary<int, SiStartDestination> destinations = new();

        try
        {
            foreach (KeyValuePair<int, string> pair in SiStartChoice.TeleportIds)
            {
                // The towns' own SI teleporters use these rows: Type is empty, and the key is case-sensitive.
                DbTeleport row = WorldMgr.GetTeleportLocation((eRealm)pair.Key, ":" + pair.Value);
                if (row == null)
                {
                    Log.Warn($"Shrouded Isles start choice: no Teleport row \"{pair.Value}\" for realm {pair.Key}, " +
                        "so that realm's new characters are not asked.");
                    continue;
                }

                destinations[pair.Key] = new SiStartDestination(pair.Value, (ushort)row.RegionID, row.X, row.Y, row.Z,
                    (ushort)row.Heading);
            }

            Log.Info("Shrouded Isles start choice: ready for " +
                string.Join(", ", destinations.Keys.Select(realm => (eRealm)realm)) +
                $" (si_start_choice={SI_START_CHOICE})");
        }
        catch (Exception ex)
        {
            destinations.Clear();
            Log.Warn("Shrouded Isles start choice: the Teleport table could not be read, so nobody will be asked: " + ex.Message);
        }

        _destinations = destinations;
        GameEventMgr.AddHandler(GamePlayerEvent.GameEntered, OnGameEntered);
    }

    [ScriptUnloadedEvent]
    public static void OnScriptUnloaded(DOLEvent e, object sender, EventArgs args)
    {
        GameEventMgr.RemoveHandler(GamePlayerEvent.GameEntered, OnGameEntered);
    }

    // Fires once per login (PlayerInitRequestHandler). Bots are GameBot, a GameNPC, so they never get here.
    private static void OnGameEntered(DOLEvent e, object sender, EventArgs args)
    {
        if (sender is not GamePlayer player || !Qualifies(player))
            return;

        new ECSGameTimer(player, timer => AskIfStillHere(player), AskDelay);
    }

    // PlayerInit can still move a player after GameEntered, so the timer asks only if the player is still in
    // the game on the same client and still qualifies.
    private static int AskIfStillHere(GamePlayer player)
    {
        try
        {
            GameClient client = player.Client;
            if (player.ObjectState == GameObject.eObjectState.Active
                && client.Player == player
                && client.ClientState == GameClient.eClientState.Playing
                && Qualifies(player))
                player.Out.SendCustomDialog(SiStartChoice.Question((int)player.Realm), ResponseCallback);
        }
        catch (Exception ex)
        {
            // A timer that throws sends its owner to the character screen, so this one only logs.
            Log.Error($"Shrouded Isles start choice: could not ask {player.Name}", ex);
        }

        return 0;
    }

    private static void OnResponse(GamePlayer player, byte response)
    {
        try
        {
            // A click clears the pending callback before calling it (DialogResponseHandler). When the server
            // sends another dialog over ours, it calls ours with 0x00 while ours is still pending.
            bool stillPending = player.CustomDialogCallback == ResponseCallback;
            // Classify ignores qualification for a dialog still pending, so only an answer reads the database.
            bool qualifies = !stillPending && Qualifies(player);

            switch (SiStartChoice.Classify(response, stillPending, qualifies, player.IsAlive))
            {
                case SiStartOutcome.Accept:
                    SiStartChoice.ApplyAccept(_destinations[(int)player.Realm],
                        d => player.MoveTo(d.Region, d.X, d.Y, d.Z, d.Heading),
                        d => Bind(player, d),
                        () => GameServer.Database.SaveObject(player.DBCharacter),
                        answer => WriteAnswer(player, answer),
                        message => player.Out.SendMessage(message, eChatType.CT_System, eChatLoc.CL_SystemWindow));
                    break;
                case SiStartOutcome.Decline:
                    WriteAnswer(player, SiStartChoice.AnswerNo);
                    break;
                case SiStartOutcome.NotNow when !player.IsAlive:
                    player.Out.SendMessage(SiStartChoice.DeadMessage, eChatType.CT_System, eChatLoc.CL_SystemWindow);
                    break;
                // Superseded or any other NotNow: nothing is saved, so the question comes back at the next login.
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Shrouded Isles start choice: could not apply the answer of {player.Name}", ex);
        }
    }

    // The character's state now. The saved answer is read only when everything else qualifies.
    private static bool Qualifies(GamePlayer player)
    {
        return SiStartChoice.Qualifies(SI_START_CHOICE, player.Level, player.Race, player.CurrentRegionID,
            () => SelectAnswer(player)?.Value, _destinations.ContainsKey((int)player.Realm));
    }

    // As GamePlayer.Bind() does, but at the destination instead of where the player stands.
    private static void Bind(GamePlayer player, SiStartDestination destination)
    {
        player.BindRegion = destination.Region;
        player.BindXpos = destination.X;
        player.BindYpos = destination.Y;
        player.BindZpos = destination.Z;
        player.BindHeading = destination.Heading;
    }

    // The answer is one custom parameter row per character (BountyQuest.RememberCompletedTarget pattern).
    private static DbCoreCharacterXCustomParam SelectAnswer(GamePlayer player)
    {
        return DOLDB<DbCoreCharacterXCustomParam>.SelectObject(
            DB.Column("DOLCharactersObjectId").IsEqualTo(player.ObjectId)
                .And(DB.Column("KeyName").IsEqualTo(SiStartChoice.AnswerKey)));
    }

    // The only write path: update the character's row when it has one, otherwise add it.
    private static void WriteAnswer(GamePlayer player, string answer)
    {
        DbCoreCharacterXCustomParam record = SelectAnswer(player);
        if (record == null)
            GameServer.Database.AddObject(
                new DbCoreCharacterXCustomParam(player.ObjectId, SiStartChoice.AnswerKey, answer));
        else
        {
            record.Value = answer;
            GameServer.Database.SaveObject(record);
        }
    }
}
