using System.Reflection;
using ElementalLegion.Client.Legion;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using WorldPoint = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    public sealed class MoveDestinationMarkerTests
    {
        [Test]
        public void ClickStartsMarkerDragMovesItAndClearRemovesIt()
        {
            var root = new GameObject("Move marker test");
            try
            {
                var visuals = root.AddComponent<ElementalVisuals>();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var known = typeof(ElementalVisuals).GetField("moveMarkerKnown", flags);
                var position = typeof(ElementalVisuals).GetField("moveMarkerPosition", flags);
                var started = typeof(ElementalVisuals).GetField("moveMarkerStart", flags);
                var until = typeof(ElementalVisuals).GetField("moveMarkerUntil", flags);

                visuals.ShowMoveDestination(new WorldPoint(5, 7), false);
                Assert.That(known.GetValue(visuals), Is.False);
                visuals.ShowMoveDestination(new WorldPoint(5, 7), true);
                Assert.That(known.GetValue(visuals), Is.True);
                Assert.That((WorldPoint)position.GetValue(visuals), Is.EqualTo(new WorldPoint(5, 7)));

                started.SetValue(visuals, -1f);
                until.SetValue(visuals, (float)Time.realtimeSinceStartupAsDouble + 5);
                visuals.ShowMoveDestination(new WorldPoint(9, 11), false);
                Assert.That((WorldPoint)position.GetValue(visuals), Is.EqualTo(new WorldPoint(9, 11)));
                Assert.That((float)started.GetValue(visuals), Is.EqualTo(-1f));

                visuals.ShowMoveDestination(new WorldPoint(float.NaN, 11), true);
                Assert.That((WorldPoint)position.GetValue(visuals), Is.EqualTo(new WorldPoint(9, 11)));
                visuals.ShowMoveDestination(new WorldPoint(9, 11), true);
                Assert.That((float)started.GetValue(visuals), Is.GreaterThanOrEqualTo(0));
                started.SetValue(visuals, -1f);
                until.SetValue(visuals, 0f);
                visuals.ShowMoveDestination(new WorldPoint(9, 11), false);
                Assert.That((float)started.GetValue(visuals), Is.EqualTo(-1f));
                visuals.ShowMoveDestination(new WorldPoint(12, 14), false);
                Assert.That((float)started.GetValue(visuals), Is.GreaterThanOrEqualTo(0));
                visuals.Clear();
                Assert.That(known.GetValue(visuals), Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
