using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.Realm;
using DOL.GS.ServerRules;
using DOL.GS.Styles;
using DOL.UnitTests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137 level-matched run, levelmatch/DESIGN.md): a bot made at a GameBot's level, class and race. The
// allocator against GameBot.SpendSpecPoints' numbers (an autonomous GameBot's training, REVIEW-FIXES 5) and
// VerifySpecPoints' budget, the stats against CharacterStatValidator, gender and race, the specs string and its keys,
// the retrain and the skill counts. No server and no database (an empty one for GameObject's type initializer, as
// UT_PlayerBotDriver.cs:26-38, and SkillBase's property names, PetTestLanguageScope): OpenSpecs (SkillBase's career),
// TrainTo and the login are left to the smoke leg.
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
}
