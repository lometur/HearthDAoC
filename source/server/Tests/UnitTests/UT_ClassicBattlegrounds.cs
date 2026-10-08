using System.Collections.Generic;
using System.Linq;
using DOL.GS.HearthDAoC;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the classic battlegrounds. A frontier porter sends a character wearing the battlegrounds
// medallion to the battleground for its level (Abermenai 15-19, Thidranki 20-24, Murdaigean 25-29,
// Caledonia 30-35) while its realm level is under that battleground's cap, and says why when it won't.
// ClassicBattlegrounds holds every decision; the game wiring (ClassicBattlegroundsScript) only feeds it the
// battleground rows and the character's state. The realm teleporters' [Battlegrounds] choice says the
// porter's cap refusal.
[TestFixture]
public sealed class UT_ClassicBattlegrounds
{
    private const int Albion = (int)eRealm.Albion, Midgard = (int)eRealm.Midgard, Hibernia = (int)eRealm.Hibernia;
    private const uint Player = (uint)ePrivLevel.Player, Gm = (uint)ePrivLevel.GM, Admin = (uint)ePrivLevel.Admin;

    // The four battleground rows after the world fix. The cap is REALMPOINTS_FOR_LEVEL[MaxRealmLevel].
    private static List<BattlegroundBracket> Classic()
    {
        return new List<BattlegroundBracket>
        {
            new(253, "Abermenai", 15, 19, 3, 125),
            new(252, "Thidranki", 20, 24, 4, 350),
            new(251, "Murdaigean", 25, 29, 6, 1375),
            new(250, "Caledonia", 30, 35, 10, 7125),
        };
    }

    private static List<BattlegroundBracket> Without(int region)
    {
        return Classic().Where(b => b.Region != region).ToList();
    }

    // The classic rows, with this region's row uncapped (MaxRealmLevel 0).
    private static List<BattlegroundBracket> Uncapped(int region)
    {
        return Classic().Select(b => b.Region == region ? b with { MaxRealmLevel = 0, RealmPointCap = 0 } : b).ToList();
    }

    private static BattlegroundBracket Row(int region)
    {
        return Classic().Single(b => b.Region == region);
    }

    // The porter's decision for a 1L1 Albion character with no realm points, with one thing changed per call.
    private static PorterDecision Porter(int level, int realmLevel = 1, long realmPoints = 0, int realm = Albion,
        IReadOnlyList<BattlegroundBracket> brackets = null)
    {
        return ClassicBattlegrounds.Porter(level, realmLevel, realmPoints, realm, brackets ?? Classic());
    }

    private static PorterDecision Refused(string text)
    {
        return new PorterDecision(null, text);
    }

    [Test]
    public void LevelsOutsideTheRangeAreRefusedWithTheirTexts()
    {
        foreach (int level in new[] { 1, 14 })
            Assert.That(Porter(level), Is.EqualTo(Refused(
                "The battlegrounds are for levels 15 to 35. Come back when you reach level 15.")), $"level {level}");

        foreach (int level in new[] { 36, 50 })
            Assert.That(Porter(level), Is.EqualTo(Refused(
                "You have outgrown the battlegrounds, which are for levels 15 to 35.")), $"level {level}");

        // The levels in the texts come from the rows: the lowest MinLevel and the highest MaxLevel.
        Assert.That(Porter(17, brackets: Without(253)), Is.EqualTo(Refused(
            "The battlegrounds are for levels 20 to 35. Come back when you reach level 20.")));
        Assert.That(Porter(30, brackets: Without(250)), Is.EqualTo(Refused(
            "You have outgrown the battlegrounds, which are for levels 15 to 29.")));
    }

    [TestCase(15, 253, "Abermenai")]
    [TestCase(19, 253, "Abermenai")]
    [TestCase(20, 252, "Thidranki")]
    [TestCase(24, 252, "Thidranki")]
    [TestCase(25, 251, "Murdaigean")]
    [TestCase(29, 251, "Murdaigean")]
    [TestCase(30, 250, "Caledonia")]
    [TestCase(35, 250, "Caledonia")]
    public void EachLevelGoesToItsBattleground(int level, int region, string name)
    {
        Assert.That(Porter(level), Is.EqualTo(new PorterDecision(
            new BattlegroundLanding(name, (ushort)region, 38113, 53507, 4160, 3268), null)));
    }

