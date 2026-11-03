using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace ElementalLegion.Client.FogOfWar
{
    /// <summary>투명 월드 표현 이후, 후처리 이전에 카메라별 3D 연무를 적분하고 합성한다.</summary>
    public sealed class FogOfWarRendererFeature : ScriptableRendererFeature
    {
        private readonly Dictionary<UnityEngine.Camera, FogPresentation> cameras = new Dictionary<UnityEngine.Camera, FogPresentation>();
        private FogPass pass;
        public void Register(UnityEngine.Camera camera, FogPresentation presentation) { cameras[camera] = presentation; }
        public void Unregister(UnityEngine.Camera camera, FogPresentation presentation)
        { if (cameras.TryGetValue(camera, out FogPresentation current) && current == presentation) cameras.Remove(camera); }
        public override void Create() { pass?.ReleaseProfilers(); pass = new FogPass(); }
        public void SetProfiling(bool enabled) { if (pass != null) pass.SetProfiling(enabled); }
        public float GpuMilliseconds => pass != null ? pass.GpuMilliseconds : 0;
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType != CameraType.Game || renderingData.cameraData.renderType != CameraRenderType.Base) return;
            if (!cameras.TryGetValue(renderingData.cameraData.camera, out FogPresentation presentation) || presentation == null || !presentation.Prepare()) return;
            pass.Presentation = presentation; renderer.EnqueuePass(pass);
        }
        // URP는 RendererData 재생성 때 같은 Feature를 Dispose→Create한다.
        // 카메라 등록은 Presentation 수명에 속하므로 pass 자원과 함께 지우지 않는다.
        protected override void Dispose(bool disposing) { pass?.ReleaseProfilers(); pass = null; }

        private sealed class FogPass : ScriptableRenderPass
        {
            private static readonly int DepthId = Shader.PropertyToID("_FogDepth");
            private static readonly int ColorId = Shader.PropertyToID("_FogSceneColor");
            private static readonly int VolumeId = Shader.PropertyToID("_FogVolume");
            private static readonly int SizeId = Shader.PropertyToID("_FogVolumeSize");
            public FogPresentation Presentation;
            private readonly ProfilingSampler raySampler = new ProfilingSampler("Space Fog Raymarch");
            private readonly ProfilingSampler compositeSampler = new ProfilingSampler("Space Fog Depth Composite");
            public float GpuMilliseconds => raySampler.gpuElapsedTime + compositeSampler.gpuElapsedTime;
            public void SetProfiling(bool enabled) { raySampler.enableRecording = enabled; compositeSampler.enableRecording = enabled; }
            public void ReleaseProfilers() { raySampler.Dispose(); compositeSampler.Dispose(); }
            public FogPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                ConfigureInput(ScriptableRenderPassInput.Depth); requiresIntermediateTexture = true;
            }

            private sealed class PassData
            {
                public Material Material;
                public MaterialPropertyBlock Properties;
                public TextureHandle Depth, Color, Volume;
                public Vector4 VolumeSize;
                public int Index;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                UniversalResourceData resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer || !resources.cameraDepthTexture.IsValid()) return;
                FogPresentation owner = Presentation;
                TextureHandle source = resources.activeColorTexture;
                TextureDesc descriptor = graph.GetTextureDesc(source);
                int fullWidth = descriptor.width, fullHeight = descriptor.height;
                descriptor.name = "Space Fog Integral"; descriptor.width = Mathf.Max(1, fullWidth / owner.Downsample); descriptor.height = Mathf.Max(1, fullHeight / owner.Downsample);
                descriptor.colorFormat = GraphicsFormat.R16G16B16A16_SFloat; descriptor.msaaSamples = MSAASamples.None; descriptor.depthBufferBits = DepthBits.None; descriptor.clearBuffer = false;
                TextureHandle volume = graph.CreateTexture(descriptor);
                Vector4 volumeSize = new Vector4(1f / descriptor.width, 1f / descriptor.height, descriptor.width, descriptor.height);
                AddPass(graph, owner.CameraMaterial, owner.RayProperties, 0, resources.cameraDepthTexture, TextureHandle.nullHandle, TextureHandle.nullHandle, volume, volumeSize, raySampler);
                descriptor = graph.GetTextureDesc(source); descriptor.name = "Space Fog Composite"; descriptor.clearBuffer = false; descriptor.msaaSamples = MSAASamples.None; descriptor.depthBufferBits = DepthBits.None;
                TextureHandle destination = graph.CreateTexture(descriptor);
                AddPass(graph, owner.CameraMaterial, owner.CompositeProperties, 1, resources.cameraDepthTexture, source, volume, destination, volumeSize, compositeSampler);
                // 다음 URP 후처리는 합성된 색을 입력으로 사용한다. 원본 색과 같은 자원에 읽고 쓰지 않는다.
                resources.cameraColor = destination;
            }

            private static void AddPass(RenderGraph graph, Material material, MaterialPropertyBlock properties, int index, TextureHandle depth, TextureHandle color, TextureHandle volume, TextureHandle destination, Vector4 size, ProfilingSampler sampler)
            {
                using (IRasterRenderGraphBuilder builder = graph.AddRasterRenderPass<PassData>(index == 0 ? "Space Fog Raymarch" : "Space Fog Depth Composite", out PassData data, sampler))
                {
                    data.Material = material; data.Properties = properties; data.Depth = depth; data.Color = color; data.Volume = volume; data.VolumeSize = size; data.Index = index;
                    builder.UseTexture(depth, AccessFlags.Read);
                    if (color.IsValid()) builder.UseTexture(color, AccessFlags.Read);
                    if (volume.IsValid()) builder.UseTexture(volume, AccessFlags.Read);
                    builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (PassData payload, RasterGraphContext context) =>
                    {
                        MaterialPropertyBlock block = payload.Properties; block.Clear();
                        block.SetTexture(DepthId, (Texture)payload.Depth);
                        if (payload.Color.IsValid()) block.SetTexture(ColorId, (Texture)payload.Color);
                        if (payload.Volume.IsValid()) block.SetTexture(VolumeId, (Texture)payload.Volume);
                        block.SetVector(SizeId, payload.VolumeSize);
                        context.cmd.DrawProcedural(Matrix4x4.identity, payload.Material, payload.Index, MeshTopology.Triangles, 3, 1, block);
                    });
                }
            }
        }
    }
}
