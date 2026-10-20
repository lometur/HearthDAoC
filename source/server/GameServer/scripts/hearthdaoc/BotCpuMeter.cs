using System;
using DOL.AI.Brain;

namespace DOL.GS.HearthDAoC;

// HearthDAoC: the bot CPU meter's hot path (#137; spec docs/fork/specs/2026-10-14-bot-cpu-meter-design.md). While the
// meter is on, each game-loop callback is timed and charged to the kind of its owner (2.5, 3.3) and to its stage (3.4),
// so the owner can compare the average CPU of each kind of bot. BotCpuMeter has no static field initializer and no
// static constructor (5.1): nothing in it runs or allocates while the meter is off.

/// <summary>
/// The player-bot contract (2.5): a <see cref="GamePlayer"/> whose client implements this marker is counted as
/// <see cref="BotCpuKind.Playerbot"/>, with no meter change. Deriving the client from <see cref="BotDummyClient"/>
/// also works.
/// </summary>
public interface IPlayerBotClient { }

/// <summary>What an item is charged to (2.5), in the report's kind order. The text name is the lower-case member name.
/// </summary>
public enum BotCpuKind
{
    /// <summary>A <see cref="GameBot"/> with <see cref="GameBot.IsAutonomousWorldBot"/>.</summary>
    Gamebot,
    /// <summary>Any other <see cref="GameBot"/>: companions and temporary helpers.</summary>
    Companion,
    /// <summary>A <see cref="GamePlayer"/> whose client is an <see cref="IPlayerBotClient"/> or a
    /// <see cref="BotDummyClient"/>.</summary>
    Playerbot,
    /// <summary>A <see cref="GamePlayer"/> the <c>/bot</c> pilot drives (<see cref="AutonomousPlayerPilot.IsActive"/>).
    /// </summary>
    Piloted,
    /// <summary>Any other <see cref="GamePlayer"/>.</summary>
    Player,
    /// <summary>Any other living: monsters, guards, merchants, keep doors.</summary>
    Monster,
    /// <summary>No owner, an owner that is not living, or a pet walk longer than
    /// <see cref="BotCpuMeter.PetWalkSteps"/>.</summary>
    Unattributed,
}

/// <summary>
/// A game-loop stage (3.4), in the CSV's column order (2.3). The text name is the member name. <see cref="Fleet"/> is
/// the GameBot fleet planning scope (3.5) and <see cref="Pets"/> the work of pets in any stage: neither has a profile
/// key.
/// </summary>
public enum BotCpuStage
{
    GameLoop,
    Timer,
    ClientBegin,
    Npc,
    Fleet,
    Attack,
    Casting,
    Effect,
    EffectListBegin,
    EffectListEnd,
    Movement,
    Crafting,
    Reaper,
    Zone,
    ClientEnd,
    Quests,
    Pets,
    Other,
}

/// <summary>The meter's hot path: the stage of a profile key and the kind of an owner.</summary>
public static class BotCpuMeter
{
    /// <summary>The most owners the pet walk follows (3.3); a longer walk, a cycle, is
    /// <see cref="BotCpuKind.Unattributed"/>.</summary>
    public const int PetWalkSteps = 8;

    /// <summary>
    /// The stage of a <see cref="GameLoop.ActiveService"/> profile key (3.4): the service's class name, plus
    /// <c>.BeginTick</c> or <c>.EndTick</c> for those steps. An unknown, missing or empty key is
    /// <see cref="BotCpuStage.Other"/>. A switch, so there is no map to build and nothing runs before it is called.
    /// </summary>
    public static BotCpuStage StageOf(string profileKey) => profileKey switch
    {
        "GameLoopService" => BotCpuStage.GameLoop,
        "TimerService" => BotCpuStage.Timer,
        "ClientService.BeginTick" => BotCpuStage.ClientBegin,
        "NpcService" => BotCpuStage.Npc,
        "AttackService" => BotCpuStage.Attack,
        "CastingService" => BotCpuStage.Casting,
        "EffectService" => BotCpuStage.Effect,
        "EffectListService.BeginTick" => BotCpuStage.EffectListBegin,
        "EffectListService.EndTick" => BotCpuStage.EffectListEnd,
        "MovementService" => BotCpuStage.Movement,
        "CraftingService" => BotCpuStage.Crafting,
        "ReaperService" => BotCpuStage.Reaper,
        "ZoneService" => BotCpuStage.Zone,
        "ClientService.EndTick" => BotCpuStage.ClientEnd,
        "DailyQuestService" or "WeeklyQuestService" or "MonthlyQuestService" => BotCpuStage.Quests,
        _ => BotCpuStage.Other,
    };

    /// <summary>The kind of an owner (3.3), with the real pilot check, <see cref="AutonomousPlayerPilot.IsActive"/>.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, out GameObject charged, out bool pet) =>
        KindOf(owner, AutonomousPlayerPilot.IsActive, out charged, out pet);

    /// <summary>
    /// The kind of an owner (3.3), tested in this order: a <see cref="GameBot"/>; a <see cref="GamePlayer"/>, asking
    /// <paramref name="isPiloted"/> only when its client is no bot client; a pet, walked up its owners to the first
    /// that is not a pet, following at most <see cref="PetWalkSteps"/> of them; any other living; anything else or
    /// none. <paramref name="charged"/> is the object whose kind it is (for a pet, the walk's end), null when
    /// <see cref="BotCpuKind.Unattributed"/>; <paramref name="pet"/> says the walk followed at least one owner to get
    /// there. Upstream's <c>GetLivingOwner</c> is not used: it gives a companion's pets to its player and can throw.
    /// </summary>
    public static BotCpuKind KindOf(GameObject owner, Func<GamePlayer, bool> isPiloted, out GameObject charged,
        out bool pet)
    {
        GameObject current = owner;
        pet = false;
        for (int steps = 0; ; steps++)
        {
            switch (current)
            {
                case GameBot bot: // First: a GameBot's BotBrain is itself an IControlledBrain (bots/BotBrain.cs:21).
                    charged = bot;
                    return bot.IsAutonomousWorldBot ? BotCpuKind.Gamebot : BotCpuKind.Companion;
                case GamePlayer player:
                    charged = player;
                    if (player.Client is IPlayerBotClient or BotDummyClient)
                        return BotCpuKind.Playerbot;
                    return isPiloted != null && isPiloted(player) ? BotCpuKind.Piloted : BotCpuKind.Player;
                case GameNPC npc when npc.Brain is IControlledBrain { Owner: { } next }:
                    if (steps == PetWalkSteps)
                    {
                        charged = null;
                        pet = false;
                        return BotCpuKind.Unattributed;
                    }

                    current = next;
                    pet = true;
                    break;
                case GameLiving living:
                    charged = living;
                    return BotCpuKind.Monster;
                default: // none, or not living: only the first owner, as every owner a walk follows is living
                    charged = null;
                    return BotCpuKind.Unattributed;
            }
        }
    }
}
