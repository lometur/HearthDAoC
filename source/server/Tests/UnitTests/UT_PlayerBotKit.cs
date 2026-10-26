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

// HearthDAoC proto (#137 level-matched run, levelmatch/DESIGN.md): a bot made at a GameBot's level, class and race. The
// allocator against GameBot.SpendSpecPoints' numbers (an autonomous GameBot's training, REVIEW-FIXES 5) and
// VerifySpecPoints' budget, the stats against CharacterStatValidator, gender and race, the specs string and its keys,
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

    // What an autonomous GameBot trained level by level has earned: VerifyTotal without the half levels.
    private static int Earned(int level, int multiplier) =>
        Enumerable.Range(2, Math.Max(0, level - 1)).Sum(i => PlayerBotKit.SpecPointsForLevel(i, multiplier, false));

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

    [TestCase(20, 20, 17, 6, 6, 3)] [TestCase(30, 30, 27, 9, 7, 2)] [TestCase(40, 40, 37, 10, 9, 5)]
    [TestCase(45, 45, 42, 11, 9, 9)] [TestCase(47, 47, 44, 12, 9, 3)]
    public void An_armsman_spends_as_a_gamebot_trained_level_by_level(int level, int slash, int shields, int parry, int bows, int unspent)
    {
        Dictionary<string, int> specs = Run(Armsman, level, 20, out int left);
        Assert.That((specs[Specs.Slash], specs[Specs.Shields], specs[Specs.Parry], specs[Specs.Crossbow], left),
            Is.EqualTo((slash, shields, parry, bows, unspent)));
        Assert.That(specs.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(Earned(level, 20)));
        Assert.That(specs.Values.Sum(PlayerBotKit.Cost), Is.LessThanOrEqualTo(PlayerBotKit.Budget(level, 20)));   // never respecced
    }

    [TestCase(30, 30, 1, 0)] [TestCase(47, 47, 1, 0)]
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
    public void Every_gamebot_plan_spends_within_its_earned_points_its_level_and_its_caps()
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
                Assert.That(specs.Values.Sum(PlayerBotKit.Cost) + left, Is.EqualTo(Earned(level, 20)), $"{cls} {choice} {level}");
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
