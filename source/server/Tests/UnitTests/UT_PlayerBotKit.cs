using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.PacketHandler.Client.v168;
using DOL.GS.Realm;
using DOL.GS.ServerRules;
using DOL.GS.Styles;
using DOL.UnitTests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC player bots (#137 level-matched run, levelmatch/DESIGN.md): a bot made at a GameBot's level, class and race. The
// allocator against GameBot.SpendSpecPoints' numbers (with a player's half levels from 41 on, #167 F13), the kit's spare
// points (KitSpecs) and VerifySpecPoints' budget, the stats against CharacterStatValidator, gender and race, the specs string and its keys,
// the retrain, the skill counts, the creation row, the gear rules and the KIT line. No server and no database (an empty one
// for GameObject's type initializer, as UT_PlayerBotDriver.cs:26-38, and SkillBase's property names,
// PetTestLanguageScope): OpenSpecs (SkillBase's career), EnsureCharacter's database steps and the CharacterCreated
// handlers, TrainTo, PlayerBotKitGear.Load/Equip and the login are left to the smoke leg.
[TestFixture, NonParallelizable]
public sealed class UT_PlayerBotKit
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private sealed class TestServer : GameServer
    {
        protected override IObjectDatabase DataBaseImpl => EmptyDatabase;
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
    }
    private static readonly IObjectDatabase EmptyDatabase =
        DispatchProxy.Create<IObjectDatabase, UT_AutonomousLootFlow.EmptyReadDatabase>();
    private GameServer _previousServer;
    private PetTestLanguageScope _language;
    [SetUp] public void SetUp()
    {
        _previousServer = GameServer.Instance;
        _language = new PetTestLanguageScope();
        GameServer.LoadTestDouble((TestServer)RuntimeHelpers.GetUninitializedObject(typeof(TestServer)));
    }
    [TearDown] public void TearDown()
    {
        _language.Dispose();
        GameServer.LoadTestDouble(_previousServer);
    }

    private static readonly BotSpecLine[] Armsman =
        [new(Specs.Slash, 50, 1.0f), new(Specs.Shields, 50, 1.0f), new(Specs.Parry, 18, 0.2f), new(Specs.Crossbow, 25, 0.1f)];
    private static readonly BotSpecLine[] FireWizard = [new(Specs.Cold_Magic, 20, 0.1f), new(Specs.Fire_Magic, 50, 1.0f)];

    private static Dictionary<string, int> Run(BotSpecLine[] lines, int level, int multiplier, out int unspent) =>
        PlayerBotKit.Allocate(lines, lines.Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase), level, multiplier, out unspent);

    // GamePlayer.VerifySpecPoints' total (GamePlayer.cs:10081-10086) at the start of a level
    private static int VerifyTotal(int level, int multiplier)
    {
        int all = -1;
        for (int i = 1; i <= level; i++)
        {
            if (i <= 5) all += i;
            if (i > 5) all += multiplier * i / 10;
            if (i > 40) all += multiplier * (i - 1) / 20;
        }
        return all;
    }

    private static IEnumerable<ICharacterClass> AdvancedClasses() =>
        typeof(GamePlayer).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(ICharacterClass).IsAssignableFrom(t) && t.GetCustomAttributes<CharacterClassAttribute>(false).Any())
            .Select(t => (ICharacterClass)Activator.CreateInstance(t))
            .Where(c => c.HasAdvancedFromBaseClass());

    // The 39 classes of the owner's level export (gamebot-levels.txt), each with a BotSpec plan (BotSpec.cs:31-74).
    private static readonly eCharacterClass[] GameBotClasses =
    [
        eCharacterClass.Armsman, eCharacterClass.Cabalist, eCharacterClass.Cleric, eCharacterClass.Friar, eCharacterClass.Infiltrator,
        eCharacterClass.Mercenary, eCharacterClass.Minstrel, eCharacterClass.Paladin, eCharacterClass.Reaver, eCharacterClass.Scout,
        eCharacterClass.Sorcerer, eCharacterClass.Theurgist, eCharacterClass.Wizard, eCharacterClass.Necromancer,
        eCharacterClass.Bard, eCharacterClass.Blademaster, eCharacterClass.Champion, eCharacterClass.Druid, eCharacterClass.Eldritch,
        eCharacterClass.Enchanter, eCharacterClass.Hero, eCharacterClass.Mentalist, eCharacterClass.Nightshade, eCharacterClass.Animist,
        eCharacterClass.Ranger, eCharacterClass.Valewalker, eCharacterClass.Warden,
        eCharacterClass.Berserker, eCharacterClass.Bonedancer, eCharacterClass.Healer, eCharacterClass.Hunter, eCharacterClass.Runemaster,
        eCharacterClass.Savage, eCharacterClass.Shadowblade, eCharacterClass.Shaman, eCharacterClass.Skald, eCharacterClass.Spiritmaster,
        eCharacterClass.Thane, eCharacterClass.Warrior,
    ];

    [Test]
    public void The_budget_is_what_VerifySpecPoints_allows([Values(10, 15, 20, 25)] int multiplier)
    {
        for (int level = 1; level <= 50; level++)
            Assert.That(PlayerBotKit.Budget(level, multiplier), Is.EqualTo(VerifyTotal(level, multiplier)), $"level {level}");
    }

    // From 41 on the half levels' points too (#167 F13): 45 and 47 were (45, 42, 11, 9, 9) and (47, 44, 12, 9, 3).
    [TestCase(20, 20, 17, 6, 6, 3)] [TestCase(30, 30, 27, 9, 7, 2)] [TestCase(40, 40, 37, 10, 9, 5)]
    [TestCase(45, 45, 45, 15, 12, 0)] [TestCase(47, 47, 47, 17, 16, 0)]
    public void An_armsman_spends_as_a_gamebot_trained_level_by_level(int level, int slash, int shields, int parry, int bows, int unspent)
    {
        Dictionary<string, int> specs = Run(Armsman, level, 20, out int left);
        Assert.That((specs[Specs.Slash], specs[Specs.Shields], specs[Specs.Parry], specs[Specs.Crossbow], left),
            Is.EqualTo((slash, shields, parry, bows, unspent)));
        Assert.That(specs.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(PlayerBotKit.Budget(level, 20)));   // never respecced
    }

    private static HashSet<string> Open(PlayerBotKitPlan kit) =>
        kit.Lines.Where(l => l.Spec != null).Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int Multiplier(eCharacterClass cls) => ScriptMgr.FindCharacterClass((int)cls).SpecPointsMultiplier;

    // #167 F13: a level-45 kit spends its whole budget, half levels too (they were left unspent: Allocate passed
    // botCreation false): what is left buys no rank on any plan line below its cap and the level, one a GameBot leaves at
    // 1 until 50 (ratio 0) too, but for what the plan's other lines take at a later level below 50 (KitSpecs' spare:
    // the least Allocate leaves at 45 to 49). Allocate alone left a Warrior's two-hander plan 196 points (Axe and Parry
    // at 45, Thrown Weapons at ratio 0) and every Bonedancer its half levels' 104 (its secondary lines at ratio 0). The
    // plan's other lines keep Allocate's levels.
    [Test]
    public void A_level_45_kit_spends_its_full_budget()
    {
        foreach (eCharacterClass cls in GameBotClasses)
        {
            int multiplier = Multiplier(cls);
            for (long seed = 1; seed <= 40; seed++)
            {
                PlayerBotKitPlan kit = PlayerBotKit.Plan(new PlayerBotBuild(45, cls, eRace.Briton), seed);
                HashSet<string> open = Open(kit);
                Dictionary<string, int> specs = PlayerBotKit.KitSpecs(kit.Lines, open, 45, multiplier, out int left);
                Dictionary<string, int> allocated = PlayerBotKit.Allocate(kit.Lines, open, 45, multiplier, out int allocatedLeft);
                int held = Enumerable.Range(45, 5).Min(level =>
                {
                    PlayerBotKit.Allocate(kit.Lines, open, level, multiplier, out int unspent);
                    return unspent;
                });
                int spare = held - (allocatedLeft - left);
                string at = $"{cls} {kit.Spec.SpecType} seed {seed}";
                Assert.That(specs.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(PlayerBotKit.Budget(45, multiplier)), at);
                foreach (BotSpecLine line in kit.Lines.Where(l => l.Spec != null))
                    Assert.That(specs[line.Spec] >= Math.Min(45, line.SpecCap) ||
                        (line.levelRatio > 0 ? left : spare) < specs[line.Spec] + 1, Is.True,
                        $"{at}: {line.Spec} (ratio {line.levelRatio}) at {specs[line.Spec]}, {left} left, {spare} spare");
                foreach (string spec in kit.Lines.Where(l => l.Spec != null).GroupBy(l => l.Spec)
                             .Where(g => g.All(l => l.levelRatio > 0)).Select(g => g.Key))
                    Assert.That(specs[spec], Is.EqualTo(allocated[spec]), $"{at}: {spec}");
                if (cls is eCharacterClass.Bonedancer)
                    Assert.That((allocatedLeft, left), Is.EqualTo((104, 0)), $"{at}: the half levels' points, spent");
            }
        }
        Dictionary<string, int> armsman = Run(Armsman, 45, 20, out int unspent);
        Assert.That(armsman.Values.Sum(PlayerBotKit.Cost), Is.EqualTo(PlayerBotKit.Budget(45, 20)).And.EqualTo(2_264));
        Assert.That(unspent, Is.Zero);
    }

    // The Warrior's two-hander plan (Warrior.cs:47-50: Axe 50 at 0.8, Parry 49 at 0.4, Thrown Weapons 21 at 0): at 45
    // Allocate leaves 196 points, which raise Thrown Weapons to 19 (Cost 189); at 30 its 16 raise it to 5. An Armsman
    // two-hander (Armsman.cs:73-81, Crossbows 3 at 0) has 18 left at 45, but its other lines take them all by 48
    // (Allocate leaves 0 there): spent on Crossbows, a main line would be a rank short at 48 (TrainTo never lowers
    // one), so they wait.
    [Test]
    public void A_kit_spends_what_its_plan_leaves_on_the_lines_a_gamebot_raises_at_50()
    {
        BotSpecLine[] warrior = [new(Specs.Axe, 50, 0.8f), new(Specs.Parry, 49, 0.4f), new(Specs.Thrown_Weapons, 21, 0f)];
        HashSet<string> open = warrior.Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> allocated = PlayerBotKit.Allocate(warrior, open, 45, 20, out int was);
        Dictionary<string, int> kit = PlayerBotKit.KitSpecs(warrior, open, 45, 20, out int left);
        Assert.That((allocated[Specs.Axe], allocated[Specs.Parry], allocated[Specs.Thrown_Weapons], was), Is.EqualTo((45, 45, 1, 196)));
        Assert.That((kit[Specs.Axe], kit[Specs.Parry], kit[Specs.Thrown_Weapons], left), Is.EqualTo((45, 45, 19, 7)));
        Dictionary<string, int> at30 = PlayerBotKit.KitSpecs(warrior, open, 30, 20, out int left30);
        Assert.That((at30[Specs.Axe], at30[Specs.Parry], at30[Specs.Thrown_Weapons], left30), Is.EqualTo((30, 29, 5, 2)));

        BotSpecLine[] armsman = [new(Specs.Two_Handed, 50, 1f), new(Specs.Slash, 39, 0.6f), new(Specs.Shields, 42, 0.5f),
            new(Specs.Parry, 18, 0.1f), new(Specs.Crossbow, 3, 0f)];
        open = armsman.Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.That((PlayerBotKit.KitSpecs(armsman, open, 45, 20, out left)[Specs.Crossbow], left), Is.EqualTo((1, 18)));
        PlayerBotKit.Allocate(armsman, open, 48, 20, out int at48);
        Assert.That(at48, Is.Zero);
    }

    // TrainTo raises the specs toward KitSpecs' and never lowers one (Retrain): a kit's specs never fall from one level
    // to the next, so the leftover spent on a ratio-0 line never holds back a main line later.
    [Test]
    public void A_kits_specs_never_fall_from_one_level_to_the_next()
    {
        foreach (eCharacterClass cls in GameBotClasses)
            for (long seed = 1; seed <= 8; seed++)
            {
                PlayerBotKitPlan kit = PlayerBotKit.Plan(new PlayerBotBuild(45, cls, eRace.Briton), seed);
                Dictionary<string, int> before = PlayerBotKit.KitSpecs(kit.Lines, Open(kit), 2, Multiplier(cls), out _);
                for (int level = 3; level <= 49; level++)
                {
                    Dictionary<string, int> now = PlayerBotKit.KitSpecs(kit.Lines, Open(kit), level, Multiplier(cls), out _);
                    foreach ((string spec, int was) in before)
                        Assert.That(now[spec], Is.GreaterThanOrEqualTo(was), $"{cls} {kit.Spec.SpecType} seed {seed} {spec} at {level}");
                    before = now;
                }
            }
    }

    // A Suppression Spiritmaster (Spiritmaster.cs:41-44, cases 4 and 5) has its main line, Suppression to 49, at ratio 0,
    // so it stayed at 1 until 50 and a level-35 bot left 377 points unspent. The player-bot plan gives it 1.0, as the
    // plan's case 6 has (#167 F13); the GameBot's plan is unchanged. The kits are the real plans (PlayerBotKit.Plan; the
    // case is a random roll within the choice, Spiritmaster.cs:13).
    [Test]
    public void A_suppression_spiritmasters_main_line_rises_from_the_start()
    {
        int multiplier = Multiplier(eCharacterClass.Spiritmaster), seen = 0;
        for (long seed = 1; seed <= 400; seed++)
        {
            PlayerBotKitPlan kit = PlayerBotKit.Plan(new PlayerBotBuild(35, eCharacterClass.Spiritmaster, eRace.Norseman), seed);
            if (kit.Choice is not eSpecType.SuppSpirit ||
                kit.Spec.SpecLines.Single(l => l.Spec == Specs.Suppression).levelRatio > 0)
                continue;                                                               // cases 6 and 7 rise anyway
            seen++;
            Assert.That(kit.Lines.Single(l => l.Spec == Specs.Suppression).levelRatio, Is.EqualTo(1f), $"seed {seed}");
            Dictionary<string, int> at35 = PlayerBotKit.KitSpecs(kit.Lines, Open(kit), 35, multiplier, out int left);
            Assert.That((at35[Specs.Suppression], left), Is.EqualTo((35, 0)), $"seed {seed}");
            Assert.That(PlayerBotKit.Allocate(kit.Spec.SpecLines, Open(kit), 35, multiplier, out int was)[Specs.Suppression],
                Is.EqualTo(1), "the GameBot's plan");
            Assert.That(was, Is.EqualTo(377));
        }
        Assert.That(seen, Is.GreaterThan(0), "a Suppression Spiritmaster of case 4 or 5");
        BotSpecLine[] dark = [new(Specs.Darkness, 47, 1f), new(Specs.Suppression, 5, 0f), new(Specs.Summoning, 26, 0.1f)];
        Assert.That(PlayerBotKit.PlayerLines(eSpecType.DarkSpirit, dark), Is.EqualTo(dark));   // a minor line keeps its 0
    }

    // #167 E (CY2): every Warden plays the Battle Warden plan, its weapon line at 0.7 (Warden.cs:44-50), whatever the
    // GameBot's choice for the seed (ChoosePersistentSpecialization); other classes keep theirs.
    [Test]
    public void A_warden_plays_the_battle_warden_plan()
    {
        int other = 0;
        for (long seed = 1; seed <= 60; seed++)
        {
            if (BotSpec.ChoosePersistentSpecialization(eCharacterClass.Warden, seed) != eSpecType.BattleWarden) other++;
            PlayerBotKitPlan kit = PlayerBotKit.Plan(new PlayerBotBuild(33, eCharacterClass.Warden, eRace.Celt), seed);
            Assert.That((kit.Choice, kit.Spec.SpecType), Is.EqualTo((eSpecType.BattleWarden, eSpecType.BattleWarden)), $"seed {seed}");
            BotSpecLine weapon = kit.Lines.Single(l => l.Spec == SkillBase.ObjectTypeToSpec(kit.Spec.WeaponOneType));
            Assert.That((weapon.SpecCap, weapon.levelRatio), Is.EqualTo((39u, 0.7f)), $"seed {seed}");
        }
        Assert.That(other, Is.GreaterThan(0), "a seed the GameBot gives another Warden plan");
        Assert.That(PlayerBotKit.PlayerChoice(eCharacterClass.Druid, eSpecType.NurtureDruid), Is.EqualTo(eSpecType.NurtureDruid));
        Assert.That(PlayerBotKit.PlayerChoice(eCharacterClass.Warden, eSpecType.RegrowthWarden), Is.EqualTo(eSpecType.BattleWarden));
    }

    // At 47 the half levels' points (#167 F13) raise Cold to 16 (it was 1, nothing left over).
    [TestCase(30, 30, 1, 0)] [TestCase(47, 47, 16, 14)]
    public void A_fire_wizard_spends_as_a_gamebot_trained_level_by_level(int level, int fire, int cold, int unspent)
    {
        Dictionary<string, int> specs = Run(FireWizard, level, 10, out int left);
        Assert.That((specs[Specs.Fire_Magic], specs[Specs.Cold_Magic], left), Is.EqualTo((fire, cold, unspent)));
    }

    [Test]
    public void The_half_levels_count_only_in_the_budget()       // GameBot.cs:3374-3386 with and without botCreation
    {
        Assert.That((PlayerBotKit.SpecPointsForLevel(1, 20, false), PlayerBotKit.SpecPointsForLevel(4, 20, true)), Is.EqualTo((0, 4)));
        Assert.That((PlayerBotKit.SpecPointsForLevel(40, 20, true), PlayerBotKit.SpecPointsForLevel(41, 20, false)), Is.EqualTo((80, 82)));
        Assert.That(PlayerBotKit.SpecPointsForLevel(41, 20, true), Is.EqualTo(82 + 40));
    }

    [Test]
    public void Every_gamebot_plan_spends_within_its_budget_its_level_and_its_caps()
    {
        foreach (eCharacterClass cls in Enum.GetValues<eCharacterClass>())
        foreach (eSpecType choice in BotSpec.GetSpecializationChoices(cls))
        {
            BotSpec spec = BotSpec.GetSpec(cls, choice);
            if (spec == null) continue;
            HashSet<string> open = spec.SpecLines.Where(l => l.Spec != null).Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (int level in new[] { 20, 25, 30, 35, 40, 41, 45, 47 })
            {
                Dictionary<string, int> specs = PlayerBotKit.Allocate(spec.SpecLines, open, level, 20, out int left);
                Assert.That(specs.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(PlayerBotKit.Budget(level, 20)), $"{cls} {choice} {level}");
                foreach (BotSpecLine line in spec.SpecLines.Where(l => l.Spec != null))
                    Assert.That(specs[line.Spec], Is.InRange(1, Math.Min(level, (int)spec.SpecLines.Where(l => l.Spec == line.Spec).Max(l => l.SpecCap))),
                        $"{cls} {choice} {level} {line.Spec}");
            }
        }
    }

    [Test]
    public void A_line_with_no_ratio_waits_for_50_and_a_closed_line_is_never_raised()
    {
        BotSpecLine[] lines = [new("A", 50, 1.0f), new("B", 50, 0f), new("C", 50, 0.5f)];
        var open = new HashSet<string> { "A", "B" };
        Dictionary<string, int> at30 = PlayerBotKit.Allocate(lines, open, 30, 20, out _);
        Assert.That((at30["B"], at30.ContainsKey("C")), Is.EqualTo((1, false)));
        Assert.That(PlayerBotKit.Allocate(lines, open, 50, 20, out _)["B"], Is.GreaterThan(1));
    }

    [Test]
    public void A_plan_line_in_another_case_raises_the_classs_own_spec()   // REVIEW-FIXES 20: GetSpecializationByName is case-blind
    {
        BotSpecLine[] lines = [new("slash", 50, 1.0f), new("SHIELDS", 50, 1.0f)];
        var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Specs.Slash, Specs.Shields, Specs.Parry };
        Dictionary<string, int> specs = PlayerBotKit.Allocate(lines, open, 20, 20, out _);
        Assert.That(specs.Keys, Is.EquivalentTo(new[] { "Slash", "Shields", "Parry" }));   // the KeyNames the login finds
        Assert.That((specs["Slash"], specs["Parry"]), Is.EqualTo((20, 1)));
        Assert.That(PlayerBotKit.SerializeSpecs(specs), Does.StartWith("Parry|1;Shields|").And.Contain(";Slash|20"));
        Assert.That(PlayerBotKit.UnknownSpecs([new("slash", 50, 1f), new("Witchcraft", 50, 1f), new(null, 50, 1f)], open),
            Is.EqualTo(new[] { "Witchcraft", "-" }));
    }

    [Test]
    public void Retrain_only_raises_toward_the_plan_and_never_past_the_free_points()
    {
        var current = new Dictionary<string, int> { ["A"] = 10, ["B"] = 5, ["C"] = 3 };
        var target = new Dictionary<string, int> { ["A"] = 12, ["B"] = 4, ["C"] = 4 };
        Assert.That(PlayerBotKit.Retrain(current, target, ["A", "B", "C"], 27),
            Is.EqualTo(new Dictionary<string, int> { ["A"] = 12, ["B"] = 5, ["C"] = 4 }));   // 11 + 4 + 12
        Assert.That(PlayerBotKit.Retrain(current, target, ["A", "B", "C"], 15),
            Is.EqualTo(new Dictionary<string, int> { ["A"] = 11, ["B"] = 5, ["C"] = 4 }));   // 11 + 4, then 12 > 0
        Assert.That(PlayerBotKit.Retrain(current, target, ["A", "B", "C"], 0), Is.EqualTo(current));
        Assert.That(PlayerBotKit.Retrain(current, target, ["a", "c"], 27)["A"], Is.EqualTo(12));             // REVIEW-FIXES 20
    }

    [Test]
    public void Stats_at_any_level_are_a_valid_30_point_character_of_that_level()
    {
        ICharacterClass[] classes = AdvancedClasses().ToArray();
        Assert.That(classes.Select(c => (eCharacterClass)c.ID), Is.SupersetOf(GameBotClasses));
        foreach (ICharacterClass cls in classes)
        foreach (PlayerRace race in cls.EligibleRaces.Where(r => GlobalConstants.STARTING_STATS_DICT.ContainsKey(r.ID)))
        foreach (int level in new[] { 1, 5, 6, 20, 35, 47, 50 })
        {
            Dictionary<eStat, int> stats = PlayerBotKit.Stats(race.ID, level, cls.PrimaryStat, cls.SecondaryStat, cls.TertiaryStat);
            bool valid = CharacterStatValidator.Validate(stats, GlobalConstants.STARTING_STATS_DICT[race.ID], cls.PrimaryStat,
                cls.SecondaryStat, cls.TertiaryStat, level, out int distributed);
            Assert.That((valid, distributed), Is.EqualTo((true, 30)), $"{cls.Name} {race.ID} {level}");
        }
    }

    [TestCase(eRace.Briton, eCharacterClass.Armsman, 0)] [TestCase(eRace.AlbionMinotaur, eCharacterClass.Armsman, 0)]
    [TestCase(eRace.Norseman, eCharacterClass.Valkyrie, 1)] [TestCase(eRace.Elf, eCharacterClass.Bainshee, 1)]
    public void The_row_gender_follows_the_constraints_else_male(eRace race, eCharacterClass cls, int gender) =>
        Assert.That(PlayerBotKit.RowGender(race, cls), Is.EqualTo(gender));

    [Test]
    public void Every_race_of_the_export_has_its_model()
    {
        for (int id = 1; id <= 15; id++)                                    // gamebot-levels.txt: races 1-15
            Assert.That(PlayerBotKit.RaceOf((eRace)id).ID, Is.EqualTo((eRace)id));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerBotKit.RaceOf(eRace.Unknown));
    }

    [Test]
    public void The_specs_string_reads_back_as_the_login_reads_it()   // GamePlayer.cs:10325-10345
    {
        string s = PlayerBotKit.SerializeSpecs(new Dictionary<string, int> { [Specs.Slash] = 20, [Specs.Fire_Magic] = 1 });
        Assert.That(s, Is.EqualTo("Fire Magic|1;Slash|20"));
        Assert.That(Util.SplitCSV(s).Select(e => e.Split('|')).ToDictionary(p => p[0], p => int.Parse(p[1])),
            Is.EqualTo(new Dictionary<string, int> { ["Fire Magic"] = 1, ["Slash"] = 20 }));
    }

    // #167 F10a: a Druid's pet is its Nature level's (Nature 7 a level-12 pet, 12 a level-20, 20 a level-32). The GameBot
    // plan gives a Nurture or Regrowth Druid Nature at ratio 0 (Druid.cs:32, :40, :47), so 1 until 50: a level-7 pet.
    [Test]
    public void A_nurture_or_regrowth_druid_kit_trains_nature_for_a_pet_near_its_level()
    {
        var build = new PlayerBotBuild(35, eCharacterClass.Druid, eRace.Celt);
        var seen = new HashSet<eSpecType>();
        for (long seed = 1; seed <= 300; seed++)
        {
            PlayerBotKitPlan kit = PlayerBotKit.Plan(build, seed);
            if (kit.Spec.SpecType is not (eSpecType.NurtureDruid or eSpecType.RegrowthDruid)) continue;
            seen.Add(kit.Spec.SpecType);
            HashSet<string> open = kit.Lines.Select(l => l.Spec).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> at35 = PlayerBotKit.Allocate(kit.Lines, open, 35, 10, out int left);
            Assert.That(at35[Specs.Nature], Is.GreaterThanOrEqualTo(20), $"{seed} {kit.Spec.SpecType}");      // the level-32 pet
            Assert.That(at35.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(PlayerBotKit.Budget(35, 10)));
            Assert.That(PlayerBotKit.Allocate(kit.Lines, open, 20, 10, out _)[Specs.Nature], Is.GreaterThanOrEqualTo(12));  // level 20
        }
        Assert.That(seen, Is.EquivalentTo(new[] { eSpecType.NurtureDruid, eSpecType.RegrowthDruid }));
        foreach (eSpecType type in new[] { eSpecType.NurtureDruid, eSpecType.RegrowthDruid })           // the GameBot's own plan
            Assert.That(BotSpec.GetSpec(eCharacterClass.Druid, type).SpecLines.Single(l => l.Spec == Specs.Nature).levelRatio,
                Is.EqualTo(0f));
    }

    [Test]
    public void The_plan_is_the_same_for_the_same_slot_and_a_choice_of_the_class()
    {
        var build = new PlayerBotBuild(40, eCharacterClass.Wizard, eRace.Briton);
        for (long seed = 1; seed <= 50; seed++)
        {
            PlayerBotKitPlan a = PlayerBotKit.Plan(build, seed), b = PlayerBotKit.Plan(build, seed);
            Assert.That(a.Choice, Is.EqualTo(b.Choice));
            Assert.That(BotSpec.GetSpecializationChoices(eCharacterClass.Wizard), Does.Contain(a.Choice));
            Assert.That(a.Lines.Select(l => l.levelRatio), Is.Ordered.Descending);
        }
        IEnumerable<long> seeds = new[] { eRealm.Albion, eRealm.Midgard, eRealm.Hibernia }.SelectMany(r =>
            Enumerable.Range(1, PlayerBotPlan.MaxPerRealm).Select(i => PlayerBotKit.Seed(new PlayerBotSlot("x", r, i))));
        Assert.That(seeds.Distinct().Count(), Is.EqualTo(3 * PlayerBotPlan.MaxPerRealm));
        Assert.That(PlayerBotKit.Plan(new PlayerBotBuild(20, eCharacterClass.Fighter, eRace.Briton), 1), Is.Null);   // no GameBot plan
    }

    // A player made without its constructor, its skill caches set as GetAllUsableSkills / GetAllUsableListSpells hold them
    // (GamePlayer.cs:3394-3447; non-empty, so neither rebuilds).
    private static GamePlayer WithSkills(List<(Skill, Skill)> usable, List<(SpellLine, List<Skill>)> listSpells)
    {
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("_usableSkills", Hidden)!.SetValue(player, usable);
        typeof(GamePlayer).GetField("_usableListSpells", Hidden)!.SetValue(player, listSpells);
        return player;
    }

    private static Spell Spell(int id, string type = "DirectDamage") =>
        new(new DbSpell { SpellID = id, Name = "s" + id, Type = type, Target = "Enemy", Damage = 50, Range = 1500 }, 10);

    [Test]
    public void A_hybrids_spells_count_beside_a_list_casters()       // REVIEW-FIXES 19: a Paladin or Thane kit has spells > 0
    {
        var chants = new SpellLine("Chants", "Chants", Specs.Chants, false);
        var style = new Style(new DbStyle { Name = "slam", ID = 1, SpecKeyName = Specs.Slash, SpecLevelRequirement = 1 }, null);
        GamePlayer hybrid = WithSkills([(Spell(1), chants), (Spell(2), chants), (style, null)], [(chants, [])]);
        Assert.That((PlayerBotKit.Spells(hybrid), PlayerBotKit.Styles(hybrid)), Is.EqualTo((2, 1)));
        var fire = new SpellLine("Fire", "Fire", Specs.Fire_Magic, true);
        GamePlayer caster = WithSkills([(style, null)], [(fire, [Spell(3), Spell(4), Spell(5)])]);
        Assert.That((PlayerBotKit.Spells(caster), PlayerBotKit.Styles(caster)), Is.EqualTo((3, 1)));
    }

    // --- creation (levelmatch/DESIGN.md 2.4, DECISIONS 2) ---

    [Test]
    public void A_build_row_is_a_valid_level_1_character_then_the_build_after_the_creation_event()
    {
        int rows = 0;
        foreach (eCharacterClass cls in GameBotClasses)
        {
            eRealm realm = GlobalConstants.STARTING_CLASSES_DICT.First(kv => kv.Value.Contains(cls)).Key;
            var slot = new PlayerBotSlot("hdbot-x-01", realm, 1);
            foreach (PlayerRace race in ScriptMgr.FindCharacterClass((int)cls).EligibleRaces)
            {
                PlayerBotKitPlan kit = PlayerBotKit.Plan(new PlayerBotBuild(40, cls, race.ID), PlayerBotKit.Seed(slot));
                DbCoreCharacter ch = PlayerBotAccounts.NewRow(slot.AccountName, slot, kit, out ICharacterClass charClass);
                Assert.That((CharacterCreateRequestHandler.IsCharacterValid(ch), ch.Level, ch.Class, ch.Race),
                    Is.EqualTo((true, 1, (int)cls, (int)race.ID)), $"{cls} {race.ID}");   // what the creation checks see
                PlayerBotKit.ApplyToRow(ch, kit, charClass, kit.Lines.Where(l => l.Spec != null).Select(l => l.Spec)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
                Assert.That((ch.Class, ch.Level, ch.Experience), Is.EqualTo(((int)cls, 40, GamePlayer.GetExperienceAmountForLevel(39))));
                Assert.That((CharacterStatValidator.Validate(ch, out int points), points), Is.EqualTo((true, 30)), $"{cls} {race.ID} at 40");
                Assert.That(ch.SerializedSpecs, Does.Contain("|"), $"{cls} {race.ID}");
                rows++;
            }
        }
        Assert.That(rows, Is.GreaterThan(2 * GameBotClasses.Length));                       // every class, most with 3+ races
        var fighter = new PlayerBotKitPlan(new PlayerBotBuild(20, eCharacterClass.Fighter, eRace.Briton), eSpecType.None, new BotSpec());
        var alb = new PlayerBotSlot("hdbot-alb-02", eRealm.Albion, 2);
        Assert.That(PlayerBotAccounts.NewRow(alb.AccountName, alb, fighter, out _), Is.Null);   // a base class at 5 or more
        DbCoreCharacter roster = PlayerBotAccounts.NewRow(alb.AccountName, alb, null, out ICharacterClass none);
        Assert.That((roster.Race, roster.Class, roster.Gender, roster.Level, none),
            Is.EqualTo(((int)alb.Template.Race, (int)alb.Template.BaseClass, 0, 1, (ICharacterClass)null)));   // today's roster bot
        Assert.That(CharacterCreateRequestHandler.IsCharacterValid(roster), Is.True);
    }

    // --- gear (levelmatch/DESIGN.md 2.3) ---

    private static DbItemTemplate Item(string id, int level, int itemType, eObjectType type, int hand = 0, int damage = 0,
        string classes = "", int requirement = 0) =>
        new() { Id_nb = id, Name = id, Level = level, Item_Type = itemType, Object_Type = (int)type, Hand = hand,
                Type_Damage = damage, AllowedClasses = classes, LevelRequirement = requirement, Realm = 1, IsPickable = true };

    [Test]
    public void Pick_keeps_the_gamebot_window_and_gives_a_bot_the_same_item_each_time()
    {
        DbItemTemplate[] items = Enumerable.Range(1, 50).Select(l => Item("torso" + l, l, Slot.TORSO, eObjectType.Plate)).ToArray();
        for (long seed = 1; seed <= 200; seed++)
        {
            DbItemTemplate t = PlayerBotKitGear.Pick(items, 40, seed, Slot.TORSO);
            Assert.That(t.Level, Is.InRange(34, 40));
            Assert.That(PlayerBotKitGear.Pick(items.Reverse(), 40, seed, Slot.TORSO), Is.SameAs(t));   // order-free
        }
        Assert.That(Enumerable.Range(1, 200).Select(s => PlayerBotKitGear.Pick(items, 40, s, Slot.TORSO).Level).Distinct().Count(), Is.EqualTo(7));
        Assert.That(PlayerBotKitGear.Pick(items.Where(i => i.Level < 30), 40, 1, Slot.TORSO), Is.Null);
    }

    [Test]
    public void Hands_and_classes_follow_the_gamebot_and_companion_rules()
    {
        var R = eInventorySlot.RightHandWeapon; var L = eInventorySlot.LeftHandWeapon; var T = eInventorySlot.TwoHandWeapon;
        Assert.That(PlayerBotKitGear.FitsHand(Item("a", 1, Slot.RIGHTHAND, eObjectType.SlashingWeapon), R, 0), Is.True);
        Assert.That(PlayerBotKitGear.FitsHand(Item("b", 1, Slot.LEFTHAND, eObjectType.SlashingWeapon), R, 0), Is.True);
        Assert.That(PlayerBotKitGear.FitsHand(Item("c", 1, Slot.LEFTHAND, eObjectType.LeftAxe, hand: 2), R, 0), Is.False);
        Assert.That(PlayerBotKitGear.FitsHand(Item("d", 1, Slot.RIGHTHAND, eObjectType.SlashingWeapon), L, 0), Is.False);
        Assert.That(PlayerBotKitGear.FitsHand(Item("e", 1, Slot.TWOHAND, eObjectType.PolearmWeapon, 1, (int)eWeaponDamageType.Thrust), T, eWeaponDamageType.Slash), Is.False);
        Assert.That(PlayerBotKitGear.FitsHand(Item("f", 1, Slot.TWOHAND, eObjectType.PolearmWeapon, 1, (int)eWeaponDamageType.Slash), T, eWeaponDamageType.Slash), Is.True);
        Assert.That(PlayerBotKitGear.FitsClass(Item("g", 30, Slot.TORSO, eObjectType.Plate, classes: "2;1"), 2, 30), Is.True);
        Assert.That(PlayerBotKitGear.FitsClass(Item("h", 30, Slot.TORSO, eObjectType.Plate, classes: "1"), 2, 30), Is.False);
        Assert.That(PlayerBotKitGear.FitsClass(Item("i", 30, Slot.TORSO, eObjectType.Plate, requirement: 35), 2, 30), Is.False);
    }

    // #167 E (CY1): a hand-to-hand claw (left-hand capable, Hand 2, as Midgard's 240 claws are) fits the right hand
    // too, as a GameBot Savage wears one (SavageBotWeaponPolicy); any other Hand-2 item, or a Hand-1 one, still
    // doesn't. With claws only, a Hand-to-Hand Savage's plan finds a weapon for both hands at every level.
    [Test]
    public void A_savage_wears_a_claw_in_its_right_hand()
    {
        var R = eInventorySlot.RightHandWeapon; var L = eInventorySlot.LeftHandWeapon;
        Assert.That(PlayerBotKitGear.FitsHand(Item("claw", 30, Slot.LEFTHAND, eObjectType.HandToHand, hand: 2), R, 0), Is.True);
        Assert.That(PlayerBotKitGear.FitsHand(Item("axe", 30, Slot.LEFTHAND, eObjectType.LeftAxe, hand: 2), R, 0), Is.False);
        Assert.That(PlayerBotKitGear.FitsHand(Item("greave", 30, Slot.LEFTHAND, eObjectType.HandToHand, hand: 1), R, 0), Is.False);
        IReadOnlyList<PlayerBotWeapon> plan = PlayerBotKitGear.WeaponPlan(Spec(eSpecType.DualWield, eObjectType.HandToHand),
            eCharacterClass.Savage, eClassType.PureTank);
        Assert.That(plan, Is.EqualTo(new[] { new PlayerBotWeapon(eObjectType.HandToHand, R), new PlayerBotWeapon(eObjectType.HandToHand, L) }));
        DbItemTemplate[] claws = Enumerable.Range(1, 50)
            .Select(l => Item("claw" + l, l, Slot.LEFTHAND, eObjectType.HandToHand, hand: 2)).ToArray();
        for (int level = 20; level <= 50; level++)
            foreach (PlayerBotWeapon w in plan)
                Assert.That(PlayerBotKitGear.Pick(claws.Where(c => PlayerBotKitGear.FitsHand(c, w.Slot, w.Damage)), level, 7, (int)w.Slot),
                    Is.Not.Null, $"level {level} {w.Slot}");
    }

    private static BotSpec Spec(eSpecType type, eObjectType w1, eObjectType w2 = 0, bool twoHanded = false, eWeaponDamageType damage = 0) =>
        new() { SpecType = type, WeaponOneType = w1, WeaponTwoType = w2, Is2H = twoHanded, DamageType = damage };

    [Test]
    public void The_weapon_plan_and_the_active_slot_are_the_gamebots()   // GameBot.cs:3964-4050
    {
        var R = eInventorySlot.RightHandWeapon; var L = eInventorySlot.LeftHandWeapon; var T = eInventorySlot.TwoHandWeapon;
        PlayerBotWeapon W(eObjectType t, eInventorySlot s, eWeaponDamageType d = 0) => new(t, s, d);
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.OneHandAndShield, eObjectType.SlashingWeapon), eCharacterClass.Armsman, eClassType.PureTank),
            Is.EqualTo(new[] { W(eObjectType.SlashingWeapon, R) }));
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.TwoHandHybrid, eObjectType.SlashingWeapon, eObjectType.PolearmWeapon, true, eWeaponDamageType.Slash),
            eCharacterClass.Armsman, eClassType.PureTank),
            Is.EqualTo(new[] { W(eObjectType.SlashingWeapon, R), W(eObjectType.PolearmWeapon, T, eWeaponDamageType.Slash) }));
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.DualWield, eObjectType.ThrustWeapon), eCharacterClass.Mercenary, eClassType.PureTank),
            Is.EqualTo(new[] { W(eObjectType.ThrustWeapon, R), W(eObjectType.ThrustWeapon, L) }));
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.LeftAxe, eObjectType.Axe, eObjectType.LeftAxe), eCharacterClass.Shadowblade, eClassType.PureTank),
            Is.EqualTo(new[] { W(eObjectType.Axe, R), W(eObjectType.Axe, T), W(eObjectType.LeftAxe, L) }));
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.FireWiz, eObjectType.Staff, twoHanded: true), eCharacterClass.Wizard, eClassType.ListCaster),
            Is.EqualTo(new[] { W(eObjectType.Staff, T) }));
        Assert.That(PlayerBotKitGear.WeaponPlan(Spec(eSpecType.MendHealer, eObjectType.Hammer), eCharacterClass.Healer, eClassType.Hybrid),
            Is.EqualTo(new[] { W(eObjectType.Hammer, R), W(eObjectType.Hammer, T) }));
        Assert.That(PlayerBotKitGear.ActiveSlot(Spec(eSpecType.TwoHandHybrid, eObjectType.SlashingWeapon, eObjectType.TwoHandedWeapon, true),
            eCharacterClass.Armsman, eClassType.PureTank, 9, true, true), Is.EqualTo(eActiveWeaponSlot.Standard));      // GameBot.cs:4064: 10
        Assert.That(PlayerBotKitGear.ActiveSlot(Spec(eSpecType.TwoHandHybrid, eObjectType.SlashingWeapon, eObjectType.TwoHandedWeapon, true),
            eCharacterClass.Armsman, eClassType.PureTank, 30, true, true), Is.EqualTo(eActiveWeaponSlot.TwoHanded));
        Assert.That(PlayerBotKitGear.ActiveSlot(Spec(eSpecType.FireWiz, eObjectType.Staff, twoHanded: true),
            eCharacterClass.Wizard, eClassType.ListCaster, 30, false, true), Is.EqualTo(eActiveWeaponSlot.TwoHanded));
        Assert.That(PlayerBotKitGear.ActiveSlot(Spec(eSpecType.FireWiz, eObjectType.Staff, twoHanded: true),
            eCharacterClass.Wizard, eClassType.ListCaster, 30, true, false), Is.EqualTo(eActiveWeaponSlot.Standard));   // no staff found
    }

    [Test]
    public void A_shield_is_the_shield_abilitys_size_in_a_free_left_hand_of_a_class_that_holds_one()   // REVIEW-FIXES 24
    {
        PlayerBotWeapon[] oneHand = [new(eObjectType.SlashingWeapon, eInventorySlot.RightHandWeapon)];
        PlayerBotWeapon[] dual = [.. oneHand, new(eObjectType.SlashingWeapon, eInventorySlot.LeftHandWeapon)];
        Assert.That(PlayerBotKitGear.ShieldSize(eCharacterClass.Armsman, 3, oneHand), Is.EqualTo(3));
        Assert.That(PlayerBotKitGear.ShieldSize(eCharacterClass.Armsman, 0, oneHand), Is.Zero);
        Assert.That(PlayerBotKitGear.ShieldSize(eCharacterClass.Mercenary, 1, dual), Is.Zero);
        foreach (eCharacterClass cls in new[] { eCharacterClass.Infiltrator, eCharacterClass.Mercenary, eCharacterClass.Nightshade,
                     eCharacterClass.Ranger, eCharacterClass.Blademaster, eCharacterClass.Shadowblade, eCharacterClass.Berserker,
                     eCharacterClass.Savage })                                      // BotEquipment.cs:512-522: the backpack
            Assert.That(PlayerBotKitGear.ShieldSize(cls, 2, oneHand), Is.Zero, cls.ToString());
    }

    [TestCase(eRealm.Albion, 3, eObjectType.Studded)] [TestCase(eRealm.Hibernia, 3, eObjectType.Reinforced)]
    [TestCase(eRealm.Midgard, 4, eObjectType.Chain)] [TestCase(eRealm.Hibernia, 4, eObjectType.Scale)]
    [TestCase(eRealm.Albion, 5, eObjectType.Plate)] [TestCase(eRealm.Albion, 1, eObjectType.Cloth)] [TestCase(eRealm.Albion, -1, (eObjectType)0)]
    public void The_armour_type_is_the_gamebots(eRealm realm, int level, eObjectType type) =>
        Assert.That(PlayerBotKitGear.ArmourType(realm, level), Is.EqualTo(type));

    [Test]
    public void KitFormat()
    {
        var spec = new BotSpec { SpecType = eSpecType.StaffFriar };
        spec.SpecLines.AddRange([new(Specs.Rejuvenation, 24, 0.5f), new(Specs.Staff, 50, 1.0f), new(Specs.Parry, 18, 0.1f)]);
        var kit = new PlayerBotKitPlan(new PlayerBotBuild(40, eCharacterClass.Friar, eRace.Briton), eSpecType.StaffFriar, spec);
        var specs = new Dictionary<string, int> { ["Staff"] = 40, ["Rejuvenation"] = 21, ["Parry"] = 1 };
        var gear = new PlayerBotKitResult(2, 6, 1, false, 0, "Staff", eActiveWeaponSlot.TwoHanded);
        Assert.That(PlayerBotKit.Format("hdbot-alb-01", "Pbotaaaab", kit, 40, 10, 1, 51, specs, 1624, 14, 23, "Smite:40", gear), Is.EqualTo(
            "PLAYER_BOT_KIT account=hdbot-alb-01 name=Pbotaaaab region=51 spotLevel=40 level=40 spotClass=10 class=10 spotRace=1 race=1 " +
            "choice=StaffFriar plan=Staff:50,Rejuvenation:24,Parry:18 points=1049 budget=1624 specs=Staff:40,Rejuvenation:21 " +
            "styles=14 spells=23 damageSpell=Smite:40 armourLevel=2 armour=6 weapons=1 shield=0 missing=0 weapon=Staff active=TwoHanded"));
        var bare = new PlayerBotKitPlan(kit.Build, eSpecType.StaffFriar, new BotSpec { SpecType = eSpecType.StaffFriar });
        Assert.That(PlayerBotKit.Format("a", "b", bare, 40, 10, 1, 51, new Dictionary<string, int>(), 1624, 0, 0, "-", gear),
            Does.Contain(" choice=StaffFriar plan=- points=0 budget=1624 specs=- styles=0 "));
    }
}
