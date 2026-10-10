using System;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: a data quest asks "Do you accept?" before it starts (owner 2026-10-09). The prompt wording is the owner's,
// and an answer counts only when it carries the reserved quest ID, comes from the NPC that made the offer, and is not
// older than five minutes.
[TestFixture]
public sealed class UT_DataQuestOffers
{
    private static readonly DateTime Offered = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void PromptUsesTheApprovedWording()
        => Assert.That(DataQuestOffers.PromptText("Master Edric", "Fighting for Camelot"),
            Is.EqualTo("Master Edric offers you the quest \"Fighting for Camelot\". Do you accept?"));

    [Test]
    public void DeclineTextUsesTheApprovedWording()
        => Assert.That(DataQuestOffers.DeclineText, Is.EqualTo("Come back when you're ready."));

    [Test]
    public void ReservedIdIsFarAboveRegisteredQuestTypes()
        => Assert.That(DataQuestOffers.OfferQuestId, Is.GreaterThanOrEqualTo(0xFF00));

    [Test]
    public void AnswerFromTheSameNpcWithOurIdMatches()
        => Assert.That(DataQuestOffers.AnswerMatches(DataQuestOffers.OfferQuestId, true, Offered, Offered.AddSeconds(30)), Is.True);

    [Test]
    public void AnswerWithAnotherQuestIdDoesNotMatch()
        => Assert.That(DataQuestOffers.AnswerMatches(3, true, Offered, Offered.AddSeconds(30)), Is.False);

    [Test]
    public void AnswerFromAnotherNpcDoesNotMatch()
        => Assert.That(DataQuestOffers.AnswerMatches(DataQuestOffers.OfferQuestId, false, Offered, Offered.AddSeconds(30)), Is.False);

    [Test]
    public void AnswerAtFiveMinutesStillMatches()
        => Assert.That(DataQuestOffers.AnswerMatches(DataQuestOffers.OfferQuestId, true, Offered, Offered.AddMinutes(5)), Is.True);

    [Test]
    public void AnswerAfterFiveMinutesDoesNotMatch()
        => Assert.That(DataQuestOffers.AnswerMatches(DataQuestOffers.OfferQuestId, true, Offered, Offered.AddMinutes(5).AddSeconds(1)), Is.False);

    [Test]
    public void ExpiryFollowsTheSameLimit()
    {
        Assert.That(DataQuestOffers.IsExpired(Offered, Offered.AddMinutes(5)), Is.False);
        Assert.That(DataQuestOffers.IsExpired(Offered, Offered.AddMinutes(6)), Is.True);
    }

    [Test]
    public void JournalWithTwentyFiveQuestsIsNotFull()
        => Assert.That(DataQuestOffers.JournalIsFull(25, true), Is.False);

    [Test]
    public void JournalWithTwentySixQuestsAndADataQuestIsFull()
        => Assert.That(DataQuestOffers.JournalIsFull(26, true), Is.True);

    [Test]
    public void JournalWithTwentySixQuestsAndNoDataQuestIsNotFull()
        => Assert.That(DataQuestOffers.JournalIsFull(26, false), Is.False);
}
