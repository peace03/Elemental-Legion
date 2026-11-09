using ElementalLegion.Client.Legion;
using NUnit.Framework;

namespace ElementalLegion.Client.Tests
{
    public sealed class SquadControlTests
    {
        [Test]
        public void SelectionStopsOnlyTheLastDirectMaskAndIdleNeverCancelsPointOrders()
        {
            var controls = new SquadControlState();
            Assert.That(controls.NextDirect(false, out _), Is.False);
            Assert.That(controls.NextDirect(true, out byte mask), Is.True);
            Assert.That(mask, Is.EqualTo(1));
            Assert.That(controls.Select(4), Is.EqualTo(1));
            Assert.That(controls.Representative, Is.EqualTo(3));
            Assert.That(controls.NextDirect(false, out _), Is.False);
            Assert.That(controls.Select(7), Is.Zero);
            Assert.That(controls.Representative, Is.EqualTo(1));
            Assert.That(controls.NextDirect(true, out mask), Is.True);
            Assert.That(mask, Is.EqualTo(7));
            Assert.That(controls.NextDirect(false, out mask), Is.True);
            Assert.That(mask, Is.EqualTo(7));
            Assert.That(controls.NextDirect(false, out _), Is.False);
        }

        [Test]
        public void RightClickPlacementCancellationConsumesTheClickBeforeWorldMovement()
        {
            Assert.That(SquadControlState.ShouldMoveTo(true, false, true), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(true, true, false), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(false, false, false), Is.False);
            Assert.That(SquadControlState.ShouldMoveTo(true, false, false), Is.True);
        }
    }
}
