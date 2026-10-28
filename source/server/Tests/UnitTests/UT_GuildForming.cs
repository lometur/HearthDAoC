using DOL.GS.HearthDAoC;
using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: forming a guild needs a group of guild_num (8) again, bots included (owner 2026-10-28, #176). GuildForming
// holds the rule; guild.cs counts the group (every member: players, player bots, gamebots, companions) and its human
// players, and carries the outcome out. Player bots never join a starter guild (StartupGuilds.AddNewbieToStarterGuild).
[TestFixture]
public sealed class UT_GuildForming
{
    private const int GuildNum = 8; // guild_num's default and the clean world's value

    private static GuildFormStep Rule(int members, int humans, int guildNum = GuildNum) =>
        GuildForming.Rule(members, humans, guildNum);

    [Test]
    public void APlayerAloneIsRefused()
    {
        Assert.That(Rule(1, 1), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void APlayerAndSixBotsAreRefused()
    {
        Assert.That(Rule(7, 1), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void APlayerAndSevenBotsFormAtOnce()
    {
        // The bots can't answer the accept dialog: the player forms the guild at the registrar, for the fee.
        Assert.That(Rule(8, 1), Is.EqualTo(GuildFormStep.FormNow));
    }

    [Test]
    public void TwoPlayersAndSixBotsAskTheOtherPlayer()
    {
        // The dialog goes to the human players other than the leader; the bots count as accepting.
        Assert.That(Rule(8, 2), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [Test]
    public void EightPlayersAskTheOtherSeven()
    {
        Assert.That(Rule(8, 8), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [Test]
    public void PlayersShortOfTheNumberAreRefusedToo()
    {
        Assert.That(Rule(7, 2), Is.EqualTo(GuildFormStep.Refused));
        Assert.That(Rule(7, 7), Is.EqualTo(GuildFormStep.Refused));
    }

    [Test]
    public void WithGuildNumOneAPlayerAloneFormsAtOnce()
    {
        // An owner who sets guild_num to 1 lets a player form a guild alone.
        Assert.That(Rule(1, 1, guildNum: 1), Is.EqualTo(GuildFormStep.FormNow));
    }

    [Test]
    public void AGroupOverTheNumberFormsToo()
    {
        Assert.That(Rule(9, 1), Is.EqualTo(GuildFormStep.FormNow));
        Assert.That(Rule(9, 3), Is.EqualTo(GuildFormStep.AskOthers));
    }

    [TestCase("hdbot-alb-01", true)]
    [TestCase("hdbot-hib-150", true)]
    [TestCase("HDBOT-mid-07", true)] // Account.Name is compared without case in the database
    [TestCase("hdbot", false)]
    [TestCase("myhdbot-alb-01", false)]
    [TestCase("lometur", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void APlayerBotIsKnownByItsAccount(string account, bool bot)
    {
        // StartupGuilds.AddNewbieToStarterGuild skips these characters, so a player bot never joins a starter guild.
        Assert.That(PlayerBotPlan.IsBotAccount(account), Is.EqualTo(bot));
    }
}
