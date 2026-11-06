using ElementalLegion.Client.Legion;
using NUnit.Framework;
using UnityEngine;

namespace ElementalLegion.Client.Tests
{
    /// <summary>느린 이동과 반대 방향 입력에서 몸체의 회전이 튀지 않는지 확인한다.</summary>
    public sealed class ElementFacingTests
    {
        [TestCase(30)]
        [TestCase(60)]
        [TestCase(144)]
        public void SlowStraightMovementAndReversalReachTheHeadingAtALimitedRate(int framesPerSecond)
        {
            const float turnRate = 360;
            float elapsed = 1f / framesPerSecond;
            float yaw = 269.999f;
            foreach (var velocity in new[] { Vector3.right, Vector3.left })
            {
                for (int frame = 0; frame < framesPerSecond; frame++)
                {
                    float next = ElementalVisuals.AdvanceFacingYaw(yaw, velocity, 9, elapsed, turnRate);
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, next)), Is.LessThanOrEqualTo(turnRate * elapsed + .001f));
                    yaw = next;
                }
                float target = velocity.x > 0 ? 90 : 270;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, target)), Is.LessThan(.001f));
            }

            float idle = ElementalVisuals.AdvanceFacingYaw(yaw, Vector3.zero, 9, elapsed, turnRate);
            Assert.That(Mathf.DeltaAngle(yaw, idle), Is.EqualTo(9 * elapsed).Within(.001f));
        }
    }
}
