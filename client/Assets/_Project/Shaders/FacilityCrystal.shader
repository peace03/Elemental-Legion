Shader "ElementalLegion/FacilityCrystal"
{
    Properties
    {
        [HDR] _FaceColor("결정 면 색", Color) = (.035,.42,.65,1)
        [HDR] _EdgeColor("삼각 면 경계 색", Color) = (.18,1.7,2.5,1)
        [HDR] _CoreColor("결정 내부 광원 색", Color) = (.06,.78,1.12,1)
        _EdgeWidth("경계 두께(px)", Range(.25,2)) = 1.05
        _CoreStrength("내부 발광 강도", Range(0,3)) = 1.35
        _CorePulse("내부 발광의 맥동 폭", Range(0,.25)) = .08
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back
        ZWrite On

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _FaceColor;
            half4 _EdgeColor;
            half4 _CoreColor;
            float _EdgeWidth;
            float _CoreStrength;
            float _CorePulse;
        CBUFFER_END

        struct Attributes
        {
            float3 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 facet : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 facet : TEXCOORD0;
            float3 positionOS : TEXCOORD1;
            float3 viewOS : TEXCOORD2;
            half surfaceLight : TEXCOORD3;
            half fog : TEXCOORD4;
            half3 normalOS : TEXCOORD5;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings Vert(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            Varyings output;
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            float3 positionWS = TransformObjectToWorld(input.positionOS);
            output.positionCS = TransformWorldToHClip(positionWS);
            output.facet = input.facet.xyz;
            output.positionOS = input.positionOS;
            output.viewOS = TransformWorldToObjectDir(GetWorldSpaceViewDir(positionWS), false);
            output.normalOS = input.normalOS;
            half3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            half light = saturate(dot(normalWS, half3(-.39,.84,.38)));
            output.surfaceLight = (.36h + light * .92h) * input.facet.w;
            output.fog = ComputeFogFactor(output.positionCS.z);
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForwardOnly" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            half4 Frag(Varyings input) : SV_Target
            {
                // 경계의 양쪽 면이 한 선을 이루도록 각 면에서 픽셀 기준 반쪽 폭을 구한다.
                float3 dx = ddx(input.facet);
                float3 dy = ddy(input.facet);
                float3 gradient = sqrt(dx * dx + dy * dy);
                float3 width = max(min(gradient * (_EdgeWidth * .75), .15), .0001);
                float3 edgeDistance = input.facet / width;
                float nearestEdge = min(edgeDistance.x, min(edgeDistance.y, edgeDistance.z));
                half edge = 1.0h - smoothstep(.14, 1.0, nearestEdge);

                // 각 면의 법선에 따라 굴절시키고 내부 광원까지의 거리에 따라 빛을 감쇠시킨다.
                const float3 coreCenter = float3(.12, -.18, .09);
                const float coreRadius = .67;
                float3 rayDirection = refract(-normalize(input.viewOS), normalize(input.normalOS), .74);
                float3 fromCore = input.positionOS - coreCenter;
                float centerDistance = -dot(fromCore, rayDirection);
                float3 nearestToCore = fromCore + centerDistance * rayDirection;
                float halfChord = sqrt(max(0.0, coreRadius * coreRadius - dot(nearestToCore, nearestToCore)));
                float entryDistance = max(0.0, centerDistance - halfChord);
                float lightPath = max(0.0, centerDistance + halfChord - entryDistance);
                float transmission = exp(-.82 * entryDistance);
                float emission = 1.0 - exp(-1.8 * lightPath * lightPath);
                float pulse = 1.0 + sin(_Time.y * 1.7 + input.positionOS.y * 2.1) * _CorePulse;
                half facetLight = saturate(input.surfaceLight * .85h);
                half facetTransmission = .18h + 1.4h * facetLight * facetLight;
                half glow = transmission * emission * pulse * _CoreStrength * facetTransmission;
                half corePeak = max(_CoreColor.r, max(_CoreColor.g, _CoreColor.b));
                half3 coreColor = lerp(_CoreColor.rgb, corePeak.xxx, emission * .04h);
                half3 face = _FaceColor.rgb * (.38h + input.surfaceLight * input.surfaceLight * 1.15h);
                face += coreColor * glow * 1.35h;

                half edgePeak = max(_EdgeColor.r, max(_EdgeColor.g, _EdgeColor.b));
                half3 edgeColor = lerp(_EdgeColor.rgb, edgePeak.xxx, .10h + .30h * saturate(glow));
                half3 boundary = edgeColor * (.75h + .9h * facetLight + .35h * glow);
                return half4(MixFog(lerp(face, boundary, edge), input.fog), 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Depth
            #pragma multi_compile_instancing

            half Depth(Varyings input) : SV_Target
            {
                return input.positionCS.z;
            }
            ENDHLSL
        }
    }
}
