Shader "ElementalLegion/FacilityGroundGlow"
{
    Properties
    {
        _GlowColor("바닥 발광 색 (선형 RGB)", Vector) = (0,0,0,0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-100"
            "DisableBatching" = "True"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Blend One One
            ColorMask RGB
            Cull Off
            ZTest LEqual
            ZWrite Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            UNITY_INSTANCING_BUFFER_START(Glow)
                UNITY_DEFINE_INSTANCED_PROP(float4, _GlowColor)
            UNITY_INSTANCING_BUFFER_END(Glow)

            struct Attributes
            {
                float3 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 discPosition : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                nointerpolation float3 glowColor : TEXCOORD2;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.discPosition = input.positionOS.xz;
                output.glowColor = UNITY_ACCESS_INSTANCED_PROP(Glow, _GlowColor).rgb;
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // 로컬 반경 1에서 밝기와 변화율이 함께 0이 되어 원판 경계가 드러나지 않는다.
                float radiusSquared = dot(input.discPosition, input.discPosition);
                float edgeFade = saturate(1.0 - radiusSquared);
                float falloff = edgeFade * edgeFade / (1.0 + 3.0 * radiusSquared);
                float fog = InitializeInputDataFog(float4(input.positionWS, 1.0), 0.0);
                float3 glow = MixFogColor(input.glowColor * falloff, float3(0.0, 0.0, 0.0), fog);
                return float4(glow, 0.0);
            }
            ENDHLSL
        }
    }
}
