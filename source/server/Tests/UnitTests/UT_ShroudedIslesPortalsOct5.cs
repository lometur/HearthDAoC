using System.Numerics;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>The Cotswold, Mularn and Mag Mell portals are used from their platforms.</summary>
    [TestFixture]
    public class UT_ShroudedIslesPortalsOct5
    {
        // HearthDAoC: no pads, the portals are the client's own 1.65 ones (UT_SiPortalsPeriod); upstream's test was
        // TheThreeEntranceZonePointsHaveAPlatform, with TryGetPad true for each.
        [Test]
        public void NoEntranceHasAPad()
        {
            Assert.That(ShroudedIslesPortals.EntranceIds, Is.EquivalentTo(new ushort[] { 153, 165, 167 }));
            foreach (ushort id in ShroudedIslesPortals.EntranceIds)
                Assert.That(ShroudedIslesPortals.TryGetPad(id, out _), Is.False, $"zone point {id}");
            Assert.That(ShroudedIslesPortals.TryGetPad(1, out _), Is.False, "Other zone points are unchanged.");
        }

        [Test]
        public void YouUseThePortalFromTheMularnPlatformNotTheGroundBesideIt()
        {
            Vector3 pad = new(808830, 725039, 4815); // HearthDAoC: upstream's Mularn pad, a literal (Pads is empty)
            Assert.Multiple(() =>
            {
                Assert.That(ShroudedIslesPortals.IsOnPad(pad, pad + new Vector3(40, -30, 5)), Is.True, "On the platform.");
                Assert.That(ShroudedIslesPortals.IsOnPad(pad, new Vector3(pad.X, pad.Y, 5141)), Is.False, "On top of the arch.");
                Assert.That(ShroudedIslesPortals.IsOnPad(pad, new Vector3(pad.X + 120, pad.Y, pad.Z)), Is.False, "Beside the portal.");
                Assert.That(ShroudedIslesPortals.IsOnPad(pad, new Vector3(pad.X + 150, pad.Y, pad.Z)), Is.False, "Too far from the portal.");
            });
        }

        [TestCase(0L, 1_000L, ExpectedResult = true, TestName = "First use")]
        [TestCase(10_000L, 12_000L, ExpectedResult = false, TestName = "Not again within 5 s")]
        [TestCase(10_000L, 15_000L, ExpectedResult = true, TestName = "Again after 5 s")]
        public bool APlayerIsNotBouncedThroughTwice(long lastUse, long now) =>
            ShroudedIslesPortals.MayUse(lastUse, now);
    }
}
