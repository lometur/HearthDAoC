using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.AI.Brain;
using DOL.GS.Quests;

namespace DOL.GS.HearthDAoC.PlayerBots;

public enum PlayerBotWakeKind { Aggro, TargetsBot, HeldAggro, Target, Attacker, Talk, Pet, Quest }

public readonly record struct PlayerBotWakeWindow(int Seconds, bool Lever, long Scans, long Started, long Aggro,
    long TargetsBot, long HeldAggro, long Target, long Attacker, long Talk, long Pet = 0, long Quest = 0);

// HearthDAoC #137 lever. With HEARTHDAOC_PLAYER_BOT_WAKE=gamebot a player bot's client updates keep its NPC cache but
// wake no NPC (the gate in ClientService.OnObjectCreateOrUpdateForPlayer). The driver then wakes what a GameBot wakes
// (BotBrain.cs:1561-1584, every 1,250 + ObjectID % 500 ms), plus the bot's own target, the monsters fighting it, each
// NPC it talks to, its own main pet, and, at each scan, the NPCs its current quests name within 6,000 (the owner's
// rule, 2026-10-22: a quest's walking fellows and roamers move only while awake). A wake is
// GameNPC.OnAutonomousBotNearby (GameNPC.cs:2042-2048): IsVisibleToPlayersOrBots for 60 s, never IsVisibleToPlayers.
// Not woken yet: the bot's other pets (an Animist's field turrets, a bomber, a Bonedancer's minions: outside
// ControlledBrain, and ABrain.IsActive counts only GameBot owners, AutonomousSummonActivity.cs:22-44), the porter (wake
// it before the whisper, as AutonomousFrontierTransport.cs:261 does), and the NPCs a scripted quest names in its code.
public static class PlayerBotWake
{
    public const string EnvironmentVariable = "HEARTHDAOC_PLAYER_BOT_WAKE";
    public const int ScanMilliseconds = 1_250, ScanSpreadMilliseconds = 500;   // BotBrain.cs:1568
    public const int QuestRadius = 6_000;   // the owner's rule (2026-10-22): the NPCs the bot's current quests name

    /// <summary>On: player bots wake only what GameBots wake. Set once in PlayerBotSpawner.OnServerStarted, which
    /// GameServer raises before GameLoop.Init (GameServer.cs:510, :517), so the loop threads start after the write.</summary>
    public static bool Lever { get; set; }

    private static readonly long[] _woke = new long[Enum.GetValues<PlayerBotWakeKind>().Length];
    private static long _scans, _started;
    [ThreadStatic] private static List<(GameNPC Npc, PlayerBotWakeKind Kind)> _list;

    /// <summary>Unset or empty is gamebot, the lever: on #137's level-matched run 390 player bots cost 0.44x the
    /// estimated cost of the live server's 390 GameBots with it, 1.10x without it (issuecomment-6441658806).</summary>
    public static bool Parse(string text)
    {
        string value = text?.Trim() ?? "";
        if (value.Equals("player", StringComparison.OrdinalIgnoreCase)) return false;
        if (value.Length == 0 || value.Equals("gamebot", StringComparison.OrdinalIgnoreCase)) return true;
        throw new FormatException($"{EnvironmentVariable} must be player or gamebot, got '{text}'");
    }

    public static string Describe(bool lever) => lever ? "gamebot" : "player";

    /// <summary>ClientService's gate: a human's update always wakes; a player bot's only with the lever off.</summary>
    public static bool ClientUpdateWakes(GamePlayer player) => !Lever || player?.Client is not IPlayerBotClient;

    public static long NextScanTick(GamePlayer bot, long now) =>
        now + ScanMilliseconds + (uint)bot.ObjectID % ScanSpreadMilliseconds;

    /// <summary>The GameBot's skip rule (BotBrain.cs:1569-1572) for a player: no stable route or Shade here.</summary>
    public static bool CanWake(GamePlayer bot) =>
        bot != null && bot.IsAlive && bot.ObjectState == GameObject.eObjectState.Active && !bot.IsStealthed;

    /// <summary>Why the 3,600-unit scan wakes this NPC, or null. Aggro is the GameBot rule as written
    /// (BotBrain.cs:1576-1582). HeldAggro covers a sleeping monster that was given aggro: a quest event spawn
    /// (ClassicQuests.cs:1059-1060), a linked camp (LinkedCamps.cs:94), a behaviour attack (AttackAction.cs:57).
    /// TargetsBot covers anything that has the bot targeted.</summary>
    public static PlayerBotWakeKind? ScanReason(GamePlayer bot, GameNPC npc)
    {
        if (npc?.Brain == null || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active ||
            npc is GameTaxi or GameTrainingDummy)
            return null;
        if (npc.Brain is StandardMobBrain brain)
        {
            if (brain.AggroLevel > 0 && brain.AggroRange > 0 && npc.IsWithinRadius(bot, brain.AggroRange) &&
                brain.CanAggroTarget(bot))
                return PlayerBotWakeKind.Aggro;
            if (brain.HasAggro)
                return PlayerBotWakeKind.HeldAggro;
        }
        return npc.TargetObject == bot ? PlayerBotWakeKind.TargetsBot : null;
    }

