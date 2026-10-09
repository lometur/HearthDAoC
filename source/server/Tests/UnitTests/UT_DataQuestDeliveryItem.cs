using System;
using DOL.GS.Quests;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC: a Deliver or DeliverFinish step's item is handed when the step begins only if the player doesn't carry
// it and the step just finishing isn't handing it ("Traveler's Way -- Supply Run" gave two bundles of supplies at
// Thol Dunnin; 77 classic quests, owner test 2026-10-09). The copy being handed over to finish a step doesn't count as
// carried, so a step that hands the same item back still does ("Path of the Renegade" and 61 more classic quests).
// A quest whose first step is a delivery hands that step's item
// when it is accepted, which upstream never did (56 classic quests could not be finished, among them the level 30
// Regal Nobility), unless that delivery goes back to the giver, who wants the player to bring the item (11 more).
[TestFixture]
public sealed class UT_DataQuestDeliveryItem
{
    private static readonly string[] Nothing = Array.Empty<string>();
    private const string Target = "Lady Elaine";
    private const string Giver = "Master Edric";

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

    // "Path of the Renegade", stage 4 -> 5: the player gives the Arawnite orders to Omis (Deliver), who writes between
    // the lines and gives them back for Captain Rhodri (DeliverFinish of the same item). The orders are still in the
    // backpack while the step advances and leave it afterwards, so the new copy must be handed.
    [Test]
    public void DeliverXThenDeliverXHandsTheNewCopy()
    {
        var backpack = QuestDeliveryItems.CarriedIds(new[] { ("cq_arawnite_orders", 1, 1), ("cq_bread", 1, 0) });
        Assert.That(QuestDeliveryItems.ShouldHand("cq_arawnite_orders", backpack, Nothing), Is.True);
    }

    [Test]
    public void AnotherCopyOfTheItemHandedOverStillCounts()
    {
        var backpack = QuestDeliveryItems.CarriedIds(new[] { ("cq_arawnite_orders", 1, 1), ("cq_arawnite_orders", 1, 0) });
        Assert.That(QuestDeliveryItems.ShouldHand("cq_arawnite_orders", backpack, Nothing), Is.False);
    }

    [Test]
    public void TheItemHandedOverIsNotCarried()
        => Assert.That(QuestDeliveryItems.CarriedIds(new[] { ("cq_orders", 1, 1), ("cq_letter", 1, 0) }), Is.EqualTo(new[] { "cq_letter" }));

    // DataQuest hands a stack over whole (RemoveItem takes the item), so it passes the whole count: no copy is carried.
    [Test]
    public void AStackHandedOverWholeIsNotCarried()
        => Assert.That(QuestDeliveryItems.CarriedIds(new[] { ("cq_orders", 3, 3) }), Is.Empty);

    // Only a partial hand-over, which DataQuest never makes, leaves copies that count.
    [Test]
    public void TheCopiesLeftOfAStackAreCarried()
        => Assert.That(QuestDeliveryItems.CarriedIds(new[] { ("cq_orders", 3, 1) }), Is.EqualTo(new[] { "cq_orders" }));

    [Test]
    public void AnItemNotHandedOverIsCarriedWhateverItsCount()
        => Assert.That(QuestDeliveryItems.CarriedIds(new[] { ("cq_a", 1, 0), ("cq_b", 20, 0), ("cq_c", 0, 0) }), Is.EqualTo(new[] { "cq_a", "cq_b", "cq_c" }));

    [Test]
    public void ANullBackpackCarriesNothing()
        => Assert.That(QuestDeliveryItems.CarriedIds(null), Is.Empty);

    [Test]
    public void AFirstDeliveryStepHandsItsItem()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_document", "", "" }, Nothing, Target, Giver), Is.EqualTo("cq_document"));

    [Test]
    public void TheFirstStepItemIsReturnedTrimmed()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { " cq_document " }, Nothing, Target, Giver), Is.EqualTo("cq_document"));

    [Test]
    public void OnlyTheFirstEntryCounts()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "", "cq_document" }, Nothing, Target, Giver), Is.Null);

    [Test]
    public void AFirstStepThatIsNotADeliveryHandsNothing()
        => Assert.That(QuestDeliveryItems.FirstStepItem(false, new[] { "cq_document" }, Nothing, Target, Giver), Is.Null);

    [Test]
    public void AFirstStepItemAlreadyCarriedIsNotHandedAgain()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_document" }, new[] { "CQ_Document" }, Target, Giver), Is.Null);

    [Test]
    public void NoStepItemsHandNothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDeliveryItems.FirstStepItem(true, null, Nothing, Target, Giver), Is.Null);
            Assert.That(QuestDeliveryItems.FirstStepItem(true, Array.Empty<string>(), Nothing, Target, Giver), Is.Null);
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new string[] { null }, Nothing, Target, Giver), Is.Null);
        });
    }

    // 20098 "Sveabone Hilt Sword": Gridash says "Run along and buy me a bronze short sword", and step 1 is the delivery
    // of cq_bronze_short_sword to Gridash.
    [Test]
    public void AFirstDeliveryBackToTheGiverHandsNothing()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_bronze_short_sword", "cq_sveawolf_tooth", "" }, Nothing, "Gridash", "Gridash"), Is.Null);

    [Test]
    public void TheGiverIsFoundWithoutCaseAndWithoutTheRegion()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_red_dagger_map" }, Nothing, "audun", "Audun"), Is.Null);
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_red_dagger_map" }, Nothing, "Audun;101", "Audun"), Is.Null);
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_red_dagger_map" }, Nothing, " Audun ", "AUDUN"), Is.Null);
        });
    }

    [Test]
    public void ADeliveryToSomeoneElseIsHanded()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_letter" }, Nothing, "Gyda", "Gridash"), Is.EqualTo("cq_letter"));

    [Test]
    public void ANameThatOnlyStartsLikeTheGiversIsSomeoneElse()
        => Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_letter" }, Nothing, "Gridash the Elder", "Gridash"), Is.EqualTo("cq_letter"));

    [Test]
    public void AnUnknownTargetOrGiverIsNotTheGiver()
    {
        Assert.Multiple(() =>
        {
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_letter" }, Nothing, null, Giver), Is.EqualTo("cq_letter"));
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_letter" }, Nothing, "", ""), Is.EqualTo("cq_letter"));
            Assert.That(QuestDeliveryItems.FirstStepItem(true, new[] { "cq_letter" }, Nothing, Target, null), Is.EqualTo("cq_letter"));
        });
    }
}
