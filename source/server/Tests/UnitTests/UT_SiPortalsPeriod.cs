using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    // HearthDAoC: the Shrouded Isles portals are the client's own 1.65 portals on the Avalon Marsh pier, in Gotar and
    // in Shannon Estuary, whose jump lines send the three entrance zone points themselves. The server has no walk-on
    // pads at upstream's town spots (Cotswold, Mularn, Mag Mell), so bots cross at each zone point's source spot, which
    // deploy/bin/mob_fixes.json moves to the 1.65 portal (deploy/tests/test_si_portals.py).
    [TestFixture]
    public sealed class UT_SiPortalsPeriod
    {
        [Test]
        public void ThereAreNoWalkOnPads() =>
            Assert.That(ShroudedIslesPortals.Pads, Is.Empty);

        [TestCase((ushort)153, TestName = "Albion has no pad in Cotswold")]
        [TestCase((ushort)165, TestName = "Midgard has no pad in Mularn")]
        [TestCase((ushort)167, TestName = "Hibernia has no pad in Mag Mell")]
        public void AnEntranceHasNoPad(ushort zonePointId) =>
            Assert.That(ShroudedIslesPortals.TryGetPad(zonePointId, out _), Is.False);

        // An upstream sync that changes them: check the zone points mob_fixes.json moves.
        [Test]
        public void TheEntrancesAreStillUpstreamsThree() =>
            Assert.That(ShroudedIslesPortals.EntranceIds, Is.EqualTo(new ushort[] { 153, 165, 167 }));
    }
}