    // Realm level is (rank - 1) * 10 + level, so 2 is 1L2, and MaxRealmLevel means "must be below". The
    // realm points are written as in English whatever the server's culture (fr-FR would write 1 375).
    [TestCase(17, 253, 2, 124, 3, 125,
        "Abermenai is for realm rank 1L2 and below, under 125 realm points. You have 125, so I cannot send you there.")]
    [TestCase(22, 252, 3, 349, 4, 412,
        "Thidranki is for realm rank 1L3 and below, under 350 realm points. You have 412, so I cannot send you there.")]
    [TestCase(27, 251, 5, 1374, 6, 1375,
        "Murdaigean is for realm rank 1L5 and below, under 1,375 realm points. You have 1,375, so I cannot send you there.")]
    [TestCase(32, 250, 9, 7124, 10, 10000,
        "Caledonia is for realm rank 1L9 and below, under 7,125 realm points. You have 10,000, so I cannot send you there.")]
    [SetCulture("fr-FR")]
    public void RealmPointCapsAtEachEdge(int level, int region, int highestRealmLevel, long highestPoints,
        int refusedRealmLevel, long refusedPoints, string refusal)
    {
        PorterDecision goes = Porter(level, highestRealmLevel, highestPoints);
        Assert.That(goes.Refusal, Is.Null);
        Assert.That(goes.Destination.Region, Is.EqualTo(region));

        Assert.That(Porter(level, refusedRealmLevel, refusedPoints), Is.EqualTo(Refused(refusal)));
    }

    // The realm teleporters' [Battlegrounds] choice (upstream 0.35) says the porter's cap refusal, word for word.
    [TestCase(253, 2, 124, 3, 125)]
    [TestCase(252, 3, 349, 4, 412)]
    [TestCase(251, 5, 1374, 6, 1375)]
    [TestCase(250, 9, 7124, 10, 10000)]
    [SetCulture("fr-FR")]
    public void TheTeleportersCapRefusalIsThePorters(int region, int highestRealmLevel, long highestPoints,
        int refusedRealmLevel, long refusedPoints)
    {
        BattlegroundBracket row = Row(region);
        Assert.That(ClassicBattlegrounds.CapRefusal(row, highestRealmLevel, highestPoints), Is.Null);
        Assert.That(ClassicBattlegrounds.CapRefusal(row, refusedRealmLevel, refusedPoints), Is.Not.Null);
        Assert.That(ClassicBattlegrounds.CapRefusal(row, refusedRealmLevel, refusedPoints),
            Is.EqualTo(Porter(row.MinLevel, refusedRealmLevel, refusedPoints).Refusal));
        Assert.That(ClassicBattlegrounds.CapRefusal(row with { MaxRealmLevel = 0, RealmPointCap = 0 }, 50, 1_000_000),
            Is.Null);
    }

