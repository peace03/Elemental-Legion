Shader "ElementalLegion/Facility"
{
    Properties
    {
        _BaseMap("원본 표면 색",2D)="white"{}
        [Normal] _NormalMap("표면 노멀",2D)="bump"{}
        _RoughnessMap("표면 거칠기",2D)="white"{}
        _MetallicMap("금속성",2D)="white"{}
        _TeamMask("원본 팀 마스크",2D)="black"{}
        _ReflectionCube("기존 HDR 환경 반사",Cube)=""{}
        _TeamColor("소유 색상",Color)=(.15,.8,1,1)
        _NormalStrength("노멀 강도",Range(0,2))=.7
        _Roughness("기본 거칠기",Range(.05,1))=.5
        _Metallic("기본 금속성",Range(0,1))=.35
        _HasRoughnessMap("거칠기 지도 사용",Float)=0
        _HasMetallicMap("금속성 지도 사용",Float)=0
        _HasTeamMask("원본 팀 마스크 사용",Float)=0
        _PanelTintStrength("넓은 석재 패널의 소유 색 혼합",Range(0,1))=.5
        _EmissionStrength("인셋 발광",Range(0,3))=.8
        [HDR] _FacilityEmission("시설 동작 발광",Color)=(0,0,0,0)
        [HideInInspector] _FacilityEmissionCenter("발광 중심과 범위",Vector)=(0,0,0,0)
        [HideInInspector] _FacilityLightSource("시설 광원 위치와 거리 감쇠",Vector)=(0,0,0,0)
        [HideInInspector] _FacilityLightColor("시설 광원의 선형 RGB",Vector)=(0,0,0,0)
        [HideInInspector] _FacilityInlayGlow("작은 결정의 발광",Float)=0
        [HideInInspector] _FacilityCrystal("점화로 수정의 발광",Float)=0
        _AmbientFill("환경 보조광",Range(0,.5))=.14
        _ReflectionStrength("환경 반사 강도",Range(0,1))=.35
        _ReflectionMipCount("환경 반사 mip 수",Float)=8
        _Opacity("건설·유령 표시 밀도",Range(0,1))=1
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
        TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
        TEXTURE2D(_MetallicMap); SAMPLER(sampler_MetallicMap);
        TEXTURE2D(_TeamMask); SAMPLER(sampler_TeamMask);
        TEXTURECUBE(_ReflectionCube); SAMPLER(sampler_ReflectionCube);
        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST, _TeamColor;
            float4 _FacilityEmission, _FacilityEmissionCenter;
            float4 _FacilityLightSource, _FacilityLightColor;
            float _FacilityInlayGlow, _FacilityCrystal;
            float _NormalStrength, _Roughness, _Metallic, _HasRoughnessMap, _HasMetallicMap, _HasTeamMask;
            float _EmissionStrength, _AmbientFill, _ReflectionStrength, _ReflectionMipCount, _Opacity, _PanelTintStrength;
        CBUFFER_END
        struct Attributes
        {
            float4 positionOS:POSITION;
            float3 normalOS:NORMAL;
            float4 tangentOS:TANGENT;
            float2 uv:TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Varyings
        {
            float4 positionCS:SV_POSITION;
            float3 world:TEXCOORD0;
            half3 normal:TEXCOORD1;
            half4 tangent:TEXCOORD2;
            float2 uv:TEXCOORD3;
            half fog:TEXCOORD4;
            UNITY_VERTEX_OUTPUT_STEREO
        };
        Varyings Vert(Attributes input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            Varyings output;
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.world=TransformObjectToWorld(input.positionOS.xyz);
            output.positionCS=TransformWorldToHClip(output.world);
            output.normal=TransformObjectToWorldNormal(input.normalOS);
            output.tangent=half4(TransformObjectToWorldDir(input.tangentOS.xyz),input.tangentOS.w*GetOddNegativeScale());
            output.uv=TRANSFORM_TEX(input.uv,_BaseMap);
            output.fog=ComputeFogFactor(output.positionCS.z);
            return output;
        }
        void Coverage(float2 pixel)
        {
            // 유령과 미리보기에도 같은 메시의 깊이·그림자를 일관되게 사용한다.
            clip(_Opacity-InterleavedGradientNoise(pixel,0)-.00001);
        }
        half4 Depth(Varyings input):SV_Target { Coverage(input.positionCS.xy); return input.positionCS.z; }
        ENDHLSL
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            half4 Frag(Varyings input, FRONT_FACE_TYPE face:FRONT_FACE_SEMANTIC):SV_Target
            {
                Coverage(input.positionCS.xy);
                half3 base=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,input.uv).rgb;
                half3 mask=SAMPLE_TEXTURE2D(_TeamMask,sampler_TeamMask,input.uv).rgb;
                // 원본 마스크가 없는 시설은 청록 인셋만 고르고 금색·보라색은 보존한다.
                half cyan=smoothstep(.02,.14,base.g-base.r)*smoothstep(.02,.14,base.b-base.r)*smoothstep(.18,.45,base.g);
                half team=lerp(cyan,mask.r*_PanelTintStrength,saturate(_HasTeamMask));
                half lightMask=lerp(cyan,mask.g,saturate(_HasTeamMask));
                half3 teamSurface=_TeamColor.rgb*(.45+dot(base,half3(.2126,.7152,.0722))*.65);
                half3 albedo=lerp(base,teamSurface,team);
                half3 normal=normalize(input.normal);
                half3 tangent=normalize(input.tangent.xyz);
                half3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,input.uv),_NormalStrength);
                normal=normalize(tangent*normalTS.x+cross(normal,tangent)*input.tangent.w*normalTS.y+normal*normalTS.z);
                normal*=IS_FRONT_VFACE(face,1,-1);
                half rough=lerp(_Roughness,SAMPLE_TEXTURE2D(_RoughnessMap,sampler_RoughnessMap,input.uv).r,_HasRoughnessMap);
                half gold=saturate((base.r-base.b)*4)*saturate((base.g-base.b)*3);
                half metal=lerp(lerp(_Metallic,.8,gold),SAMPLE_TEXTURE2D(_MetallicMap,sampler_MetallicMap,input.uv).r,_HasMetallicMap);
                InputData lighting=(InputData)0;
                lighting.positionWS=input.world;
                lighting.normalWS=normal;
                lighting.viewDirectionWS=GetWorldSpaceNormalizeViewDir(input.world);
                lighting.shadowCoord=TransformWorldToShadowCoord(input.world);
                lighting.bakedGI=SampleSH(normal)+_AmbientFill*lerp(half3(.42,.52,.68),half3(.95,.92,.8),saturate(normal.y*.5+.5));
                lighting.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask=1;
                SurfaceData surface=(SurfaceData)0;
                surface.albedo=albedo;surface.metallic=metal;surface.smoothness=1-clamp(rough,.09,.93);
                surface.specular=.04;surface.normalTS=half3(0,0,1);surface.occlusion=1;surface.alpha=1;
                surface.emission=(_TeamColor.rgb*lightMask+half3(.08,.65,.85)*mask.b*_HasTeamMask)*_EmissionStrength;
                half luminance=dot(base,half3(.2126,.7152,.0722));
                half rim=1-saturate(dot(normal,lighting.viewDirectionWS));
                if (_FacilityInlayGlow > 0)
                {
                    half inlay=lerp(cyan,mask.b,saturate(_HasTeamMask));
                    surface.emission+=half3(.16,.82,1.15)*inlay*_FacilityInlayGlow*(.3h+.7h*rim*rim);
                }
                if (_FacilityCrystal > 0)
                {
                    // 원본 결정의 밝은 면은 내부 광원을 더 많이 통과시키고 비스듬한 면은 윤곽을 반사한다.
                    float height=input.world.y-_FacilityLightSource.y;
                    half core=exp2(-height*height*_FacilityLightSource.w*10);
                    half facets=smoothstep(.13h,.55h,luminance);
                    half edge=smoothstep(.48h,.98h,rim);
                    half fill=core*lerp(.32h,1,facets);
                    half peak=max(_FacilityEmission.r,max(_FacilityEmission.g,_FacilityEmission.b));
                    surface.emission+=_FacilityEmission.rgb*(.14h+.62h*fill+.38h*edge);
                    surface.emission+=half3(.54,.35,.58)*peak*(fill*facets*facets+.38h*edge);
                }
                else
                {
                    float3 fromEmitter=input.world-_FacilityEmissionCenter.xyz;
                    float glowRadius=_FacilityEmissionCenter.w;
                    float falloff=glowRadius>0 ? saturate(1-dot(fromEmitter,fromEmitter)/max(.001,glowRadius*glowRadius)) : 1;
                    surface.emission+=_FacilityEmission.rgb*falloff*falloff*lerp(.36,1,saturate(luminance*1.9));
                }
                half3 color=UniversalFragmentPBR(lighting,surface).rgb;
                if (_FacilityLightSource.w > 0 && any(_FacilityLightColor.rgb > 0))
                {
                    float3 toLight=_FacilityLightSource.xyz-input.world;
                    float distanceSquared=dot(toLight,toLight);
                    float distanceRatio=distanceSquared*_FacilityLightSource.w;
                    float window=saturate(1-distanceRatio);
                    float attenuation=window*window/(1+3*distanceRatio);
                    half3 direction=toLight*rsqrt(max(.0001,distanceSquared));
                    BRDFData brdf;
                    InitializeBRDFData(surface,brdf);
                    // shortcut: 부품 사이의 차폐는 생략한다. 자체 그림자가 필요한 시설은 차폐를 베이크한다.
                    half3 reflection=LightingPhysicallyBased(brdf,_FacilityLightColor.rgb,direction,attenuation,normal,lighting.viewDirectionWS);
                    color+=min(reflection,half3(4,4,4));
                }
                half fresnel=pow(rim,5);
                half3 reflected=SAMPLE_TEXTURECUBE_LOD(_ReflectionCube,sampler_ReflectionCube,reflect(-lighting.viewDirectionWS,normal),rough*_ReflectionMipCount).rgb;
                color+=reflected*lerp(lerp(half3(.04,.04,.04),albedo,metal),half3(1,1,1),fresnel)*_ReflectionStrength;
                return half4(MixFog(color,input.fog),1);
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex ShadowVert
            #pragma fragment Depth
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            float3 _LightDirection, _LightPosition;
            Varyings ShadowVert(Attributes input)
            {
                Varyings output=Vert(input);
                float3 lightDirection=_LightDirection;
                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    lightDirection=normalize(_LightPosition-output.world);
                #endif
                output.positionCS=TransformWorldToHClip(ApplyShadowBias(output.world,output.normal,lightDirection));
                #if UNITY_REVERSED_Z
                    output.positionCS.z=min(output.positionCS.z,UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z=max(output.positionCS.z,UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Normals
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT
            half4 Normals(Varyings input, FRONT_FACE_TYPE face:FRONT_FACE_SEMANTIC):SV_Target
            {
                Coverage(input.positionCS.xy);
                half3 normal=normalize(input.normal);
                half3 tangent=normalize(input.tangent.xyz);
                half3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,input.uv),_NormalStrength);
                normal=normalize(tangent*normalTS.x+cross(normal,tangent)*input.tangent.w*normalTS.y+normal*normalTS.z);
                normal*=IS_FRONT_VFACE(face,1,-1);
                #if defined(_GBUFFER_NORMALS_OCT)
                    float2 oct=PackNormalOctQuadEncode(normal)*.5+.5;
                    return half4(PackFloat2To888(saturate(oct)),0);
                #else
                    return half4(normal,0);
                #endif
            }
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask R
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Depth
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
}
