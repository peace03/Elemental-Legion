using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>밀집할 때 상태 껍질이 유닛 표시 크기를 따르는지 확인한다.</summary>
    public sealed class FeedbackStatusVisualTests
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

        [Test]
        public void CrowdedStatusShellsFollowFootprintWithoutShieldOwnerRings()
        {
            var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var visuals = root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var matrices = (Matrix4x4[][])typeof(ElementalVisuals).GetField("matrices", Hidden).GetValue(visuals);
                var counts = (int[])typeof(ElementalVisuals).GetField("counts", Hidden).GetValue(visuals);
                int ice = Constant("StatusIce"), shield = Constant("StatusShield");
                visuals.Render(Timeline(rules, 1), 1, new System.Numerics.Vector2(100), 1);
                float iceWidth = matrices[ice][0].GetColumn(0).magnitude;
                float shieldWidth = matrices[shield][0].GetColumn(0).magnitude;
                visuals.Render(Timeline(rules, 2), 1, new System.Numerics.Vector2(100), 1);
                float minimum = new SerializedObject(visuals).FindProperty("crowdedFootprintMinimum").floatValue;
                Assert.That(counts[ice], Is.EqualTo(2));
                Assert.That(counts[shield], Is.EqualTo(2));
                for (int index = 0; index < 2; index++)
                {
                    Assert.That(matrices[ice][index].GetColumn(0).magnitude, Is.EqualTo(iceWidth * minimum).Within(.0001f));
                    Assert.That(matrices[shield][index].GetColumn(0).magnitude, Is.EqualTo(shieldWidth * minimum).Within(.0001f));
                }
                int start = Constant("OwnershipStart"), kinds = Constant("OwnershipKinds");
                for (int slot = 0; slot <= PlayerPalette.Count; slot++)
                    Assert.That(counts[start + slot * kinds + Constant("OwnerRing")], Is.Zero);
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static SnapshotTimeline Timeline(GameRules rules, int count)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketKind.Units); writer.Put(1L); writer.Put(1);
            writer.Put(100f); writer.Put(100f); writer.Put((ushort)count); writer.Put((ushort)0); writer.Put(0u);
            int fire = rules.ElementIndex("fire");
            for (int index = 0; index < count; index++)
            {
                writer.Put(index + 1); writer.Put(1); writer.Put((byte)fire); writer.Put((short)0); writer.Put((short)0);
                writer.Put((byte)0); writer.Put(rules.Elements[fire].Hp); writer.Put(5f); writer.Put((ushort)(1 << 4));
                writer.Put((byte)1); writer.Put(0u);
            }
            var assembler = new UnitSnapshotAssembler(rules);
            Assert.That(assembler.TryAccept(new NetDataReader(writer.CopyData())), Is.True);
            var timeline = new SnapshotTimeline(2); timeline.Push(assembler); return timeline;
        }

        private static int Constant(string name) => (int)typeof(ElementalVisuals).GetField(name, BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
    }
}