    [Test]
    public void MaxRealmLevelZeroMeansNoCap()
    {
        List<BattlegroundBracket> brackets = Uncapped(252);

        PorterDecision decision = Porter(22, realmLevel: 50, realmPoints: 1_000_000, brackets: brackets);
        Assert.That(decision.Refusal, Is.Null);
        Assert.That(decision.Destination.Region, Is.EqualTo(252));

        // Over the limit only by level then.
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 24, 50, brackets, out _), Is.False);
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 25, 1, brackets, out _), Is.True);
    }

    [TestCase(253)]
    [TestCase(252)]
    [TestCase(251)]
    [TestCase(250)]
    public void EachRealmLandsBesideItsPortalKeep(int region)
    {
        BattlegroundBracket bracket = Row(region);
        var spots = new Dictionary<int, BattlegroundLanding>
        {
            [Albion] = new(bracket.Name, bracket.Region, 38113, 53507, 4160, 3268),
            [Midgard] = new(bracket.Name, bracket.Region, 53568, 23643, 4530, 0),
            [Hibernia] = new(bracket.Name, bracket.Region, 17367, 18248, 4320, 0),
        };

        foreach ((int realm, BattlegroundLanding spot) in spots)
        {
            Assert.That(ClassicBattlegrounds.Landing(realm, bracket), Is.EqualTo(spot), $"realm {realm}");
            Assert.That(Porter(bracket.MinLevel, realm: realm), Is.EqualTo(new PorterDecision(spot, null)), $"realm {realm}");
        }

        // Any other realm has no landing spot: the porter neither sends it nor says anything.
        foreach (int realm in new[] { 0, 4 })
        {
            Assert.That(ClassicBattlegrounds.Landing(realm, bracket), Is.Null, $"realm {realm}");
            Assert.That(Porter(bracket.MinLevel, realm: realm), Is.EqualTo(new PorterDecision(null, null)), $"realm {realm}");
        }
    }

    [Test]
    public void ALevelWithoutARowIsRefused()
    {
        Assert.That(Porter(22, brackets: Without(252)),
            Is.EqualTo(Refused("No battleground on this server takes level 22.")));

        foreach (int level in new[] { 1, 22, 50 })
            Assert.That(Porter(level, brackets: new List<BattlegroundBracket>()),
                Is.EqualTo(Refused($"No battleground on this server takes level {level}.")), $"level {level}");
    }

    // Level above MaxLevel, or realm level at or above MaxRealmLevel. Being below MinLevel is not over the limit.
    [TestCase(253, 20, 1, true)]
    [TestCase(253, 19, 3, true)]
    [TestCase(253, 19, 2, false)]
    [TestCase(253, 10, 1, false)]
    [TestCase(252, 25, 1, true)]
    [TestCase(252, 24, 4, true)]
    [TestCase(252, 24, 3, false)]
    [TestCase(252, 15, 1, false)]
    [TestCase(251, 30, 1, true)]
    [TestCase(251, 29, 6, true)]
    [TestCase(251, 29, 5, false)]
    [TestCase(250, 36, 1, true)]
    [TestCase(250, 35, 10, true)]
    [TestCase(250, 35, 9, false)]
    public void OverTheLimitByLevelOrRealmLevel(int region, int level, int realmLevel, bool over)
    {
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, region, level, realmLevel, Classic(),
            out BattlegroundBracket bracket), Is.EqualTo(over));
        Assert.That(bracket, Is.EqualTo(over ? Row(region) : null));
    }

    [Test]
    public void NotOverTheLimitForAGmOrOutsideTheFour()
    {
        foreach (uint privLevel in new[] { Gm, Admin })
            Assert.That(ClassicBattlegrounds.IsOverLimit(privLevel, 252, 50, 50, Classic(), out _), Is.False,
                $"privilege level {privLevel}");

        foreach (int region in new[] { 250, 251, 252, 253 })
            Assert.That(ClassicBattlegrounds.IsBattleground(region), Is.True, $"region {region}");

        // Cathal Valley has a battleground row too (45-49, under realm level 45), but it is not one of the four.
        List<BattlegroundBracket> brackets = Classic();
        brackets.Add(new BattlegroundBracket(165, "Cathal Valley", 45, 49, 45, 734250));
        foreach (int region in new[] { 1, 165, 238, 249, 254 })
        {
            Assert.That(ClassicBattlegrounds.IsBattleground(region), Is.False, $"region {region}");
            Assert.That(ClassicBattlegrounds.IsOverLimit(Player, region, 50, 50, brackets, out BattlegroundBracket bracket),
                Is.False, $"region {region}");
            Assert.That(bracket, Is.Null, $"region {region}");
        }

        // One of the four without its row has no limit to compare with.
        Assert.That(ClassicBattlegrounds.IsOverLimit(Player, 252, 50, 50, Without(252), out _), Is.False);
    }

    [TestCase(true, true, 1, true)]
    [TestCase(true, true, 100, true)]
    [TestCase(true, true, 200, true)]
    [TestCase(true, true, 165, true)]
    [TestCase(false, true, 1, false)]
    [TestCase(true, false, 1, false)]
    [TestCase(false, false, 1, false)]
    [TestCase(true, true, 250, false)]
    [TestCase(true, true, 251, false)]
    [TestCase(true, true, 252, false)]
    [TestCase(true, true, 253, false)]
    public void GoesToTheBindPointUnlessItIsMissingZonelessOrInABattleground(bool bindRegionExists, bool bindHasZone,
        int bindRegion, bool toBind)
    {
        Assert.That(ClassicBattlegrounds.GoesToBind(bindRegionExists, bindHasZone, bindRegion), Is.EqualTo(toBind));
    }

    // A capture resets a keep to starting_keep_level (4 in the shipped worlds).
    [TestCase(253, 4)]
    [TestCase(252, 4)]
    [TestCase(251, 2)]
    [TestCase(250, 10)]
    public void ACentralKeepAboveLevelOneGoesBackToOne(int region, int level)
    {
        Assert.That(ClassicBattlegrounds.ShouldResetKeepLevel(region, isPortalKeep: false, level), Is.True);
    }

    [TestCase(252, true, 4)]
    [TestCase(250, true, 1)]
    [TestCase(252, false, 1)]
    [TestCase(253, false, 1)]
    [TestCase(165, false, 4)]
    [TestCase(238, false, 4)]
    [TestCase(163, false, 4)]
    public void NoKeepLevelChangeForAPortalKeepLevelOneOrAnotherRegion(int region, bool isPortalKeep, int level)
    {
        Assert.That(ClassicBattlegrounds.ShouldResetKeepLevel(region, isPortalKeep, level), Is.False);
    }

    [TestCase(1, "1L1")]
    [TestCase(2, "1L2")]
    [TestCase(3, "1L3")]
    [TestCase(5, "1L5")]
    [TestCase(9, "1L9")]
    [TestCase(10, "2L0")]
    [TestCase(24, "3L4")]
    public void RankLabels(int realmLevel, string label)
    {
        Assert.That(ClassicBattlegrounds.RankLabel(realmLevel), Is.EqualTo(label));
    }

    [Test]
    public void ARefusalIsSaidOncePerCeremony()
    {
        Assert.That(ClassicBattlegrounds.RefusedAtKey, Is.EqualTo("hdc_bg_porter_refused"));
        Assert.That(ClassicBattlegrounds.RefusalQuietMs, Is.EqualTo(30_000));

        const long now = 1_000_000;
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, null), Is.True, "none said before");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 30_000), Is.True, "30 s ago");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 10_000), Is.False, "10 s ago");
        Assert.That(ClassicBattlegrounds.ShouldSayRefusal(now, now - 29_999), Is.False, "just under 30 s ago");

        // The porter casts every 120 s, and its callback runs 5 s and 15 s after each cast. The time is stored
        // only when the refusal is said.
        long? lastSaid = null;
        var said = new List<long>();
        foreach (long cast in new long[] { 0, 120_000, 240_000 })
        foreach (long run in new[] { cast + 5_000, cast + 15_000 })
        {
            if (!ClassicBattlegrounds.ShouldSayRefusal(run, lastSaid))
                continue;

            said.Add(run);
            lastSaid = run;
        }

        Assert.That(said, Is.EqualTo(new long[] { 5_000, 125_000, 245_000 }));
    }

    [Test]
    public void OutgrownMessages()
    {
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(252), atBind: true), Is.EqualTo(
            "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your bind point."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(252), atBind: false), Is.EqualTo(
            "You have outgrown Thidranki (levels 20 to 24, realm rank 1L3 and below), so you are back at your realm's portal keep."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Row(250), atBind: true), Is.EqualTo(
            "You have outgrown Caledonia (levels 30 to 35, realm rank 1L9 and below), so you are back at your bind point."));
        Assert.That(ClassicBattlegrounds.OutgrownMessage(Uncapped(252).Single(b => b.Region == 252), atBind: true),
            Is.EqualTo("You have outgrown Thidranki (levels 20 to 24), so you are back at your bind point."));

        // The script checks again, and sends the message, this long after GameEntered.
        Assert.That(ClassicBattlegrounds.LoginCheckDelayMs, Is.EqualTo(1000));
        Assert.That(ClassicBattlegrounds.LoginCheckAttempts, Is.EqualTo(10));
    }

    [Test]
    public void SummaryLine()
    {
        Assert.That(ClassicBattlegrounds.Regions, Is.EqualTo(new ushort[] { 253, 252, 251, 250 }));
        Assert.That(ClassicBattlegrounds.Names, Is.EqualTo(new Dictionary<ushort, string>
        {
            [253] = "Abermenai",
            [252] = "Thidranki",
            [251] = "Murdaigean",
            [250] = "Caledonia",
        }));

        Assert.That(ClassicBattlegrounds.Summary(Classic()), Is.EqualTo("Classic battlegrounds: Abermenai 15-19 up to 1L2, "
            + "Thidranki 20-24 up to 1L3, Murdaigean 25-29 up to 1L5, Caledonia 30-35 up to 1L9"));

        // In region order whatever the order of the rows; a missing row and a row without a cap say so.
        List<BattlegroundBracket> edited = Uncapped(250).Where(b => b.Region != 252).Reverse().ToList();
        Assert.That(ClassicBattlegrounds.Summary(edited), Is.EqualTo("Classic battlegrounds: Abermenai 15-19 up to 1L2, "
            + "Thidranki off (no battleground row), Murdaigean 25-29 up to 1L5, Caledonia 30-35 with no realm rank cap"));
    }
}
