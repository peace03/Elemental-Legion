using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object=UnityEngine.Object;

namespace ElementalLegion.Client.Tests
{
    /// <summary>동결·방어막 껍질이 원소 본체 및 본체 입자에 섞이지 않는지 확인한다.</summary>
    public sealed class ElementStatusBatchTests
    {
        [Test]
        public void StatusShellUsesNeutralGeometryAndItsOwnTransparentMaterial()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);
            var settings=new SerializedObject(prefab.GetComponent<ElementalVisuals>());
            var mesh=(Mesh)settings.FindProperty("domeMesh").objectReferenceValue;
            var shell=(Material)settings.FindProperty("statusShell").objectReferenceValue;
            Assert.That(shell,Is.Not.Null);
            Assert.That(shell.shader.name,Is.EqualTo("ElementalLegion/ElementStatusShell"));
            Assert.That(ShaderUtil.ShaderHasError(shell.shader),Is.False);
            Assert.That(shell.enableInstancing,Is.True);
            Assert.That(shell.renderQueue,Is.GreaterThanOrEqualTo((int)UnityEngine.Rendering.RenderQueue.Transparent));
            var profiles=settings.FindProperty("profiles");
            for(int index=0;index<profiles.arraySize;index++)
            {
                var profile=(ElementArtProfile)profiles.GetArrayElementAtIndex(index).objectReferenceValue;
                Assert.That(mesh,Is.Not.EqualTo(profile.Mesh),profile.ElementId+"의 본체를 상태 외피로 쓰면 안 됩니다.");
                Assert.That(shell,Is.Not.EqualTo(profile.Body));
                Assert.That(shell,Is.Not.EqualTo(profile.Particles));
            }
        }

        [TestCase("water")]
        [TestCase("dark_matter")]
        public void StatusOverlaysDoNotDependOnOtherElementBodies(string elementId)
        {
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            var copies=new ElementArtProfile[2];
            try
            {
                var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var visuals=root.GetComponent<ElementalVisuals>();
                var settings=new SerializedObject(visuals);
                var profiles=settings.FindProperty("profiles");
                string[] overlayElements={"ice","magnetism"};
                for(int index=0;index<copies.Length;index++)
                {
                    var slot=profiles.GetArrayElementAtIndex(rules.ElementIndex(overlayElements[index]));
                    copies[index]=Object.Instantiate((ElementArtProfile)slot.objectReferenceValue);
                    slot.objectReferenceValue=copies[index];
                }
                settings.ApplyModifiedPropertiesWithoutUndo();
                visuals.Initialize();
                foreach(var copy in copies)
                {
                    // 상태 외피가 다른 원소의 본체를 다시 그리면 이 참조에서 실패한다.
                    var profile=new SerializedObject(copy);
                    profile.FindProperty("mesh").objectReferenceValue=null;
                    profile.ApplyModifiedPropertiesWithoutUndo();
                }
                Assert.DoesNotThrow(()=>visuals.Render(Timeline(rules,2,elementId),1,new System.Numerics.Vector2(100),1),
                    "보호막과 빙결은 자기장·얼음 유닛 본체와 독립적으로 그려야 합니다.");
                var counts=(int[])typeof(ElementalVisuals).GetField("counts",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                Assert.That(counts[rules.ElementIndex(elementId)],Is.EqualTo(1));
                Assert.That(counts[Constant("StatusIce")],Is.EqualTo(1));
                Assert.That(counts[Constant("StatusShield")],Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(root);
                foreach(var copy in copies)if(copy!=null)Object.DestroyImmediate(copy);
            }
        }

        [TestCase(130)]
        [TestCase(Protocol.MaxUnits)]
        public void StatusShellsHaveTheirOwnBatchesAndSkipGhosts(int unitCount)
        {
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var visuals=root.GetComponent<ElementalVisuals>();
                visuals.Initialize();
                var timeline=Timeline(rules,unitCount);
                Assert.DoesNotThrow(()=>visuals.Render(timeline,1,new System.Numerics.Vector2(100),1));
                var counts=(int[])typeof(ElementalVisuals).GetField("counts",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(visuals);
                int fire=rules.ElementIndex("fire"),ice=rules.ElementIndex("ice"),magnetism=rules.ElementIndex("magnetism");
                Assert.That(counts[fire],Is.EqualTo(unitCount-1));
                Assert.That(counts[ice],Is.Zero,"동결 껍질을 얼음 유닛으로 세면 안 됩니다.");
                Assert.That(counts[magnetism],Is.Zero,"방어막을 자기장 유닛으로 세면 안 됩니다.");
                Assert.That(counts[Constant("StatusIce")],Is.EqualTo(unitCount-1));
                Assert.That(counts[Constant("StatusShield")],Is.EqualTo(unitCount-1));
                foreach(var system in root.GetComponentsInChildren<ParticleSystem>(true))
                    if(system.name=="ice Body Particles"||system.name=="magnetism Body Particles")
                        Assert.That(system.particleCount,Is.Zero,"상태 껍질 때문에 다른 원소 본체 입자가 생기면 안 됩니다.");
                Assert.That(visuals.ActiveParticles,Is.LessThanOrEqualTo(32768));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static SnapshotTimeline Timeline(GameRules rules,int count,string elementId="fire")
        {
            var assembler=new UnitSnapshotAssembler(rules);var writer=new NetDataWriter();
            int fire=rules.ElementIndex(elementId);
            for(int part=0;part<Protocol.ChunkCount(count);part++)
            {
                writer.Reset();writer.Put((byte)PacketKind.Units);writer.Put(1L);writer.Put(1);
                writer.Put(100f);writer.Put(100f);writer.Put((ushort)count);writer.Put((ushort)part);writer.Put(0u);
                for(int index=part*Protocol.UnitsPerChunk;index<Mathf.Min(count,(part+1)*Protocol.UnitsPerChunk);index++)
                {
                    writer.Put(index+1);writer.Put(1);writer.Put((byte)fire);writer.Put((short)0);writer.Put((short)0);
                    writer.Put((byte)(index==count-1?4:0));writer.Put(rules.Elements[fire].Hp);writer.Put(5f);writer.Put((ushort)(1<<4));
                    writer.Put((byte)1);writer.Put(0u);
                }
                bool complete=assembler.TryAccept(new NetDataReader(writer.CopyData()));
                Assert.That(complete,Is.EqualTo(part==Protocol.ChunkCount(count)-1));
            }
            var timeline=new SnapshotTimeline(2);timeline.Push(assembler);return timeline;
        }

        private static int Constant(string name)=>(int)typeof(ElementalVisuals).GetField(name,BindingFlags.NonPublic|BindingFlags.Static).GetRawConstantValue();
    }
}
