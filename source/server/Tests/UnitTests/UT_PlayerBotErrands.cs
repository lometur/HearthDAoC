using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC player bots (#137): the errand bot's decisions (PlayerBotErrandPlan), from what it sees, with no server.
[TestFixture]
public sealed class UT_PlayerBotErrands
{
    private const int Far = 10_000, Fighter = 14, Armsman = 2, Mercenary = 11;

    private static ErrandView View(int level = 1, int classId = Fighter, int questStep = 0, bool acceptDue = false,
        bool hasNote = false, bool giftInBackpack = false, bool giftEquipped = false, bool levelHeld = false,
        int toLorik = 65, int toOlorustos = Far, int toAron = Far, int toTrainer = Far, long msSinceWhisper = 60_000,
        bool dialogPending = false, bool alive = true)
        => new(alive, level, classId, questStep, acceptDue, hasNote, giftInBackpack, giftEquipped, levelHeld,
            toLorik, toOlorustos, toAron, toTrainer, msSinceWhisper, dialogPending);

    private static ErrandAction Next(ErrandView view, ErrandTrainer trainer = null)
        => PlayerBotErrandPlan.Decide(view, trainer ?? PlayerBotErrandPlan.Falron);

    private static void Expect(ErrandAction action, ErrandActionKind kind, ErrandTarget target = ErrandTarget.None, string text = null)
        => Assert.That((action.Kind, action.Target, action.Text), Is.EqualTo((kind, target, text)));

    [Test] public void ADeadBotWaits() => Expect(Next(View(alive: false, dialogPending: true)), ErrandActionKind.Wait);
    [Test] public void APendingDialogIsDeclinedFirst() => Expect(Next(View(dialogPending: true)), ErrandActionKind.DeclineDialog);
    [Test] public void AtTheStartTheBotWhispersInterestedToLorik()
        => Expect(Next(View()), ErrandActionKind.Whisper, ErrandTarget.Lorik, "interested");
    [Test] public void AWhisperWaitsForTheServersGap() => Expect(Next(View(msSinceWhisper: 1_000)), ErrandActionKind.Wait);

    [Test]
    public void FarFromLorikTheBotWalksIntoWhisperRange()
    {
        ErrandAction a = Next(View(toLorik: Far));
        Expect(a, ErrandActionKind.Walk, ErrandTarget.Lorik);
        Assert.That(a.Range, Is.EqualTo(PlayerBotErrandPlan.WhisperRange));
    }

    [Test] public void AfterTheWhisperTheOfferIsAccepted() => Expect(Next(View(acceptDue: true)), ErrandActionKind.Accept, ErrandTarget.Lorik);

    [Test]
    public void StepOneWalksToOlorustosThenInteracts()
    {
        Expect(Next(View(questStep: 1)), ErrandActionKind.Walk, ErrandTarget.Olorustos);
        Expect(Next(View(questStep: 1, toOlorustos: 100)), ErrandActionKind.Interact, ErrandTarget.Olorustos);
    }

    [Test]
    public void StepTwoGivesTheNoteToAronFromWithinGiveRange()
    {
        Expect(Next(View(questStep: 2, hasNote: true, toAron: 127)), ErrandActionKind.Walk, ErrandTarget.Aron);
        Expect(Next(View(questStep: 2, hasNote: true, toAron: 90)), ErrandActionKind.Give, ErrandTarget.Aron, "cq_wax_sealed_note");
    }

    [Test] public void StepTwoWithoutTheNoteGoesBackToOlorustos()
        => Expect(Next(View(questStep: 2, toOlorustos: 100)), ErrandActionKind.Interact, ErrandTarget.Olorustos);
    [Test] public void BeforeLevelFiveTheDriverFights() => Expect(Next(View(questStep: -1, level: 4)), ErrandActionKind.Level);

    [Test]
    public void AtLevelFiveTheBotWalksToItsTrainerAndWhispersSlashing()
    {
        Expect(Next(View(questStep: -1, level: 5)), ErrandActionKind.Walk, ErrandTarget.Trainer);
        Expect(Next(View(questStep: -1, level: 5, toTrainer: 300)), ErrandActionKind.Whisper, ErrandTarget.Trainer, "slashing");
    }

    [Test]
    public void APromotedBotEquipsTheGiftThenIsDone()
    {
        Expect(Next(View(questStep: -1, level: 5, classId: Armsman, giftInBackpack: true)), ErrandActionKind.Equip, text: "slash_sword_item");
        Expect(Next(View(questStep: -1, level: 5, classId: Armsman, giftEquipped: true)), ErrandActionKind.Done);
    }

    [Test] public void AHeldLevelIsReleasedAtTheTrainer()
        => Expect(Next(View(questStep: -1, level: 5, classId: Armsman, giftEquipped: true, levelHeld: true, toTrainer: 100)),
            ErrandActionKind.Interact, ErrandTarget.Trainer);
    [Test] public void TheMercenaryFallbackCountsAsPromoted()
        => Expect(Next(View(questStep: -1, level: 5, classId: Mercenary, giftEquipped: true), PlayerBotErrandPlan.Astyp), ErrandActionKind.Done);
    [Test] public void PromotedBeforeTheQuestIsAFailure()
        => Assert.That(Next(View(classId: Armsman, level: 5)).Kind, Is.EqualTo(ErrandActionKind.Fail));
    [Test] public void AnArmsmanWithTheMercenaryTrainerIsAFailure()
        => Assert.That(Next(View(questStep: -1, level: 5, classId: Armsman), PlayerBotErrandPlan.Astyp).Kind, Is.EqualTo(ErrandActionKind.Fail));

    // The #137 lever: the NPC each action faces, woken after it (PlayerBotWake.Interacting) and named on a talk line.
    [TestCase(ErrandActionKind.Whisper, ErrandTarget.Lorik, ErrandTarget.Lorik)]
    [TestCase(ErrandActionKind.Whisper, ErrandTarget.Trainer, ErrandTarget.Trainer)]
    [TestCase(ErrandActionKind.Interact, ErrandTarget.Olorustos, ErrandTarget.Olorustos)]
    [TestCase(ErrandActionKind.Interact, ErrandTarget.Trainer, ErrandTarget.Trainer)]
    [TestCase(ErrandActionKind.Accept, ErrandTarget.None, ErrandTarget.Lorik)]
    [TestCase(ErrandActionKind.Give, ErrandTarget.None, ErrandTarget.Aron)]
    [TestCase(ErrandActionKind.Walk, ErrandTarget.Aron, ErrandTarget.None)]
    [TestCase(ErrandActionKind.Equip, ErrandTarget.None, ErrandTarget.None)]
    [TestCase(ErrandActionKind.DeclineDialog, ErrandTarget.None, ErrandTarget.None)]
    [TestCase(ErrandActionKind.Done, ErrandTarget.None, ErrandTarget.None)]
    public void TheBotTalksToTheNpcItsActionFaces(ErrandActionKind kind, ErrandTarget target, ErrandTarget talksTo)
        => Assert.That(PlayerBotErrandPlan.TalkTarget(new ErrandAction(kind, target)), Is.EqualTo(talksTo));

    [Test]
    public void TheTalkLineSaysWhetherTheBrainRanBefore()
    {
        string line = PlayerBotErrandPlan.TalkLine("Master Lorik", ErrandActionKind.Whisper, "asleep", lever: true);
        Assert.That(line, Is.EqualTo("talk npc=\"Master Lorik\" action=Whisper brainBefore=asleep lever=gamebot"));
        Assert.That(PlayerBotErrandPlan.TalkLine("Guardian Aron", ErrandActionKind.Give, "awake", lever: false),
            Is.EqualTo("talk npc=\"Guardian Aron\" action=Give brainBefore=awake lever=player"));
        // summarize.py counts " FAIL"; promo.sh stops on "after-login|FAIL".
        Assert.That(line, Does.Not.Contain(" FAIL").And.Not.Contain("after-login"));
    }

    [Test]
    public void TheRangesSitInsideTheServers()
    {
        Assert.That(PlayerBotErrandPlan.WhisperRange, Is.LessThan(512));  // WorldMgr.WHISPER_DISTANCE
        Assert.That(PlayerBotErrandPlan.InteractRange, Is.LessThan(192)); // WorldMgr.INTERACT_DISTANCE
        Assert.That(PlayerBotErrandPlan.GiveRange, Is.LessThan(128));     // WorldMgr.GIVE_ITEM_DISTANCE
        Assert.That(PlayerBotErrandPlan.WhisperGapMs, Is.GreaterThan(1500));
    }
}
