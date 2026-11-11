Shader "ElementalLegion/FacilityEnergy"
{
    Properties
    {
        [HDR] _BaseColor("기본 발광 색", Color) = (.035,.53,1.55,1)
        [HDR] _PeakColor("밝은 발광 색", Color) = (.28,1.95,3.95,1)
        [Toggle(_FACILITY_SPARK)] _Spark("포집 입자 모드", Float) = 0
        _FlowSpeed("유동 속도(주기/초)", Range(0,5)) = 1.4
        _FlowRepeats("유동선 하나의 반복 수", Range(1,12)) = 2.6
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForwardOnly" }
            Blend One One
            ColorMask RGB
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local _FACILITY_SPARK
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _PeakColor;
                float _Spark;
                float _FlowSpeed;
                float _FlowRepeats;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(input.positionOS);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE facing : FRONT_FACE_SEMANTIC) : SV_Target
            {
                #if defined(_FACILITY_SPARK)
                    float2 sparkOffset = input.uv * 2.0 - 1.0;
                    float distanceSquared = dot(sparkOffset, sparkOffset);
                    half halo = 1.0h - smoothstep(.025, .86, distanceSquared);
                    half core = 1.0h - smoothstep(.0, .23, distanceSquared);
                    half3 color = lerp(_BaseColor.rgb, _PeakColor.rgb, core);
                    return half4(color * input.color.rgb * (halo * halo * input.color.a), 0);
                #else
                    // 꼭대기에서 시작하는 UV를 따라 밝은 부분이 바닥으로 흐른다.
                    clip(IS_FRONT_VFACE(facing, 1.0, -1.0));
                    float phase = frac(input.uv.x * _FlowRepeats - _Time.y * _FlowSpeed + input.uv.y);
                    half pulse = smoothstep(.57, .94, phase) * (1.0h - smoothstep(.94, 1.0, phase));
                    half fade = smoothstep(.0, .15, input.uv.x) * (1.0h - smoothstep(.84, 1.0, input.uv.x));
                    half3 color = lerp(_BaseColor.rgb * .62h, _PeakColor.rgb, pulse);
                    return half4(color * (fade * input.color.a), 0);
                #endif
            }
            ENDHLSL
        }
    }
}
