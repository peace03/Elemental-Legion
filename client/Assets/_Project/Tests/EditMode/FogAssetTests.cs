using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using System.Linq;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using LiteNetLib.Utils;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ElementalLegion.Client.Tests
{
    public sealed class FogAssetTests
    {
        [Test]
        public void FogShaderAndProfileAreImported()
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/_Project/Shaders/FogOfWar.shader");
            Assert.That(shader, Is.Not.Null, "M2 볼륨 셰이더");
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(AssetDatabase.LoadMainAssetAtPath("Assets/_Project/Settings/Rendering/SpaceFog.asset"), Is.Not.Null);
        }

        [Test]
        public void PrefabCamerasOwnDifferentMasksAndReleaseTheirResources()
        {
            GameObject first = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            GameObject second = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            try
            {
                GameRules rules = Rules();
                var a = first.GetComponentInChildren<FogPresentation>(true); var b = second.GetComponentInChildren<FogPresentation>(true);
                a.Initialize(rules); b.Initialize(rules);
                Assert.That(a.Mask, Is.Not.SameAs(b.Mask)); Assert.That(a.CameraMaterial, Is.Not.SameAs(b.CameraMaterial));
                var known = new FogStateReplica(rules); var setup = new WorldSetup();
                setup.Players.Add(new PlayerSpawn(1, "water", new System.Numerics.Vector2(100), new System.Numerics.Vector2(100)));
                var world = new SimulationWorld(rules, setup, 1); var sender = new FogReplicationState(rules); Send(sender, known, world.GetView(1)); a.Apply(known);
                Assert.That(a.Mask.GetPixel(10, 10).g, Is.GreaterThan(0.9f)); Assert.That(b.Mask.GetPixel(10, 10).g, Is.Zero);
                a.ResetVision(); Assert.That(a.Mask.GetPixel(10, 10).g, Is.Zero);
                a.Apply(known);
                Texture2D mask = a.Mask; Material material = a.CameraMaterial;
                a.gameObject.SetActive(false);
                // 런타임 OnDisable이 사용하는 수명 경계를 Play 진입 없이 검증한다.
                a.ReleaseResources();
                Assert.That(a.Mask == null && a.CameraMaterial == null, Is.True);
                Assert.That(mask == null && material == null, Is.True, "GPU 자원의 네이티브 객체도 해제해야 합니다.");
                a.gameObject.SetActive(true); a.Initialize(rules);
                Assert.That(a.Mask.GetPixel(10,10).g, Is.GreaterThan(0.9f));
            }
            finally { DestroyFixture(first); DestroyFixture(second); }
        }

        [Test]
        public void RenderGraphProducesBlackUnknownDarkExploredAndReadableVisibleTerrain()
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            var groundMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")); groundMaterial.SetColor("_BaseColor", Color.white);
            ground.transform.position = new UnityEngine.Vector3(100, 0, 100); ground.transform.localScale = UnityEngine.Vector3.one * 100;
            ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
            var target = new RenderTexture(128, 128, 24); target.Create(); var readback = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            RenderTexture original = RenderTexture.active;
            try
            {
                var camera = root.GetComponentInChildren<UnityEngine.Camera>();
                camera.transform.position = new UnityEngine.Vector3(105, 48, 105); camera.transform.rotation = Quaternion.Euler(90, 0, 0); camera.orthographicSize = 5;
                var fog = camera.GetComponent<FogPresentation>(); GameRules rules = Rules(); fog.Initialize(rules);
                float unknown = Render(camera, target, readback);
                Assert.That(unknown, Is.LessThan(0.005f), "미탐색 영역은 흰 지형 위에서도 검정이어야 합니다.");
                var feature = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/_Project/Settings/Rendering/PC_Renderer.asset").rendererFeatures.OfType<FogOfWarRendererFeature>().Single();
                feature.Dispose(); feature.Create();
                Assert.That(Render(camera,target,readback), Is.LessThan(0.005f), "Renderer 재생성 후에도 카메라의 시야 등록을 유지해야 합니다.");
                var setup = new WorldSetup(); setup.Players.Add(new PlayerSpawn(1, "water", new System.Numerics.Vector2(500), new System.Numerics.Vector2(100)));
                setup.Units.Add(new UnitSpawn(1, "water", new System.Numerics.Vector2(100)));
                var world = new SimulationWorld(rules, setup, 1); var sender = new FogReplicationState(rules); var replica = new FogStateReplica(rules);
                Send(sender, replica, world.GetView(1)); fog.Apply(replica); float visible = Render(camera, target, readback);
                world.TryEnqueue(1, SimCommand.Move(1, System.Numerics.Vector2.UnitX)); for (int tick = 0; tick < 220; tick++) world.Step();
                Send(sender, replica, world.GetView(1)); fog.Apply(replica); float explored = Render(camera, target, readback);
                Assert.That(visible, Is.GreaterThan(0.15f), "현재 시야 지형의 읽기성");
                Assert.That(explored, Is.GreaterThan(unknown + 0.01f)); Assert.That(explored, Is.LessThan(visible * 0.8f));
                TestContext.WriteLine($"D3D11 center linear luminance unknown={unknown:F4}, explored={explored:F4}, visible={visible:F4}");
                foreach (var canvas in root.GetComponentsInChildren<Canvas>(true)) Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
            }
            finally
            {
                RenderTexture.active = original; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(readback);
                DestroyFixture(root); Object.DestroyImmediate(ground); Object.DestroyImmediate(groundMaterial);
            }
        }

        [Test]
        public void NonIntegralMapCellScaleKeepsUnexploredTerrainOpaque()
        {
            GameObject root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            var material = new Material(Shader.Find("Universal Render Pipeline/Unlit")); material.SetColor("_BaseColor", Color.white);
            ground.transform.position = new UnityEngine.Vector3(995,0,995); ground.transform.localScale = UnityEngine.Vector3.one * 100;
            ground.GetComponent<MeshRenderer>().sharedMaterial = material;
            var target = new RenderTexture(128,128,24); target.Create(); var pixel = new Texture2D(1,1,TextureFormat.RGBA32,false,true);
            RenderTexture original = RenderTexture.active;
            try
            {
                var document = new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text);
                document.Root["world"]["mapSize"] = 1681;
                var rules = new GameRules(new GameDataDocument(document.Root.ToString()));
                var setup = new WorldSetup(); var source = new System.Numerics.Vector2(1030,995);
                setup.Players.Add(new PlayerSpawn(1,"water",source,source)); var world = new SimulationWorld(rules,setup,1);
                Assert.That(world.GetView(1).Vision.GetState(99,99), Is.EqualTo(VisionCellState.Unexplored));
                Assert.That(world.GetView(1).Vision.GetState(100,100), Is.EqualTo(VisionCellState.Visible));
                var camera = root.GetComponentInChildren<UnityEngine.Camera>(); camera.transform.position = new UnityEngine.Vector3(995,48,995);
                camera.transform.rotation = Quaternion.Euler(90,0,0); camera.orthographicSize = 5;
                var fog = camera.GetComponent<FogPresentation>(); fog.Initialize(rules);
                var replica = new FogStateReplica(rules); Send(new FogReplicationState(rules),replica,world.GetView(1)); fog.Apply(replica);
                Assert.That(Render(camera,target,pixel), Is.LessThan(0.005f), "유효한 비정수 맵 격자에서도 이웃 셀의 시야가 미탐색 지형을 열면 안 됩니다.");
            }
            finally
            {
                RenderTexture.active = original; target.Release(); Object.DestroyImmediate(target); Object.DestroyImmediate(pixel);
                DestroyFixture(root); Object.DestroyImmediate(ground); Object.DestroyImmediate(material);
            }
        }

        [Test]
        public void QualityPresetsAndExistingRendererFeaturesArePreserved()
        {
            var settings = Object.Instantiate(AssetDatabase.LoadAssetAtPath<FogSettings>(M2SceneSetup.SettingsPath));
            try
            {
                Assert.That(settings.Height, Is.EqualTo(24));
                var serialized = new SerializedObject(settings);
                foreach (FogQuality quality in new[] { FogQuality.Low, FogQuality.Normal, FogQuality.High })
                {
                    serialized.FindProperty("quality").enumValueIndex = (int)quality; serialized.ApplyModifiedPropertiesWithoutUndo();
                    Assert.That(settings.Samples, Is.EqualTo(quality == FogQuality.Low ? 16 : quality == FogQuality.Normal ? 32 : 64));
                    Assert.That(settings.Downsample, Is.EqualTo(quality == FogQuality.High ? 1 : 2));
                }
                var pc = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/_Project/Settings/Rendering/PC_Renderer.asset");
                Assert.That(pc.rendererFeatures.Count(feature => feature is FogOfWarRendererFeature), Is.EqualTo(1));
                Assert.That(pc.rendererFeatures.Any(feature => feature != null && feature.GetType().Name == "ScreenSpaceAmbientOcclusion"), Is.True);
            }
            finally { Object.DestroyImmediate(settings); }
        }

        private static GameRules Rules() => new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
        private static void Send(FogReplicationState sender, FogStateReplica replica, WorldView view)
        {
            sender.Prepare(view); var writer = new NetDataWriter();
            for (int part = 0; part < sender.ChunkCount; part++) { sender.WriteChunk(writer, part); replica.TryAccept(new NetDataReader(writer.CopyData())); }
            sender.Commit();
        }
        private static float Render(UnityEngine.Camera camera, RenderTexture target, Texture2D readback)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture.active = target; readback.ReadPixels(new Rect(64, 64, 1, 1), 0, 0); readback.Apply();
            return readback.GetPixel(0, 0).grayscale;
        }
        private static void DestroyFixture(GameObject root)
        {
            foreach (FogPresentation fog in root.GetComponentsInChildren<FogPresentation>(true)) fog.ReleaseResources();
            Object.DestroyImmediate(root);
        }
    }
}
