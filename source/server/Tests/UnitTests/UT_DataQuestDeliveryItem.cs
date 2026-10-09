using System;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: a Deliver or DeliverFinish step's item is handed when the step begins only if the player doesn't carry
// it and the step just finishing isn't handing it ("Traveler's Way -- Supply Run" gave two bundles of supplies at
// Thol Dunnin; 77 classic quests, owner test 2026-10-09).
[TestFixture]
public sealed class UT_DataQuestDeliveryItem
{
    private static readonly string[] Nothing = Array.Empty<string>();

    [Test]
    public void NotCarriedAndNotBeingHandedIsHanded()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", new[] { "other_item" }, Nothing), Is.True);

    [Test]
    public void NothingCarriedIsHanded()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", Nothing, Nothing), Is.True);

    [Test]
    public void CarriedIsNotHandedAgain()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", new[] { "other_item", "cq_supplies" }, Nothing), Is.False);

    [Test]
    public void BeingHandedByTheFinishingStepIsNotHandedAgain()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", Nothing, new[] { "cq_supplies" }), Is.False);

    [Test]
    public void AnotherItemBeingHandedDoesNotMatter()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", Nothing, new[] { "cq_letter" }), Is.True);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AnEmptyTemplateHandsNothing(string template)
        => Assert.That(QuestDeliveryItems.ShouldHand(template, Nothing, Nothing), Is.False);

    [Test]
    public void TemplateIdsCompareWithoutCaseAndSpaces()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDeliveryItems.ShouldHand("CQ_Supplies", new[] { "cq_supplies" }, Nothing), Is.False);
            Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", Nothing, new[] { "CQ_SUPPLIES" }), Is.False);
            Assert.That(QuestDeliveryItems.ShouldHand(" cq_supplies ", new[] { "cq_supplies" }, Nothing), Is.False);
        });
    }

    [Test]
    public void ALongerIdIsNotTheSameItem()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", new[] { "cq_supplies_2" }, Nothing), Is.True);

    [Test]
    public void NullListsAreEmpty()
        => Assert.That(QuestDeliveryItems.ShouldHand("cq_supplies", null, null), Is.True);
}
