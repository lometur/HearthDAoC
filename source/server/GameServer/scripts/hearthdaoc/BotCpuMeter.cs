using System;
using DOL.AI;
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

/// <summary>
/// What one game-loop item is charged to (3.2 step 1): the <paramref name="Kind"/> and <paramref name="Stage"/> of its
/// slot cell, the <paramref name="Bot"/> whose per-bot counter it adds to (3.10), and whether it counts one toward its
/// kind's <c>n</c> (<paramref name="Presence"/>, 3.8).
/// </summary>
/// <param name="Kind">The kind of the item's owner (3.3).</param>
/// <param name="Stage">The stage of the call, <see cref="BotCpuStage.Pets"/> for a pet's item, or
/// <see cref="BotCpuStage.Fleet"/> for an item with no owner inside the fleet scope (3.5).</param>
/// <param name="Bot">The owner, for a bot kind (<see cref="BotCpuMeter.IsBotKind"/>); null for any other kind and for
/// <see cref="BotCpuStage.Fleet"/>, which counts for no bot.</param>
/// <param name="Presence">A brain in <see cref="BotCpuStage.Npc"/> or a client in
/// <see cref="BotCpuStage.ClientBegin"/>, not a pet, of a kind whose <c>n</c> is its presence: companion, monster and
/// the three player kinds.</param>
public readonly record struct BotCpuCharge(BotCpuKind Kind, BotCpuStage Stage, GameObject Bot, bool Presence);

/// <summary>The meter's hot path: the stage of a profile key, the kind of an owner and what each item charges.
/// </summary>
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

    /// <summary>The kinds whose bots have a per-bot counter, a spread and a top 5 (3.10): gamebot, companion,
    /// playerbot and piloted.</summary>
    public static bool IsBotKind(BotCpuKind kind) =>
        kind is BotCpuKind.Gamebot or BotCpuKind.Companion or BotCpuKind.Playerbot or BotCpuKind.Piloted;

    /// <summary>
    /// What one item of a game-loop call is charged to (3.3, 3.8). Its owner is read as its type
    /// <typeparamref name="T"/> says (3.3's table; a posted action, an <c>int</c> or any other type has none), then the
    /// kind of that owner by the pure
    /// <see cref="KindOf(GameObject, Func{GamePlayer, bool}, out GameObject, out bool)"/> with
    /// <paramref name="isPiloted"/>. A pet's item goes to <see cref="BotCpuStage.Pets"/>, whatever
    /// <paramref name="stage"/>; an item with no owner inside the fleet scope (<paramref name="inFleet"/>, 3.5) to
    /// <see cref="BotCpuKind.Gamebot"/> / <see cref="BotCpuStage.Fleet"/>. Called before the item's callback runs, so
    /// the owner is read while the item is intact; it throws only if a game object's member does (<c>Body</c> is
    /// virtual), and the wrapper's <c>try</c> charges such an item to unattributed (3.2).
    /// </summary>
    public static BotCpuCharge Classify<T>(T item, BotCpuStage stage, bool inFleet, Func<GamePlayer, bool> isPiloted)
    {
        Func<T, GameObject> owner = ItemOwner<T>.Read;
        BotCpuKind kind = KindOf(owner?.Invoke(item), isPiloted, out GameObject charged, out bool pet);
        if (pet)
            stage = BotCpuStage.Pets;
        else if (inFleet && kind == BotCpuKind.Unattributed)
        {
            kind = BotCpuKind.Gamebot;
            stage = BotCpuStage.Fleet;
        }

        // Presence (3.8) only in the item type's own stage, so a pet's item (now Pets) and fleet work never count; nor
        // a gamebot, whose n comes from the registry, nor unattributed, which has none.
        bool presence = stage == ItemOwner<T>.PresenceStage &&
            kind is not (BotCpuKind.Gamebot or BotCpuKind.Unattributed);
        return new BotCpuCharge(kind, stage, IsBotKind(kind) ? charged : null, presence);
    }

    /// <summary>
    /// The owner reader of item type <typeparamref name="T"/> (3.3) and the stage in which its items count presence
    /// (3.8), chosen once per type when an item of it is first classified: only while the meter is on (5.1). A reader
    /// of a type also reads the types derived from it (<see cref="Func{T, TResult}"/> is contravariant); a value type
    /// has none, so it is never boxed.
    /// </summary>
    private static class ItemOwner<T>
    {
        internal static readonly Func<T, GameObject> Read = (Func<T, GameObject>)OwnerReader(typeof(T));

        internal static readonly BotCpuStage? PresenceStage =
            Is<ABrain>(typeof(T)) ? BotCpuStage.Npc : Is<GameClient>(typeof(T)) ? BotCpuStage.ClientBegin : null;
    }

    private static bool Is<TItem>(Type type) => typeof(TItem).IsAssignableFrom(type);

    // The owner of each item type in 3.3's table; each reader gives none for a null item.
    private static Delegate OwnerReader(Type type) => type switch
    {
        _ when Is<ABrain>(type) => (Func<ABrain, GameObject>)(static brain => brain?.Body),
        _ when Is<AttackComponent>(type) => (Func<AttackComponent, GameObject>)(static attack => attack?.owner),
        _ when Is<CastingComponent>(type) => (Func<CastingComponent, GameObject>)(static casting => casting?.Owner),
        _ when Is<ECSGameEffect>(type) => (Func<ECSGameEffect, GameObject>)(static effect => effect?.Owner),
        _ when Is<EffectListComponent>(type) => (Func<EffectListComponent, GameObject>)(static list => list?.Owner),
        _ when Is<MovementComponent>(type) => (Func<MovementComponent, GameObject>)(static move => move?.Owner),
        _ when Is<CraftComponent>(type) => (Func<CraftComponent, GameObject>)(static craft => craft?.Owner),
        _ when Is<ECSGameTimer>(type) => (Func<ECSGameTimer, GameObject>)(static timer => timer?.Owner),
        _ when Is<SubZoneObject>(type) => (Func<SubZoneObject, GameObject>)(static node => node?.Node?.Value),
        _ when Is<LivingBeingKilled>(type) => (Func<LivingBeingKilled, GameObject>)KillOwner,
        _ when Is<GameClient>(type) => (Func<GameClient, GameObject>)ClientOwner,
        _ => null,
    };

    // The Reaper rule (3.3): XP, loot and quest credit run for the killer, so a kill is its killer's when the killer's
    // kind, after the pet walk, is a bot or player kind; otherwise the killed's. Piloted or not changes nothing here,
    // so no pilot check is asked; Classify then walks the killer again, for its kind and its Pets stage.
    private static GameObject KillOwner(LivingBeingKilled kill)
    {
        if (kill == null)
            return null;

        GameObject killer = kill.Killer;
        return KindOf(killer, null, out _, out _) is BotCpuKind.Gamebot or BotCpuKind.Companion or
            BotCpuKind.Playerbot or BotCpuKind.Piloted or BotCpuKind.Player ? killer : kill.Killed;
    }

    // A client's owner (3.3): its player while the client IsPlaying (Playing or Linkdead) and the player is Active;
    // none while it loads (WorldEnter) or at the character screen, where it keeps its old player.
    private static GameObject ClientOwner(GameClient client)
    {
        GamePlayer player = client?.Player;
        return player != null && client.IsPlaying && player.ObjectState == GameObject.eObjectState.Active
            ? player
            : null;
    }
}
