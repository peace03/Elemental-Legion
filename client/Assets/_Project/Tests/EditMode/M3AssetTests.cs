using System.Linq;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Client.UI;
using ElementalLegion.Shared.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;

namespace ElementalLegion.Client.Tests
{
    public sealed class M3AssetTests
    {
        [Test]
        public void AllElementsHaveDifferentOpticalProfilesAndCompleteReferences()
        {
            var rules=new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
            var styles=new System.Collections.Generic.HashSet<int>();
            foreach(var definition in rules.Elements)
            {
                var profile=AssetDatabase.LoadAssetAtPath<ElementArtProfile>(M3SceneSetup.ArtFolder+"/"+definition.Id+".asset");Assert.That(profile,Is.Not.Null,definition.Id);
                Assert.That(profile.ElementId,Is.EqualTo(definition.Id));Assert.That(styles.Add(profile.Style),Is.True);Assert.That(profile.Body.enableInstancing,Is.True);Assert.That(ShaderUtil.ShaderHasError(profile.Body.shader),Is.False);
                Assert.That(profile.Mesh,Is.Not.Null);Assert.That(profile.Particles.GetTexture("_BaseMap"),Is.Not.Null);
            }
        }
        [Test]
        public void HudUsesServerStateForFourteenCostsAndEmitsEachIntentOnce()
        {
            var document=new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);var rules=new GameRules(document);
            var setup=new WorldSetup();setup.Players.Add(new PlayerSpawn(1,"water",new System.Numerics.Vector2(50),new System.Numerics.Vector2(60),3,999,true,true));
            var world=new SimulationWorld(rules,setup,1);var fogSender=new FogReplicationState(rules);var fog=new FogStateReplica(rules);var writer=new NetDataWriter();fogSender.Prepare(world.GetView(1));
            for(int part=0;part<fogSender.ChunkCount;part++){fogSender.WriteChunk(writer,part);fog.TryAccept(new NetDataReader(writer.CopyData()));}
            var sender=new GameplayReplicationState(rules);var replica=new GameplayReplica(rules);sender.Prepare(world.GetView(1),fogSender.Revision);
            for(int part=0;part<sender.ChunkCount;part++){sender.WriteChunk(writer,part);replica.TryAccept(new NetDataReader(writer.CopyData()),0);}
            Assert.That(replica.TryPublish(fog,out var frame),Is.True);
            GameObject root=Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                var hud=root.GetComponentInChildren<CombatHud>(true);hud.Configure(document,rules);hud.Configure(document,rules);hud.Apply(frame);
                var serialized=new SerializedObject(hud);var labels=serialized.FindProperty("labels");var buttons=serialized.FindProperty("train");Assert.That(labels.arraySize,Is.EqualTo(14));
                for(int index=0;index<14;index++)
                {
                    var label=(TMPro.TMP_Text)labels.GetArrayElementAtIndex(index).objectReferenceValue;
                    Assert.That(label.text,Does.Contain((string)document.Root["elements"][index]["displayName"]));Assert.That(label.text,Does.Contain(rules.TrainingCost(index,frame.MainElement).ToString()));
                }
                int commands=0;hud.TrainRequested+=_=>commands++;((UnityEngine.UI.Button)buttons.GetArrayElementAtIndex(0).objectReferenceValue).onClick.Invoke();Assert.That(commands,Is.EqualTo(1));Assert.That(world.GetView(1).CoreBalance,Is.EqualTo(999));
            }
            finally{replica.Dispose();Object.DestroyImmediate(root);}
        }

        [Test]
        public void CombatHudAndPoolResourcesAreBoundAndReleased()
        {
            GameObject prefab=AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath);Assert.That(prefab.GetComponentInChildren<CombatHud>(true),Is.Not.Null);
            var original=Object.Instantiate(prefab);try
            {
                var visuals=original.GetComponent<ElementalVisuals>();var audio=original.GetComponent<CombatAudio>();visuals.Initialize();Assert.That(visuals.MaxEffects,Is.EqualTo(256));Assert.That(audio.MaxVoices,Is.EqualTo(32));
                Assert.DoesNotThrow(()=>visuals.RenderGallery(14,0,true));
                for(int index=0;index<300;index++)visuals.GalleryOutcome(index%14,true);
                visuals.RenderGallery(14,0,false);Assert.That(visuals.PeakEffects,Is.LessThanOrEqualTo(256));Assert.That(visuals.ActiveParticles,Is.InRange(1,32768));
                Assert.That(original.GetComponentsInChildren<ParticleSystem>(true).Length,Is.GreaterThanOrEqualTo(256));original.SetActive(false);
                Assert.That(original.GetComponentsInChildren<ParticleSystem>(true).Length,Is.LessThan(256));Assert.That(original.GetComponentsInChildren<AudioSource>(true).Length,Is.Zero);
            }
            finally{Object.DestroyImmediate(original);}
        }
    }
}
