using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DOL.Database;
using DOL.GS.Realm;
using DOL.GS.Styles;
using static System.FormattableString;

namespace DOL.GS.HearthDAoC.PlayerBots;

/// <summary>One level-matched bot's plan: its class's GameBot spec plan (BotSpec: the GameBot's own lines, caps and
/// ratios, and its weapon plan), with the variant a GameBot keeps for a stored bot (ChoosePersistentSpecialization),
/// and the player bots' own changes to its lines (PlayerBotKit.PlayerLines). Made once per slot (SlotState.Kit), so a
/// login retry keeps it.</summary>
public sealed class PlayerBotKitPlan
{
    public PlayerBotKitPlan(PlayerBotBuild build, eSpecType choice, BotSpec spec)
    {
        Build = build;
        Choice = choice;
        Spec = spec;
        Lines = PlayerBotKit.PlayerLines(spec.SpecType, spec.SpecLines)
            .OrderByDescending(line => line.levelRatio).ToArray();                       // GameBot.cs:3312, a stable sort
    }
    public PlayerBotBuild Build { get; }
    public eSpecType Choice { get; }
    public BotSpec Spec { get; }
    public IReadOnlyList<BotSpecLine> Lines { get; }
}

// HearthDAoC #137 level-matched run (levelmatch/DESIGN.md 2.2): a player bot made at one GameBot's level, class and
// race. The bot is created as a valid level-1 character (PlayerBotAccounts.EnsureCharacter); after the CharacterCreated
// handlers and before the first save, ApplyToRow raises the row: the advanced class, level N with the experience the
// starting_level property gives (CharacterCreationSettings.cs:128-133), the stats at N
// (BotAttributeProgression.Calculate, the formula CharacterStatValidator allows), and the specs a GameBot trained level
// by level has at N (a port of GameBot.SpendSpecPoints, GameBot.cs:3307-3386), with the half levels' points a player
// gets from 41 on (#167 F13; REVIEW-FIXES 5 left them out, as an autonomous GameBot's training does): VerifySpecPoints'
// whole budget (GamePlayer.cs:10078-10113), never more, so the login never respecs. The login reads them all (GamePlayer.cs:10120-10165). TrainTo raises the specs on each level-up. Spec keys
// match case-blind, as GetSpecializationByName does (GamePlayer.cs:3130-3142), and are written as the class's own
// KeyName: the login's SkillBase.GetSpecialization is case-sensitive and fails on a key it does not know
// (SkillBase.cs:2606-2621).
public static class PlayerBotKit
{
    public static long Seed(PlayerBotSlot slot) => (long)slot.Realm * 1_000 + slot.Index;

    public static PlayerBotKitPlan Plan(PlayerBotBuild build, long seed)
    {
        eSpecType choice = BotSpec.ChoosePersistentSpecialization(build.Class, seed);   // BotSpec.cs:120-131
        BotSpec spec = BotSpec.GetSpec(build.Class, choice);                            // BotSpec.cs:28-77; weapon rolls are Util.Random
        return spec == null ? null : new(build, choice, spec);
    }

