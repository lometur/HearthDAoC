using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137 lever test): HEARTHDAOC_PLAYER_BOT_SPOTS. The spots file (realm|region|x|y[|z[|level|class|race]];
// the realm as alb/mid/hib, or 1/2/3 as the owner's richer export writes it; the level-matched run's build as the level
// export's ids, levelmatch/DESIGN.md 2.1), the n-th bot of a realm at that realm's n-th spot, the
// Z (the navmesh near the file's or the nearest NPC's Z, the floor under it, that Z itself, else skipped), the row the
// login reads and its 10 fields, and HoldAnchor. No server and no database: PlayerBotSpots.Place, which reads the world
// and saves the row, is left to the smoke leg.
[TestFixture]
public sealed class UT_PlayerBotSpots
{
    // GetClosestPoint and GetFloorBeneath answer from fields and record each query (as UT_HeadlessClient.cs:48-59).
    private sealed class FakeNav : PathfindingMgrBase
    {
        public bool Available = true, Mesh = true;
        public Vector3? Closest, Floor;
        public readonly List<(string Query, Vector3 From, float Range)> Calls = [];
        public override bool IsAvailable => Available;
        public override bool HasNavmesh(Zone zone) => Mesh;
        public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange, EDtPolyFlags[] filters)
        {
            Assert.That((xRange, yRange), Is.EqualTo(((float)PlayerBotSpots.NavBox, (float)PlayerBotSpots.NavBox)));
            Calls.Add(("closest", position, zRange));
            return Closest;
        }
        public override Vector3? GetFloorBeneath(Zone zone, Vector3 position, float maxDepth, EDtPolyFlags[] filters)
        {
            Calls.Add(("floor", position, maxDepth));
            return Floor;
        }
    }

    private static PlayerBotSlot Slot(eRealm realm, int index) => new(PlayerBotPlan.AccountName(realm, index), realm, index);

    // --- the file ---

    [Test]
    public void Parse_skips_comments_and_blanks_and_reads_an_optional_z()
    {
        PlayerBotSpots spots = PlayerBotSpots.Parse(["# realm|region|x|y[|z]", "", "   ", "alb|1|515894|372963",
            " mid | 100 | 749544 | 830353 | -35 ", "HIB|200|1|2|3"]);
        Assert.That(spots.Count, Is.EqualTo(3));
        Assert.That(spots.For(Slot(eRealm.Albion, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Albion, 1, 515894, 372963, null)));
        Assert.That(spots.For(Slot(eRealm.Midgard, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Midgard, 100, 749544, 830353, -35)));
        Assert.That(spots.For(Slot(eRealm.Hibernia, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Hibernia, 200, 1, 2, 3)));
    }

    [Test]
    public void The_owners_richer_export_reads_as_it_is_with_its_realm_number_and_z()  // decision 7: Realm|RegionId|X|Y|Z
    {
        PlayerBotSpots spots = PlayerBotSpots.Parse(["1|277|31232|35341|15369", "2|100|749544|830353|1200", "3|181|10|20|30"]);
        Assert.That(spots.For(Slot(eRealm.Albion, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Albion, 277, 31232, 35341, 15369)));
        Assert.That(spots.For(Slot(eRealm.Midgard, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Midgard, 100, 749544, 830353, 1200)));
        Assert.That(spots.For(Slot(eRealm.Hibernia, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Hibernia, 181, 10, 20, 30)));
    }

    [Test]
    public void Load_reads_a_crlf_file_and_an_empty_setting_is_none()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ut-player-bot-spots-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "# owner\r\nalb|1|2|3\r\n\r\nmid|100|4|5|6\r\n");
        try
        {
            PlayerBotSpots spots = PlayerBotSpots.Load(" " + path + " ");
            Assert.That(spots.Count, Is.EqualTo(2));
            Assert.That(spots.For(Slot(eRealm.Midgard, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Midgard, 100, 4, 5, 6)));
        }
        finally
        {
            File.Delete(path);
        }
        foreach (string none in new[] { null, "", "  " })
            Assert.That(PlayerBotSpots.Load(none), Is.SameAs(PlayerBotSpots.None));
        Assert.Throws<FileNotFoundException>(() => PlayerBotSpots.Load(path));    // the spawner keeps the bots off
    }

    [TestCase("elf|1|2|3")] [TestCase("0|1|2|3")] [TestCase("4|1|2|3")] [TestCase("alb|1|2")] [TestCase("alb|1|2|3|4|5")]
    [TestCase("alb|x|2|3")] [TestCase("alb|70000|2|3")] [TestCase("alb|1|-2|3")] [TestCase("alb|1|2|3.5")] [TestCase("alb,1,2,3")]
    [TestCase("alb|1|2|3|4|5|6")] [TestCase("alb|1|2|3|4|5|6|7|8")]
    public void A_bad_line_is_refused_naming_its_number(string bad)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotSpots.Parse(["# owner", "alb|1|2|3", bad]));
        Assert.That(e!.Message, Is.EqualTo($"HEARTHDAOC_PLAYER_BOT_SPOTS line 3 must be realm|region|x|y[|z[|level|class|race]], got '{bad}'"));
    }

    [TestCase("alb|1|2|3|z")] [TestCase("alb|1|2|3|")] [TestCase("alb|1|2|3|1.5")]
    public void A_bad_z_is_refused_naming_its_line(string bad)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotSpots.Parse([bad]));
        Assert.That(e!.Message, Is.EqualTo($"HEARTHDAOC_PLAYER_BOT_SPOTS line 1 has a bad z, got '{bad}'"));
    }

    [Test]
    public void A_level_line_reads_its_build_and_counts_in_levels()          // levelmatch/DESIGN.md 2.1
    {
        PlayerBotSpots spots = PlayerBotSpots.Parse(["1|51|453735|485227|3091|40|10|1", "mid|100|1|2|3", "3|200|1|2|3|47|47|10"]);
        Assert.That(spots.For(Slot(eRealm.Albion, 1)), Is.EqualTo(new PlayerBotSpot(eRealm.Albion, 51, 453735, 485227, 3091,
            new PlayerBotBuild(40, eCharacterClass.Friar, eRace.Briton))));
        Assert.That(spots.For(Slot(eRealm.Midgard, 1))!.Build, Is.Null);
        Assert.That(spots.For(Slot(eRealm.Hibernia, 1))!.Build, Is.EqualTo(new PlayerBotBuild(47, eCharacterClass.Druid, eRace.Firbolg)));
        Assert.That((spots.Builds, spots.MaxBuildLevel, spots.DescribeBuilds()), Is.EqualTo((2, (byte)47, "alb:1,mid:0,hib:1")));
        Assert.That(PlayerBotSpots.Parse(["alb|1|2|3"]).DescribeBuilds(), Is.EqualTo("none"));
        Assert.That((PlayerBotSpots.None.Builds, PlayerBotSpots.None.MaxBuildLevel), Is.EqualTo((0, (byte)0)));
    }

    [TestCase("alb|1|2|3|4|0|10|1")] [TestCase("alb|1|2|3|4|51|10|1")] [TestCase("alb|1|2|3|4|x|10|1")]
    [TestCase("alb|1|2|3|4|40|22|1")]     // Warrior: a Midgard class on an Albion line
    [TestCase("alb|1|2|3|4|40|255|1")] [TestCase("alb|1|2|3|4|40|10|0")] [TestCase("alb|1|2|3|4|40|10|250")]
    [TestCase("alb|1|2|3|4|40|10|")]
    public void A_bad_build_is_refused_naming_its_line(string bad)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotSpots.Parse([bad]));
        Assert.That(e!.Message, Is.EqualTo($"HEARTHDAOC_PLAYER_BOT_SPOTS line 1 has a bad level|class|race, got '{bad}'"));
    }

    [Test]
    public void A_level_line_with_no_z_is_a_bad_z() =>
        Assert.That(Assert.Throws<FormatException>(() => PlayerBotSpots.Parse(["alb|1|2|3||40|10|1"]))!.Message, Does.Contain("has a bad z"));

    [Test]
    public void The_nth_bot_of_a_realm_takes_that_realms_nth_spot_and_none_past_the_end()
    {
        PlayerBotSpots spots = PlayerBotSpots.Parse(["alb|1|10|10", "hib|200|5|5", "alb|2|20|20"]);
        Assert.That(spots.For(Slot(eRealm.Albion, 1))!.X, Is.EqualTo(10));
        Assert.That(spots.For(Slot(eRealm.Albion, 2))!.X, Is.EqualTo(20));
        Assert.That(spots.For(Slot(eRealm.Albion, 3)), Is.Null);                 // starts as before
        Assert.That(spots.For(Slot(eRealm.Hibernia, 1))!.Region, Is.EqualTo(200));
        Assert.That(spots.For(Slot(eRealm.Midgard, 1)), Is.Null);
        Assert.That(PlayerBotSpots.None.For(Slot(eRealm.Albion, 1)), Is.Null);
    }

    [Test]
    public void Describe_counts_the_spots_of_each_realm()
    {
        Assert.That(PlayerBotSpots.None.Describe(), Is.EqualTo("none"));
        Assert.That(PlayerBotSpots.Parse(["alb|1|10|10", "hib|200|5|5", "alb|2|20|20"]).Describe(), Is.EqualTo("alb:2,mid:0,hib:1"));
    }

    // --- Z ---

    [Test]
    public void A_seeded_z_snaps_to_the_navmesh_near_it()
    {
        var nav = new FakeNav { Closest = new Vector3(101, 199, 352), Floor = new Vector3(0, 0, 9) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, 500, out Vector3 at, out string how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(101, 199, 352), "mesh")));
        Assert.That(nav.Calls, Is.EqualTo(new[] { ("closest", new Vector3(100, 200, 340), (float)PlayerBotSpots.NavHeight) }));
    }

    [Test]
    public void Off_the_mesh_box_it_takes_the_floor_under_the_seed()
    {
        var nav = new FakeNav { Floor = new Vector3(100, 200, 120) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, 500, out Vector3 at, out string how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 120), "floor")));
        Assert.That(nav.Calls[1], Is.EqualTo(("floor", new Vector3(100, 200, 340 + PlayerBotSpots.FloorAbove), (float)PlayerBotSpots.FloorDepth)));
    }

    [Test]
    public void With_no_mesh_answer_a_near_seed_gives_its_own_z_and_a_far_one_skips_the_spot()
    {
        var nav = new FakeNav();
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, PlayerBotSpots.SeedReach, out Vector3 at, out string how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 340), "npc")));
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, PlayerBotSpots.SeedReach + 1, out at, out how), Is.False);
        Assert.That((at, how), Is.EqualTo((default(Vector3), "skipped")));
    }

    [Test]
    public void Without_a_navmesh_only_the_seed_counts()
    {
        foreach (FakeNav nav in new[] { new FakeNav { Available = false, Closest = Vector3.One }, new FakeNav { Mesh = false, Floor = Vector3.One } })
        {
            Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, 500, out Vector3 at, out string how), Is.True);
            Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 340), "npc")));
            Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, null, int.MaxValue, out _, out how), Is.False);
            Assert.That(how, Is.EqualTo("skipped"));
            Assert.That(nav.Calls, Is.Empty);
        }
    }

    [Test]
    public void No_seed_takes_the_floor_from_the_top_of_the_world()
    {
        var nav = new FakeNav { Closest = Vector3.One, Floor = new Vector3(100, 200, 2500) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, null, int.MaxValue, out Vector3 at, out string how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 2500), "floor")));
        Assert.That(nav.Calls, Is.EqualTo(new[] { ("floor", new Vector3(100, 200, PlayerBotSpots.UnseededTop), (float)PlayerBotSpots.UnseededDepth) }));
    }

    [Test]
    public void A_seed_farther_than_the_visibility_distance_counts_as_no_seed()      // critic fix 4
    {
        var nav = new FakeNav { Closest = new Vector3(100, 200, 340), Floor = new Vector3(100, 200, 2500) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, WorldMgr.VISIBILITY_DISTANCE, out _, out string how), Is.True);
        Assert.That(how, Is.EqualTo("mesh"));                                    // the edge still seeds
        nav.Calls.Clear();
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, WorldMgr.VISIBILITY_DISTANCE + 1, out Vector3 at, out how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 2500), "floor")));
        Assert.That(nav.Calls, Is.EqualTo(new[] { ("floor", new Vector3(100, 200, PlayerBotSpots.UnseededTop), (float)PlayerBotSpots.UnseededDepth) }));
        nav.Floor = null;
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, null, 340, WorldMgr.VISIBILITY_DISTANCE + 1, out _, out how), Is.False);
        Assert.That(how, Is.EqualTo("skipped"));                                 // never the far NPC's own Z
    }

    [Test]
    public void A_file_z_is_checked_against_the_navmesh_and_else_used_as_it_is()     // decision 7
    {
        var nav = new FakeNav { Closest = new Vector3(101, 199, 15370) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, 15369, null, int.MaxValue, out Vector3 at, out string how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(101, 199, 15370), "mesh")));
        Assert.That(nav.Calls[0], Is.EqualTo(("closest", new Vector3(100, 200, 15369), (float)PlayerBotSpots.NavHeight)));
        nav = new FakeNav { Floor = new Vector3(100, 200, 15300) };
        Assert.That(PlayerBotSpots.ResolveZ(nav, null, 100, 200, 15369, null, int.MaxValue, out at, out how), Is.True);
        Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 15300), "floor")));
        Assert.That(nav.Calls[1], Is.EqualTo(("floor", new Vector3(100, 200, 15369 + PlayerBotSpots.FloorAbove), (float)PlayerBotSpots.FloorDepth)));
        foreach (FakeNav none in new[] { new FakeNav(), new FakeNav { Mesh = false } })
        {
            Assert.That(PlayerBotSpots.ResolveZ(none, null, 100, 200, 15369, null, int.MaxValue, out at, out how), Is.True);
            Assert.That((at, how), Is.EqualTo((new Vector3(100, 200, 15369), "file")));
        }
    }

    [Test]
    public void The_spot_line_names_the_account_the_spot_and_how_its_z_was_found() =>
        Assert.That(PlayerBotSpots.Format("hdbot-alb-01", new PlayerBotSpot(eRealm.Albion, 51, 426851, 416460, null),
                new Vector3(426850.6f, 416461.2f, -35.4f), "floor"),
            Is.EqualTo("PLAYER_BOT_SPOT account=hdbot-alb-01 region=51 spot=(426851,416460) at=(426851,416461,-35) how=floor"));

    // --- the row and the driver ---

    [Test]
    public void The_spot_goes_onto_the_accounts_row_that_the_login_reads()
    {
        DbCoreCharacter mid = new() { AccountSlot = 200 }, alb = new() { AccountSlot = 100 };
        var account = new DbAccount { Name = "hdbot-alb-01", Characters = [null, mid, alb] };
        DbCoreCharacter created = new() { AccountSlot = 100 };                    // EnsureCharacter's own object after a creation
        Assert.That(PlayerBotAccounts.LoginRow(account, created), Is.SameAs(alb)); // GameClient.cs:114 reads this one
        Assert.That(PlayerBotAccounts.LoginRow(account, alb), Is.SameAs(alb));
    }

    [Test]
    public void Apply_sets_the_position_and_the_bind_on_the_row()
    {
        DbCoreCharacter row = new() { Region = 1, Xpos = 9, Ypos = 9, Zpos = 9, Direction = 1234, BindRegion = 1, BindXpos = 9,
            BindYpos = 9, BindZpos = 9, BindHeading = 2048 };
        PlayerBotSpots.Apply(row, 51, new Vector3(426850.6f, 416461.2f, -35.4f));
        Assert.That((row.Region, row.Xpos, row.Ypos, row.Zpos, row.Direction), Is.EqualTo((51, 426850, 416461, -35, 0)));
        Assert.That((row.BindRegion, row.BindXpos, row.BindYpos, row.BindZpos, row.BindHeading), Is.EqualTo((51, 426850, 416461, -35, 0)));
    }

    [Test]
    public void A_spot_bot_never_relocates()
    {
        MethodInfo relocate = typeof(PlayerBotDriver).GetMethod("RelocateIfOutgrown", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(relocate, Is.Not.Null);
        PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
        state.RoamingSince = 1;                                                  // roaming for long past RelocateAfterMilliseconds
        state.HoldAnchor = true;
        long now = 1 + 10L * PlayerBotDriver.RelocateAfterMilliseconds;
        relocate!.Invoke(null, [state, null, now]);                              // returns before it reads the player
        Assert.That(state.NextRelocateTick, Is.Zero);
    }

    [Test]
    public void A_build_bot_on_its_spot_may_relocate_within_its_zone()        // levelmatch REVIEW-FIXES 2
    {
        MethodInfo relocate = typeof(PlayerBotDriver).GetMethod("RelocateIfOutgrown", BindingFlags.Static | BindingFlags.NonPublic);
        PlayerBotState state = (PlayerBotState)RuntimeHelpers.GetUninitializedObject(typeof(PlayerBotState));
        state.RoamingSince = 1;
        state.HoldAnchor = true;
        state.Kit = new PlayerBotKitPlan(new PlayerBotBuild(40, eCharacterClass.Friar, eRace.Briton), eSpecType.StaffFriar, new BotSpec());
        long now = 1 + 10L * PlayerBotDriver.RelocateAfterMilliseconds;
        Assert.That(() => relocate!.Invoke(null, [state, null, now]),            // past the anchor: it reads the player (none here)
            Throws.InnerException.TypeOf<NullReferenceException>());
    }
}
