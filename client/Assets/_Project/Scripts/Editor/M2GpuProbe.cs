using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ElementalLegion.Client.FogOfWar;
using ElementalLegion.Shared.Data;
using ElementalLegion.Shared.Net;
using LiteNetLib.Utils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace ElementalLegion.Client.Editor
{
    /// <summary>Play에 진입하지 않는 별도 씬의 1080p GPU 표본이다. 실제 Player 성능 판정을 대신하지 않는다.</summary>
    public static class M2GpuProbe
    {
        private static Scene fixture, originalScene;
        private static GameObject root, ground;
        private static Material groundMaterial;
        private static UnityEngine.Camera camera;
        private static RenderTexture target;
        private static FogOfWarRendererFeature feature;
        private static bool active, originalGpu;
        private static int frame;
        private static readonly List<float> samples = new List<float>(240);
        private static string path;

        public static void Start(string outputPath = "Temp/M2Work/gpu-probe.json")
        {
            if (root != null || EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("GPU 표본을 시작할 수 없습니다.");
            path = outputPath; Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            originalGpu = UnityEditorInternal.ProfilerDriver.profileGPU;
            samples.Clear(); frame = 0; originalScene = SceneManager.GetActiveScene();
            fixture = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive); SceneManager.SetActiveScene(fixture);
            try
            {
                root = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
                camera = root.GetComponentInChildren<UnityEngine.Camera>(); camera.transform.position = new Vector3(840, 48, 815);
                var rules = new GameRules(new GameDataDocument(AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json").text));
                var fog = camera.GetComponent<FogPresentation>(); fog.Initialize(rules); fog.Apply(KnownMask(rules));
                ground = GameObject.CreatePrimitive(PrimitiveType.Plane); ground.transform.position = new Vector3(840, 0, 840); ground.transform.localScale = Vector3.one * 168;
                groundMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit")); groundMaterial.SetColor("_BaseColor", new Color(0.25f, 0.35f, 0.4f)); ground.GetComponent<MeshRenderer>().sharedMaterial = groundMaterial;
                target = new RenderTexture(1920, 1080, 24) { name = "M2 1080p GPU Probe" }; target.Create();
                feature = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/_Project/Settings/Rendering/PC_Renderer.asset").rendererFeatures.OfType<FogOfWarRendererFeature>().Single();
                active = feature.isActive; originalGpu = UnityEditorInternal.ProfilerDriver.profileGPU; UnityEditorInternal.ProfilerDriver.profileGPU = true;
                feature.SetActive(false); EditorApplication.update += Update;
            }
            catch { Cleanup(); throw; }
        }

        private static void Update()
        {
            try
            {
                if (frame == 180) { feature.SetActive(true); feature.SetProfiling(true); }
                if (frame >= 240 && feature.GpuMilliseconds > 0) samples.Add(feature.GpuMilliseconds);
                RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
                if (frame == 239)
                {
                    RenderTexture previous = RenderTexture.active; RenderTexture.active = target;
                    var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false); image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                    File.WriteAllBytes(Path.ChangeExtension(path, ".png"), image.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(image); RenderTexture.active = previous;
                }
                if (++frame < 480) return;
                samples.Sort();
                string p95 = samples.Count > 0 ? samples[(int)Math.Ceiling(samples.Count * 0.95) - 1].ToString("F4", System.Globalization.CultureInfo.InvariantCulture) : "null";
                File.WriteAllText(path, "{\"mode\":\"Editor offscreen RenderRequest, default half32, 1920x1080\",\"gpu\":\"" + SystemInfo.graphicsDeviceName + "\",\"api\":\"" + SystemInfo.graphicsDeviceType + "\",\"samples\":" + samples.Count + ",\"fogPassP95Ms\":" + p95 + ",\"playerFrameComparison\":\"pending manual Play and Windows Player\"}");
                Cleanup();
            }
            catch (Exception exception) { File.WriteAllText(path, "GPU probe failed: " + exception); Cleanup(); Debug.LogException(exception); }
        }

        private static FogStateReplica KnownMask(GameRules rules)
        {
            var replica = new FogStateReplica(rules); int bytes = (replica.CellCount + 7) / 8; var body = new byte[bytes * 2];
            for (int cell = 0; cell < replica.CellCount; cell++)
            { body[bytes + (cell >> 3)] |= (byte)(1 << (cell & 7)); if (cell % replica.Width < replica.Width / 2) body[cell >> 3] |= (byte)(1 << (cell & 7)); }
            var writer = new NetDataWriter();
            for (int offset = 0; offset < body.Length; offset += FogProtocol.PayloadBytes)
            {
                writer.Reset(); writer.Put((byte)PacketKind.FogBaseline); writer.Put(0L); writer.Put(1); writer.Put(1U); writer.Put(0U); writer.Put(body.Length); writer.Put(offset);
                writer.Put(body, offset, Math.Min(FogProtocol.PayloadBytes, body.Length - offset)); replica.TryAccept(new NetDataReader(writer.CopyData()));
            }
            return replica;
        }

        private static void Cleanup()
        {
            EditorApplication.update -= Update;
            if (feature != null) { feature.SetProfiling(false); feature.SetActive(active); }
            UnityEditorInternal.ProfilerDriver.profileGPU = originalGpu;
            if (root != null) { foreach (FogPresentation fog in root.GetComponentsInChildren<FogPresentation>(true)) fog.ReleaseResources(); UnityEngine.Object.DestroyImmediate(root); }
            if (ground != null) UnityEngine.Object.DestroyImmediate(ground);
            if (groundMaterial != null) UnityEngine.Object.DestroyImmediate(groundMaterial);
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
            if (fixture.IsValid()) EditorSceneManager.CloseScene(fixture, true);
            if (originalScene.IsValid() && originalScene.isLoaded) SceneManager.SetActiveScene(originalScene);
            root = null;
        }
    }
}