    /// <summary>Woken every think: the driver's target and the client-side target while the bot could see them (same
    /// region, within VISIBILITY_DISTANCE, as ClientService.UpdateNpcs would send them), and the attackers. The
    /// client-side target only while it is the driver's or targets the bot: the driver lets a target go without
    /// clearing TargetObject (PlayerBotDriver Engage's unreachable branch, SettleTarget, Disengage), and a killed
    /// monster respawns as the same object (GameNPC.RespawnTimerCallback). Last, the bot's own pet while the bot could
    /// see it: a human's client update stamps its pet (ClientService.cs:679-680), and a player's pet is no GameBot's
    /// (AutonomousSummonActivity.AutonomousOwner, ABrain.cs:17-18), so under the lever nothing else keeps its brain
    /// running (NpcService.cs:66).</summary>
    public static void CollectAlways(GamePlayer bot, GameLiving target, IEnumerable<GameLiving> attackers,
        List<(GameNPC Npc, PlayerBotWakeKind Kind)> into)
    {
        AddInSight(into, bot, target as GameNPC, PlayerBotWakeKind.Target);
        if (bot.TargetObject is GameNPC clicked && (clicked == target || clicked.TargetObject == bot))
            AddInSight(into, bot, clicked, PlayerBotWakeKind.Target);
        if (attackers != null)
            foreach (GameLiving living in attackers)
                Add(into, living as GameNPC, PlayerBotWakeKind.Attacker);
        AddInSight(into, bot, bot.ControlledBrain?.Body, PlayerBotWakeKind.Pet);
    }

    private static void AddInSight(List<(GameNPC Npc, PlayerBotWakeKind Kind)> into, GamePlayer bot, GameNPC npc,
        PlayerBotWakeKind kind)
    {
        if (npc != null && bot.IsWithinRadius(npc, WorldMgr.VISIBILITY_DISTANCE))   // false in another region (GameObject.cs:161)
            Add(into, npc, kind);
    }

    // A dead or removed NPC is never woken: its brain would start for nothing.
    private static void Add(List<(GameNPC Npc, PlayerBotWakeKind Kind)> into, GameNPC npc, PlayerBotWakeKind kind)
    {
        if (npc == null || !npc.IsAlive || npc.ObjectState != GameObject.eObjectState.Active) return;
        foreach ((GameNPC known, _) in into) if (known == npc) return;
        into.Add((npc, kind));
    }

    /// <summary>What the bot's current quests name, read as the quest markers read a journal (QuestIndicators.For): each
    /// data quest's current step target (TargetName in TargetRegion) and the names its classic stage adds
    /// (ClassicQuests.StepNames, any region), and each reward quest's giver. Scripted quests keep their NPCs in their own
    /// code: none here.</summary>
    public static void QuestNames(GamePlayer bot, List<JournalStep> steps, List<GameNPC> givers)
    {
        foreach (KeyValuePair<AbstractQuest, byte> pair in bot.QuestList)   // not Keys: that locks every bucket and copies
        {
            switch (pair.Key)
            {
                case DataQuest data when data.Step > 0:
                    steps.Add(new(data.StepType, data.TargetName, data.TargetRegion));
                    foreach (string name in ClassicQuests.StepNames(data.ID, data.Step))
                        steps.Add(new(data.StepType, name, 0));
                    break;
                case RewardQuest reward when reward.QuestGiver != null:
                    givers.Add(reward.QuestGiver);
                    break;
            }
        }
    }

    /// <summary>Changes when the bot takes a quest, moves to its next step or ends it: OnThink reads the names again
    /// only then. A DataQuest getter logs an error at each read of a malformed step, and a scan comes every 1.25-1.75 s.
    /// </summary>
    public static long JournalKey(GamePlayer bot)
    {
        long key = 17;
        foreach (KeyValuePair<AbstractQuest, byte> pair in bot.QuestList)   // a sum: the dictionary's order doesn't count
            key += HashCode.Combine(RuntimeHelpers.GetHashCode(pair.Key), pair.Key.Step);
        return key;
    }

    /// <summary>Whether a step names the NPC, as the quest markers match one (QuestIndicators.Targets).</summary>
    public static bool Named(IReadOnlyList<JournalStep> steps, GameNPC npc)
    {
        foreach (JournalStep step in steps)
            if (QuestIndicators.Targets(step, npc.Name, npc.CurrentRegionID))
                return true;
        return false;
    }

