using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: the command_plvl_overrides server property raises (or lowers) the privilege level a
// command needs, e.g. "/tele=2;/tc=2" makes the single-player teleports GM-only on a shared server.
[TestFixture]
public sealed class UT_CommandPrivLevelOverrides
{
    private const uint Player = 1, Gm = 2;

    [Test]
    public void ListedCommandsGetTheOverrideLevel()
    {
        Assert.That(ScriptMgr.CommandPrivLevel("&tele", Player, "/tele=2;/tc=2"), Is.EqualTo(Gm));
        Assert.That(ScriptMgr.CommandPrivLevel("&tc", Player, "/tele=2;/tc=2"), Is.EqualTo(Gm));
    }

    [Test]
    public void OtherCommandsKeepTheirOwnLevel()
    {
        Assert.That(ScriptMgr.CommandPrivLevel("&spawn", Player, "/tele=2;/tc=2"), Is.EqualTo(Player));
        Assert.That(ScriptMgr.CommandPrivLevel("&tele", Player, ""), Is.EqualTo(Player));
        Assert.That(ScriptMgr.CommandPrivLevel("&tele", Player, null), Is.EqualTo(Player));
    }

    [Test]
    public void SpacesAndCaseAreIgnoredAndBadEntriesSkipped()
    {
        const string overrides = " /TELE = 2 ; garbage ; /tc=abc ; =3 ; /grind=";
        Assert.That(ScriptMgr.CommandPrivLevel("&tele", Player, overrides), Is.EqualTo(Gm));
        Assert.That(ScriptMgr.CommandPrivLevel("&tc", Player, overrides), Is.EqualTo(Player));
        Assert.That(ScriptMgr.CommandPrivLevel("&grind", Player, overrides), Is.EqualTo(Player));
    }
}
