Shader "ElementalLegion/ElementStatusShell"
{
    Properties
    {
        _Color("외피 색", Color) = (.5,.88,1,1)
        _Opacity("테두리 불투명도", Range(0,1)) = .45
        [Toggle] _Frost("빙결 균열", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent+5" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Opacity, _Frost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 local : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.local = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float4 color = _Color;
                float opacity = saturate(_Opacity);
                float frost = saturate(_Frost);
                float3 normal = normalize(input.normalWS);
                float3 view = GetWorldSpaceNormalizeViewDir(input.positionWS);
                // 정면은 완전히 비워 원소 본체가 다른 물질로 덮이지 않게 한다.
                float rim = smoothstep(lerp(.72, .4, frost), .98, 1 - saturate(dot(normal, view)));
                float wave = sin(dot(input.local, float3(27,19,23)) + _Time.y * 1.2
                    + sin(dot(input.local, float3(13,-17,11))) * 2);
                float filament = smoothstep(.86, 1, wave);
                // 교차하는 가는 절단면으로 가장자리에만 얼음 균열을 표시한다.
                float cuts = min(abs(sin(dot(input.local, float3(53,37,29)))),
                    abs(sin(dot(input.local, float3(-31,61,43)))));
                float fracture = 1 - smoothstep(.025, .025 + max(fwidth(cuts), .015), cuts);
                float detail = lerp(filament, fracture, frost);
                float alpha = rim * opacity * color.a * (.64 + .36 * detail);
                float3 tint = lerp(color.rgb, float3(.88,.98,1), .22 + .32 * detail + frost * .35);
                return half4(tint, saturate(alpha));
            }
            ENDHLSL
        }
    }
}
