using ElementalLegion.Client.Input;
using NUnit.Framework;

namespace ElementalLegion.Client.Tests
{
    public sealed class SquadShortcutTests
    {
        [TestCase(1, 1, 1)]
        [TestCase(2, 2, 2)]
        [TestCase(4, 4, 3)]
        [TestCase(8, 8, 4)]
        [TestCase(16, 7, 0)]
        public void SingleKeySelectsOnceOnRelease(byte key, byte selection, byte candidate)
        {
            var state = new SquadShortcutState();
            Assert.That(state.Update(key, key, 0, true).SelectionMask, Is.Zero);
            Assert.That(state.PendingFromSquad, Is.EqualTo(candidate));
            Assert.That(state.Update(key, key, 0, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, 0, key, true).SelectionMask, Is.EqualTo(selection));
            Assert.That(state.PendingFromSquad, Is.Zero);
            Assert.That(state.Update(0, 0, 0, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, key, key, true).SelectionMask, Is.EqualTo(selection));
        }

        [Test]
        public void HeldSourceSendsOncePerFreshDestinationPressAndConsumesBothReleases()
        {
            var state = new SquadShortcutState();
            state.Update(8, 8, 0, true);
            var first = state.Update(10, 2, 0, true);
            Assert.That(first.FromSquad, Is.EqualTo(4));
            Assert.That(first.TargetSquad, Is.EqualTo(2));
            Assert.That(first.SelectionMask, Is.Zero);
            Assert.That(state.Update(10, 2, 0, true).TargetSquad, Is.Zero);
            Assert.That(state.Update(8, 0, 2, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(8, 2, 2, true).TargetSquad, Is.EqualTo(2));
            Assert.That(state.Update(9, 1, 0, true).TargetSquad, Is.EqualTo(1));
            Assert.That(state.Update(1, 0, 8, true).SelectionMask, Is.Zero);
            Assert.That(state.PendingFromSquad, Is.Zero);
            Assert.That(state.Update(0, 0, 1, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, 4, 4, true).SelectionMask, Is.EqualTo(4));
        }

        [TestCase(0, 3)]
        [TestCase(1, 6)]
        [TestCase(1, 16)]
        [TestCase(16, 1)]
        public void AmbiguousOrAllFieldChordConsumesSelection(byte source, byte other)
        {
            var state = new SquadShortcutState();
            if (source != 0) state.Update(source, source, 0, true);
            var ambiguous = state.Update((byte)(source | other), other, 0, true);
            Assert.That(ambiguous.TargetSquad, Is.Zero);
            Assert.That(state.PendingFromSquad, Is.Zero);
            Assert.That(state.Update(other, 0, source, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, 0, other, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, 16, 16, true).SelectionMask, Is.EqualTo(7));
        }

        [Test]
        public void SimultaneousShortTapsAllowTheNextFreshPress()
        {
            var state = new SquadShortcutState();
            var ambiguous = state.Update(0, 3, 3, true);
            Assert.That(ambiguous.SelectionMask, Is.Zero);
            Assert.That(ambiguous.TargetSquad, Is.Zero);
            Assert.That(state.Update(0, 4, 4, true).SelectionMask, Is.EqualTo(4));
        }

        [Test]
        public void SimultaneousSourceReleaseAndDestinationPressDoesNotGuessOrder()
        {
            var state = new SquadShortcutState();
            state.Update(1, 1, 0, true);
            var result = state.Update(2, 2, 1, true);
            Assert.That(result.SelectionMask, Is.Zero);
            Assert.That(result.TargetSquad, Is.Zero);
            Assert.That(state.Update(0, 0, 2, true).SelectionMask, Is.Zero);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BlockingOrResetClearsCandidateUntilTheOldKeysAreReleased(bool reset)
        {
            var state = new SquadShortcutState();
            state.Update(8, 8, 0, true);
            if (reset) state.Reset();
            else state.Update(8, 0, 0, false);
            Assert.That(state.PendingFromSquad, Is.Zero);
            Assert.That(state.Update(10, 2, 0, true).TargetSquad, Is.Zero);
            Assert.That(state.Update(0, 0, 10, true).SelectionMask, Is.Zero);
            Assert.That(state.Update(0, 1, 1, true).SelectionMask, Is.EqualTo(1));
        }
    }
}