    /// <summary>The quest rule: the givers within QuestRadius, and the nearby NPCs a step names.</summary>
    public static void CollectQuest(GamePlayer bot, IReadOnlyList<JournalStep> steps, IReadOnlyList<GameNPC> givers,
        IEnumerable<GameNPC> nearby, List<(GameNPC Npc, PlayerBotWakeKind Kind)> into)
    {
        foreach (GameNPC giver in givers)
            if (bot.IsWithinRadius(giver, QuestRadius))   // false in another region (GameObject.cs:161)
                Add(into, giver, PlayerBotWakeKind.Quest);
        if (steps.Count == 0) return;
        foreach (GameNPC npc in nearby)
            if (Named(steps, npc)) Add(into, npc, PlayerBotWakeKind.Quest);
    }

    /// <summary>Each driver think while the bot is alive (PlayerBotDriver.Think, step 6b). On the bot's own timer,
    /// so the meter charges it to playerbot / Timer (BotCpuMeter.cs:657).</summary>
    public static void OnThink(PlayerBotState state, GamePlayer bot, long now)
    {
        if (!Lever || !CanWake(bot)) return;
        List<(GameNPC Npc, PlayerBotWakeKind Kind)> list = _list ??= new();
        list.Clear();
        CollectAlways(bot, state.Target, bot.attackComponent?.AttackerTracker.Attackers, list);
        if (now >= state.NextWakeScanTick)
        {
            state.NextWakeScanTick = NextScanTick(bot, now);
            Interlocked.Increment(ref _scans);
            List<JournalStep> steps = state.QuestSteps ??= new();
            List<GameNPC> givers = state.QuestGivers ??= new();
            long journal = JournalKey(bot);
            if (journal != state.QuestJournal)
            {
                steps.Clear();
                givers.Clear();
                QuestNames(bot, steps, givers);
                state.QuestJournal = journal;   // after: a throw reads them again at the next scan
            }
            // The wider query first: a narrower one in the same tick filters its cached list (GameObject.GetNPCsInRadius).
            if (steps.Count > 0 || givers.Count > 0)
                CollectQuest(bot, steps, givers, steps.Count > 0 ? bot.GetNPCsInRadius(QuestRadius) : [], list);
            foreach (GameNPC npc in bot.GetNPCsInRadius(StandardMobBrain.MAX_AGGRO_DISTANCE))   // :22, 3600
                if (ScanReason(bot, npc) is PlayerBotWakeKind kind) Add(list, npc, kind);
        }
        foreach ((GameNPC npc, PlayerBotWakeKind kind) in list) Wake(npc, kind);
        list.Clear();
    }

    /// <summary>The errands call this after each whisper, accept, interact or give (the NPC a human would face).</summary>
    public static void Interacting(GamePlayer bot, GameNPC npc)
    {
        if (Lever && npc != null && bot?.Client is IPlayerBotClient) Wake(npc, PlayerBotWakeKind.Talk);
    }

    /// <summary>For the errand lines: whether the NPC's brain ran before the bot acted. Waking: Start queued it, and
    /// NpcService's next tick adds it (ServiceObjectStore.cs:79-82, :147).</summary>
    public static string BrainState(GameNPC npc)
    {
        ServiceObjectId id = npc?.Brain?.ServiceObjectId;
        return id == null ? "none" : id.IsSet ? "awake" : id.IsPendingAddition ? "waking" : "asleep";
    }

    public static void Wake(GameNPC npc, PlayerBotWakeKind kind)
    {
        // Started counts a brain once: a second wake in the same tick finds it pending, not asleep.
        bool asleep = npc.Brain?.ServiceObjectId is { IsSet: false, IsPendingAddition: false };
        npc.OnAutonomousBotNearby();
        Interlocked.Increment(ref _woke[(int)kind]);
        if (asleep && npc.Brain?.ServiceObjectId is { } after && (after.IsPendingAddition || after.IsSet))
            Interlocked.Increment(ref _started);
    }

    public static PlayerBotWakeWindow TakeWindow(int seconds)
    {
        long Take(PlayerBotWakeKind k) => Interlocked.Exchange(ref _woke[(int)k], 0);
        return new(seconds, Lever, Interlocked.Exchange(ref _scans, 0), Interlocked.Exchange(ref _started, 0),
            Take(PlayerBotWakeKind.Aggro), Take(PlayerBotWakeKind.TargetsBot), Take(PlayerBotWakeKind.HeldAggro),
            Take(PlayerBotWakeKind.Target), Take(PlayerBotWakeKind.Attacker), Take(PlayerBotWakeKind.Talk),
            Take(PlayerBotWakeKind.Pet), Take(PlayerBotWakeKind.Quest));
    }

    public static string Format(PlayerBotWakeWindow w) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_WAKE win={w.Seconds} lever={Describe(w.Lever)} scans={w.Scans} started={w.Started} " +
        $"aggro={w.Aggro} targetsBot={w.TargetsBot} heldAggro={w.HeldAggro} target={w.Target} " +
        $"attacker={w.Attacker} talk={w.Talk} pet={w.Pet} quest={w.Quest}");
}