    /// <summary>The player bots' own changes to a GameBot plan's lines; the GameBot's plan stays as it is. #167 F13: a
    /// Suppression Spiritmaster's main line, Suppression to 49, has ratio 0 (Spiritmaster.cs:41-44, cases 4 and 5), so
    /// it stayed at 1 until 50 and the points went unspent; it gets 1.0, as the plan's case 6. #167 F10a: a
    /// Druid's pet is its Nature level's (Nature 7 a level-12 pet, 12 a level-20, 20 a level-32), and the plan gives a
    /// Nurture or Regrowth Druid Nature at ratio 0 (Druid.cs:32, :40, :47), so 1 until 50: a level-7 pet. Allocate
    /// raises the lines in ratio order and the first take the points (Nurture at 0.9 takes about 80% of them), so Nature
    /// comes first, at 0.6 to 20 (reached by 30), and the plan's two lines follow in their own order at 0.55 and 0.5,
    /// within their caps.</summary>
    public static IEnumerable<BotSpecLine> PlayerLines(eSpecType type, IEnumerable<BotSpecLine> lines)
    {
        if (type is eSpecType.SuppSpirit)
            return lines.Select(l => string.Equals(l.Spec, DOL.GS.Specs.Suppression, StringComparison.OrdinalIgnoreCase) &&
                l.levelRatio <= 0 ? new BotSpecLine(l.Spec, l.SpecCap, 1f) : l).ToList();
        if (type is not (eSpecType.NurtureDruid or eSpecType.RegrowthDruid)) return lines;
        List<BotSpecLine> mine = [new(DOL.GS.Specs.Nature, DruidNatureCap, 0.6f)];
        float ratio = 0.55f;
        foreach (BotSpecLine line in lines.Where(l => !string.Equals(l.Spec, DOL.GS.Specs.Nature, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(l => l.levelRatio))
        {
            mine.Add(new(line.Spec, line.SpecCap, ratio));
            ratio -= 0.05f;
        }
        return mine;
    }

    private const uint DruidNatureCap = 20;                                             // the level-32 pet (#167 F10a)

    /// <summary>GameBot.GetSpecPointsForLevel (GameBot.cs:3374-3386). With botCreation the half levels from 41 on.</summary>
    public static int SpecPointsForLevel(int level, int multiplier, bool botCreation)
    {
        int points = level > 5 ? multiplier * level / 10 : level >= 2 ? level : 0;
        if (botCreation && level > 40) points += multiplier * (level - 1) / 20;
        return points;
    }

    /// <summary>All points to level N, half levels too: VerifySpecPoints' total at the start of N (GamePlayer.cs:10081-10086).</summary>
    public static int Budget(int level, int multiplier)
    {
        int sum = 0;
        for (int i = 2; i <= level; i++) sum += SpecPointsForLevel(i, multiplier, true);
        return sum;
    }

    public static int Cost(int specLevel) => (specLevel * (specLevel + 1) - 2) / 2;      // GamePlayer.cs:10093

    /// <summary>GameBot.SpendSpecPoints (GameBot.cs:3307-3370) for a bot trained level by level up to N, with a player's
    /// half levels from 41 on (#167 F13: an autonomous GameBot's training, GameBot.cs:3068, has none, and left them
    /// unspent), over the specs open to the class at N (a GameBot's GetSpecializationByName finds only those, :3335).
    /// Every open spec starts at 1; the keys are open's own.</summary>
    public static Dictionary<string, int> Allocate(IEnumerable<BotSpecLine> plan, ICollection<string> open, int level,
        int multiplier, out int unspent)
    {
        BotSpecLine[] lines = plan.OrderByDescending(line => line.levelRatio).ToArray();
        var specs = open.ToDictionary(key => key, _ => 1, StringComparer.OrdinalIgnoreCase);
        int left = 0;
        for (int i = 2; i <= level; i++)
        {
            int points = SpecPointsForLevel(i, multiplier, true) + left;                // the half levels too (#167 F13)
            bool spent = true, leftOver = false;
            while (spent)
            {
                spent = false;
                foreach (BotSpecLine line in lines)
                {
                    if (line.levelRatio <= 0 && level < 50 || line.Spec == null || !specs.TryGetValue(line.Spec, out int now))
                        continue;
                    if (now >= line.SpecCap || now >= i)
                        continue;
                    if (!leftOver && now >= (int)(i * line.levelRatio))
                        continue;
                    if (points < now + 1)
                        continue;
                    points -= now + 1;
                    specs[line.Spec] = now + 1;                                        // keeps open's key
                    spent = true;
                }
                if (!spent && !leftOver)
                {
                    leftOver = true;
                    spent = true;
                }
            }
            left = points;
        }
        unspent = left;
        return specs;
    }

    /// <summary>The plan's lines the class never opens (not in its career at any level): skipped by Allocate, as a
    /// GameBot skips a line it does not find, and logged by the spawner. "-" for a line with no spec.</summary>
    public static string[] UnknownSpecs(IEnumerable<BotSpecLine> plan, ICollection<string> career) =>
        plan.Select(line => line.Spec ?? "-").Where(spec => !career.Contains(spec)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>The next levels after a level-up: each line toward its target, one level at a time in plan order, never
    /// lowered, and never past the free points (VerifySpecPoints respecs an overspender, GamePlayer.cs:10099-10107).</summary>
    public static Dictionary<string, int> Retrain(IReadOnlyDictionary<string, int> current,
        IReadOnlyDictionary<string, int> target, IEnumerable<string> order, int free)
    {
        var next = new Dictionary<string, int>(current, StringComparer.OrdinalIgnoreCase);
        var want = new Dictionary<string, int>(target, StringComparer.OrdinalIgnoreCase);
        string[] keys = order.Where(key => key != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool raised = true;
        while (raised)
        {
            raised = false;
            foreach (string key in keys)
            {
                if (!next.TryGetValue(key, out int now) || !want.TryGetValue(key, out int to) || now >= to || free < now + 1)
                    continue;
                free -= now + 1;
                next[key] = now + 1;                                                   // keeps current's key
                raised = true;
            }
        }
        return next;
    }

    /// <summary>The class's trainable, saved specs it has at this level (SkillBase.GetSpecializationCareer,
    /// gameutils/SkillBase.cs:2660-2700: spec and level required), by their KeyName.</summary>
    public static HashSet<string> OpenSpecs(int classId, int level) =>
        SkillBase.GetSpecializationCareer(classId)
            .Where(kv => kv.Key.Trainable && kv.Key.AllowSave && kv.Value <= level)
            .Select(kv => kv.Key.KeyName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The race's stats, +10 on each of the class's primary, secondary and tertiary (30 creation points) and the
    /// level growth: what CharacterStatValidator.Validate accepts at that level.</summary>
    public static Dictionary<eStat, int> Stats(eRace race, int level, eStat primary, eStat secondary, eStat tertiary) =>
        GlobalConstants.STARTING_STATS_DICT[race].ToDictionary(kv => kv.Key,
            kv => BotAttributeProgression.Calculate(kv.Value, level, kv.Key, primary, secondary, tertiary, advanced: true));

    public static void SetStats(DbCoreCharacter ch, IReadOnlyDictionary<eStat, int> s)
    {
        ch.Strength = s[eStat.STR]; ch.Dexterity = s[eStat.DEX]; ch.Constitution = s[eStat.CON]; ch.Quickness = s[eStat.QUI];
        ch.Intelligence = s[eStat.INT]; ch.Piety = s[eStat.PIE]; ch.Empathy = s[eStat.EMP]; ch.Charisma = s[eStat.CHR];
    }

    /// <summary>The row's gender (0 male, 1 female; CharacterCreateRequestHandler.cs:1006): the class's or the race's
    /// constraint (GlobalConstants.cs:1019-1033), else male.</summary>
    public static int RowGender(eRace race, eCharacterClass cls) =>
        (GlobalConstants.CLASS_GENDER_CONSTRAINTS_DICT.TryGetValue(cls, out eGender g) ||
         GlobalConstants.RACE_GENDER_CONSTRAINTS_DICT.TryGetValue(race, out g)) && g == eGender.Female ? 1 : 0;

    public static PlayerRace RaceOf(eRace race) =>
        PlayerRace.AllRaces.FirstOrDefault(r => r.ID == race) ?? throw new ArgumentOutOfRangeException(nameof(race));

    /// <summary>After the CharacterCreated handlers (StartAsBaseClass reverts the class, StartAsBaseClass.cs:36-40) and
    /// before the save: the advanced class, level N and its experience, the stats at N, and the specs.</summary>
    public static void ApplyToRow(DbCoreCharacter ch, PlayerBotKitPlan kit, ICharacterClass cls, ICollection<string> open)
    {
        byte level = kit.Build.Level;
        ch.Class = (int)kit.Build.Class;
        ch.Level = level;
        ch.Experience = GamePlayer.GetExperienceAmountForLevel(level - 1);            // CharacterCreationSettings.cs:130-131
        SetStats(ch, Stats(kit.Build.Race, level, cls.PrimaryStat, cls.SecondaryStat, cls.TertiaryStat));
        ch.SerializedSpecs = SerializeSpecs(Allocate(kit.Lines, open, level, cls.SpecPointsMultiplier, out _));
    }

    /// <summary>The string GamePlayer saves and reads (GamePlayer.cs:9946; :10325-10345): "Key|Level;Key|Level".</summary>
    public static string SerializeSpecs(IReadOnlyDictionary<string, int> specs) =>
        string.Join(";", specs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Invariant($"{kv.Key}|{kv.Value}")));

    /// <summary>On the game loop (the driver's think) after a level-up: the lines toward the plan at the new level, as
    /// /train does (train.cs:175-192: the spec, the class's hook, RefreshSpecDependantSkills, SendUpdatePlayerSkills(true)),
    /// within the free points (SkillSpecialtyPoints, GamePlayer.cs:3538-3541). Returns the lines raised.</summary>
    public static int TrainTo(GamePlayer player, PlayerBotKitPlan kit, out string line)
    {
        int level = player.Level, free = player.SkillSpecialtyPoints, spells = Spells(player), styles = Styles(player);
        Dictionary<string, int> target = Allocate(kit.Lines, OpenSpecs(player.CharacterClass.ID, level), level,
            player.CharacterClass.SpecPointsMultiplier, out _);
        Dictionary<string, int> current = Specs(player);
        int raised = 0;
        foreach ((string key, int to) in Retrain(current, target, kit.Lines.Select(l => l.Spec), free))
        {
            if (to <= current[key] || player.GetSpecializationByName(key) is not Specialization spec)
                continue;
            spec.Level = to;
            player.CharacterClass.OnSkillTrained(player, spec);
            raised++;
        }
        if (raised > 0)
        {
            player.RefreshSpecDependantSkills(false);
            player.Out.SendUpdatePlayerSkills(true);                                       // HeadlessPacketLib: the caches (G1)
        }
        line = string.Create(CultureInfo.InvariantCulture, $"PLAYER_BOT_RETRAIN bot={player.Name} level={level} raised={raised} " +
                             $"free={free} spells={spells}->{Spells(player)} styles={styles}->{Styles(player)}");
        return raised;
    }

    /// <summary>A list caster's spells (GetAllUsableListSpells) and a hybrid's (GetAllUsableSkills' Spell entries,
    /// GamePlayerUtils.cs:544-560; REVIEW-FIXES 19).</summary>
    public static int Spells(GamePlayer player) =>
        player.GetAllUsableListSpells().Sum(entry => entry.Item2.Count) + player.GetAllUsableSkills().Count(entry => entry.Item1 is Spell);

    public static int Styles(GamePlayer player) => player.GetAllUsableSkills().Count(entry => entry.Item1 is Style);

    public static Dictionary<string, int> Specs(GamePlayer player) =>
        player.GetSpecList().Where(s => s.Trainable).ToDictionary(s => s.KeyName, s => s.Level, StringComparer.OrdinalIgnoreCase);

    /// <summary>PLAYER_BOT_KIT, once per bot at its first entered world, after the gear: what the login made of the row
    /// (level, class, race, the specs it loaded, the skills they give) beside the spot's build and the plan's caps
    /// (REVIEW-FIXES 6).</summary>
    public static string Format(string account, GamePlayer player, PlayerBotKitPlan kit, PlayerBotKitResult gear) =>
        Format(account, player.Name, kit, player.Level, player.CharacterClass.ID, (int)player.Race, player.CurrentRegionID,
            Specs(player), Budget(player.Level, player.CharacterClass.SpecPointsMultiplier), Styles(player), Spells(player),
            PlayerBotDriver.BestDamageSpellName(player), gear);

    public static string Format(string account, string name, PlayerBotKitPlan kit, int level, int cls, int race, int region,
        IReadOnlyDictionary<string, int> specs, int budget, int styles, int spells, string damageSpell, PlayerBotKitResult gear)
    {
        string plan = string.Join(",", kit.Lines.Where(l => l.Spec != null)
            .Select(l => Invariant($"{l.Spec.Replace(' ', '_')}:{l.SpecCap}")));
        string raised = string.Join(",", specs.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Invariant($"{kv.Key.Replace(' ', '_')}:{kv.Value}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_KIT account={account} name={name} region={region} spotLevel={kit.Build.Level} level={level} " +
            $"spotClass={(int)kit.Build.Class} class={cls} spotRace={(int)kit.Build.Race} race={race} choice={kit.Choice} " +
            $"plan={(plan.Length == 0 ? "-" : plan)} points={specs.Values.Sum(Cost)} budget={budget} " +
            $"specs={(raised.Length == 0 ? "-" : raised)} styles={styles} spells={spells} damageSpell={damageSpell} " +
            $"armourLevel={gear.ArmourLevel} armour={gear.Armour} weapons={gear.Weapons} shield={(gear.Shield ? 1 : 0)} " +
            $"missing={gear.Missing} weapon={gear.Weapon} active={gear.Active}");
    }
}
