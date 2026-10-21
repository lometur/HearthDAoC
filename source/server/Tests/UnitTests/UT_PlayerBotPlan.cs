using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC proto (#137): the HEARTHDAOC_PLAYER_BOTS switch (PlayerBotPlan), the fixed roster (PlayerBotRoster) and
// the login pacing (PlayerBotPacing).
[TestFixture]
public sealed class UT_PlayerBotPlan
{
    [TestCase(null)] [TestCase("")] [TestCase("  ")] [TestCase("off")] [TestCase("OFF")]
    public void Off_values_turn_it_off(string text)
    {
        PlayerBotPlan plan = PlayerBotPlan.Parse(text);
        Assert.That(plan.On, Is.False);
        Assert.That(plan.Slots(), Is.Empty);
        Assert.That(plan.Describe(), Is.EqualTo("off"));
    }

    [Test]
    public void Zero_is_test_world_mode_with_no_bots()
    {
        PlayerBotPlan plan = PlayerBotPlan.Parse("0");
        Assert.That((plan.On, plan.Total, plan.Describe()), Is.EqualTo((true, 0, "alb=0 mid=0 hib=0")));
    }

    [TestCase("30", 10, 10, 10)] [TestCase("31", 11, 10, 10)] [TestCase("32", 11, 11, 10)] [TestCase("2", 1, 1, 0)]
    [TestCase("297", 99, 99, 99)] [TestCase("alb:30", 30, 0, 0)] [TestCase("mid:10, hib:5", 0, 10, 5)]
    [TestCase("ALB:1,Mid:1,hib:1", 1, 1, 1)] [TestCase(" hib:0 ", 0, 0, 0)]
    public void Counts(string text, int alb, int mid, int hib)
    {
        PlayerBotPlan plan = PlayerBotPlan.Parse(text);
        Assert.That((plan.On, plan.Albion, plan.Midgard, plan.Hibernia), Is.EqualTo((true, alb, mid, hib)));
    }

    [TestCase("298")] [TestCase("-1")] [TestCase("+3")] [TestCase("alb:100")] [TestCase("alb:1,alb:2")]
    [TestCase("elf:3")] [TestCase("alb")] [TestCase("alb:x")] [TestCase("30,alb:1")] [TestCase("alb:1,")] [TestCase("on")]
    public void Anything_else_is_refused_naming_the_variable(string text)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotPlan.Parse(text));
        Assert.That(e.Message, Does.StartWith("HEARTHDAOC_PLAYER_BOTS must be off, a number of bots from 0 to 297"));
        Assert.That(e.Message, Does.EndWith($"got '{text}'"));
    }

    [Test]
    public void Slots_interleave_the_realms_with_fixed_account_names()
    {
        Assert.That(PlayerBotPlan.Parse("alb:2,mid:1,hib:3").Slots().Select(s => s.AccountName), Is.EqualTo(new[]
            { "hdbot-alb-01", "hdbot-mid-01", "hdbot-hib-01", "hdbot-alb-02", "hdbot-hib-02", "hdbot-hib-03" }));
        Assert.That(PlayerBotPlan.AccountName(eRealm.Midgard, 7), Is.EqualTo("hdbot-mid-07"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlayerBotPlan.AccountName(eRealm.None, 1));
    }

    [Test]
    public void Only_the_first_albion_slot_is_the_quest_fighter() =>
        Assert.That(PlayerBotPlan.Parse("alb:2,mid:1,hib:1").Slots().Where(s => s.IsQuestFighter).Select(s => s.AccountName),
            Is.EqualTo(new[] { "hdbot-alb-01" }));

    [Test]
    public void Names_are_unique_and_pass_the_create_and_login_rules()
    {
        IReadOnlyList<PlayerBotSlot> slots = PlayerBotPlan.Parse("297").Slots();
        Assert.That(slots.Select(s => s.AccountName).Distinct().Count(), Is.EqualTo(297));
        Assert.That(slots.Select(s => s.CharacterName).Distinct().Count(), Is.EqualTo(297));
        foreach (PlayerBotSlot s in slots)
        {
            Assert.That(Regex.IsMatch(s.AccountName, "^[a-zA-Z0-9]*$"), Is.False, s.AccountName); // LoginRequestHandler.cs:254
            Assert.That(Regex.IsMatch(s.CharacterName, "^[A-Z][a-zA-Z]"), Is.True, s.CharacterName); // CharacterCreateRequestHandler.cs:43
            Assert.That(s.CharacterName.Length, Is.GreaterThanOrEqualTo(3));
        }
        Assert.That(slots[0].CharacterName, Is.EqualTo("Pbotaaaab"));
    }

    [Test]
    public void Each_template_spends_exactly_the_stat_budget_and_is_a_starting_class()
    {
        foreach (PlayerBotTemplate t in PlayerBotRoster.All)
        {
            bool valid = CharacterStatValidator.Validate(PlayerBotRoster.StartingStats(t.Race), GlobalConstants.STARTING_STATS_DICT[t.Race],
                eStat.STR, eStat.CON, eStat.DEX, 1, out int distributed);
            Assert.That((valid, distributed), Is.EqualTo((true, 30)), t.Race.ToString());
            Assert.That(GlobalConstants.STARTING_CLASSES_DICT[t.Realm], Does.Contain(t.BaseClass)); // GlobalConstants.cs:938
        }
    }

    [Test]
    public void The_account_slot_round_trips_through_world_inits_math()
    {
        foreach (PlayerBotTemplate t in PlayerBotRoster.All)
        {
            int accountSlot = PlayerBotRoster.AccountSlot(t.Realm);
            int realm = (int)t.Realm;
            int realmOffset = HeadlessClient.CharIndexFor(accountSlot) - (realm * 10 - 10); // WorldInitRequestHandler.cs:159-160
            Assert.That(realm * 100 + realmOffset, Is.EqualTo(accountSlot));
        }
    }

    [TestCase(30, 0, 4)] [TestCase(30, 6, 2)] [TestCase(2, 0, 2)] [TestCase(30, 8, 0)] [TestCase(30, 9, 0)] [TestCase(0, 0, 0)]
    public void Pacing_starts_at_most_4_a_second_and_keeps_8_loading(int due, int loading, int expected) =>
        Assert.That(PlayerBotPacing.LoginsToStart(due, loading, PlayerBotSpawner.LoginsPerSecond, PlayerBotSpawner.MaxLoading),
            Is.EqualTo(expected));
}
