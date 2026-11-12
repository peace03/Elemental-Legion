Shader "ElementalLegion/Battlefield"
{
    Properties
    {
        _BaseColor("현무암 바탕색", Color) = (.105,.125,.15,1)
        _BaseMap("기존 암석 색상", 2D) = "white" {}
        [Normal] _NormalMap("기존 암석 노멀", 2D) = "bump" {}
        _RoughnessMap("기존 암석 거칠기", 2D) = "white" {}
        _TextureScale("미터당 표면 반복", Range(.02,2)) = .025
        _NormalStrength("표면 요철", Range(0,2)) = 1.4
        _DrySmoothness("마른 암석 매끈함", Range(0,1)) = .3
        _WetSmoothness("젖은 암석 매끈함", Range(0,1)) = .68
        _WetCoverage("물기 영역 비율", Range(0,1)) = .12
        _VeinColor("광맥의 약한 반사색", Color) = (.12,.15,.17,1)
        _VeinStrength("광맥 농도", Range(0,1)) = .08
        _ReflectionCube("기존 HDR 반사 환경", Cube) = "" {}
        _ReflectionStrength("물기에 비친 환경 반사", Range(0,1)) = .7
        _AmbientFill("어두운 면을 읽는 보조광", Range(0,.5)) = .045
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
        TEXTURECUBE(_ReflectionCube); SAMPLER(sampler_ReflectionCube);
        CBUFFER_START(UnityPerMaterial)
            half4 _BaseColor, _VeinColor;
            float4 _BaseMap_ST;
            float _TextureScale, _NormalStrength, _DrySmoothness, _WetSmoothness;
            float _WetCoverage, _VeinStrength, _ReflectionStrength, _AmbientFill;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 tangentOS : TANGENT;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 world : TEXCOORD0;
            half3 normal : TEXCOORD1;
            half4 tangent : TEXCOORD2;
            half fog : TEXCOORD3;
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings Vert(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            Varyings output;
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.world = TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS = TransformWorldToHClip(output.world);
            output.normal = TransformObjectToWorldNormal(input.normalOS);
            output.tangent = half4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w * GetOddNegativeScale());
            output.fog = ComputeFogFactor(output.positionCS.z);
            return output;
        }
        float2 Hash(float2 p)
        {
            return frac(sin(float2(dot(p,float2(127.1,311.7)),dot(p,float2(269.5,183.3)))) * 43758.5453);
        }
        float Noise(float2 p)
        {
            float2 cell=floor(p), f=frac(p);
            f=f*f*(3-2*f);
            return lerp(lerp(Hash(cell).x,Hash(cell+float2(1,0)).x,f.x),
                        lerp(Hash(cell+float2(0,1)).x,Hash(cell+1).x,f.x),f.y);
        }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 p=input.world.xz;
                float broad=Noise(p*.075+float2(17,39));
                float2 warp=float2(Noise(p*.19),Noise(p*.19+53))-.5;
                float wetNoise=Noise(p*.37+warp*1.5+float2(51,27));
                float wet=smoothstep(1-_WetCoverage-.14,1-_WetCoverage+.14,wetNoise);
                // 젖은 영역은 반사와 거칠기만 바꾸며 평면 위에 별도 투명 메시를 겹치지 않는다.
                // 표면을 확대해도 무늬가 심하게 휘지 않도록 월드 좌표 기준으로 왜곡을 제한한다.
                float2 uv=p*_TextureScale+warp*min(.17,_TextureScale*.81);
                half3 detail=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv).rgb;
                half3 secondDetail=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv.yx*.713+float2(8,3)).rgb;
                detail=lerp(detail,secondDetail,.16);
                half grain=dot(detail,half3(.22,.7,.08));
                float fissure=Noise(p*1.83+warp*3);
                // 실제 암석 표면을 유지하고, 불연속적인 가는 균열만 낮은 대비로 보탠다.
                float crack=pow(saturate(1-abs(fissure-.48)*60),2)*smoothstep(.38,.65,broad)*smoothstep(.2,.48,grain);
                float vein=crack*smoothstep(.4,.7,wetNoise);
                half3 albedo=_BaseColor.rgb*(.48+grain*2.4)*lerp(.68,1.15,broad);
                albedo*=lerp(1,.72,crack)*lerp(1,.92,wet);
                albedo=lerp(albedo,_VeinColor.rgb,vein*_VeinStrength*(1-wet));
                half3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,uv),_NormalStrength*lerp(1,.13,wet));
                half3 n=normalize(input.normal);
                half3 tangent=normalize(input.tangent.xyz);
                half3 bitangent=cross(n,tangent)*input.tangent.w;
                n=normalize(tangent*normalTS.x+bitangent*normalTS.y+n*normalTS.z);
                half rough=SAMPLE_TEXTURE2D(_RoughnessMap,sampler_RoughnessMap,uv).r;
                half smoothness=lerp(_DrySmoothness*(1-rough*.45),_WetSmoothness,wet);
                InputData lighting=(InputData)0;
                lighting.positionWS=input.world;
                lighting.normalWS=n;
                lighting.viewDirectionWS=GetWorldSpaceNormalizeViewDir(input.world);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    lighting.shadowCoord=ComputeScreenPos(TransformWorldToHClip(input.world));
                #else
                    lighting.shadowCoord=TransformWorldToShadowCoord(input.world);
                #endif
                lighting.fogCoord=input.fog;
                lighting.vertexLighting=VertexLighting(input.world,n);
                lighting.bakedGI=SampleSH(n)+_AmbientFill;
                lighting.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask=half4(1,1,1,1);
                half occlusion=lerp(1,.8,crack);
                half4 color=UniversalFragmentPBR(lighting,albedo,.08,half3(.04,.04,.04),smoothness,occlusion,half3(0,0,0),1);
                half3 reflected=reflect(-lighting.viewDirectionWS,n);
                half3 environment=SAMPLE_TEXTURECUBE_LOD(_ReflectionCube,sampler_ReflectionCube,reflected,(1-smoothness)*7).rgb;
                half fresnel=.04+.96*pow(1-saturate(dot(n,lighting.viewDirectionWS)),5);
                color.rgb+=environment*_ReflectionStrength*wet*fresnel*occlusion;
                color.rgb=MixFog(color.rgb,input.fog);
                return half4(color.rgb,1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Depth
            #pragma multi_compile_instancing
            half4 Depth(Varyings input) : SV_Target { return input.positionCS.z; }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthNormals
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 DepthNormals(Varyings input) : SV_Target
            {
                float3 normal=normalize(input.normal);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct=PackNormalOctQuadEncode(normal);
                    return half4(PackFloat2To888(saturate(oct*.5+.5)),0);
                #else
                    return half4(normal,0);
                #endif
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection, _LightPosition;
            float4 ShadowVert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float3 world=TransformObjectToWorld(input.positionOS.xyz);
                float3 normal=TransformObjectToWorldNormal(input.normalOS);
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 direction=normalize(_LightPosition-world);
                #else
                    float3 direction=_LightDirection;
                #endif
                return ApplyShadowClamping(TransformWorldToHClip(ApplyShadowBias(world,normal,direction)));
            }
            half4 ShadowFrag() : SV_Target { return 0; }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
