Shader "ElementalLegion/ElementBody"
{
    Properties
    {
        _BaseColor("주 물질 색",Color)=(0,0.5,1,1)
        [HDR] _SecondColor("반사·발광 색",Color)=(1,1,1,1)
        _Element("광학 유형",Float)=0
        _NoiseTex("기존 흐름 자료",2D)="white"{}
        _SurfaceTex("실제 표면 색",2D)="white"{}
        [Normal] _NormalMap("실제 표면 노멀",2D)="bump"{}
        _RoughnessTex("실제 표면 거칠기",2D)="white"{}
        _SurfaceScale("표면 반복 배율",Range(.2,12))=2
        _SurfaceExposure("암석·이끼 지도 밝기 배율",Range(.5,3))=1
        _SurfaceSaturation("표면 지도의 채도",Range(0,1))=1
        _NormalStrength("표면 노멀 강도",Range(0,2))=.65
        _Roughness("거칠기 배율",Range(.02,1))=.65
        _Metallic("금속성",Range(0,1))=0
        _Absorption("투과 중 색 흡수",Range(0,6))=1.2
        _Transmission("투과율",Range(0,1))=0
        _EmissionStrength("국소 발광 강도",Range(0,4))=.8
        _CoreOpacity("메시 본체 밀도",Range(0,1))=1
        _AmbientFill("표면을 읽을 약한 환경 보조광",Range(0,.8))=.25
        _SurfaceReflection("투명 물질 가장자리 반사",Range(0,1))=.25
        _ReflectionCube("원소 전용 HDR 반사",Cube)=""{}
        _ReflectionStrength("원소 전용 HDR 반사 강도",Range(0,.8))=0
        [HideInInspector] _ReflectionMipCount("반사 mip 수",Float)=8
        [HideInInspector] _OwnershipTint("소유자 색상 우선",Float)=0
        _Flow("흐름 속도",Float)=1
        _DeformationStrength("물질 표면 변형 배율",Range(0,1.5))=1
        _FoamStrength("물마루 포말 양",Range(0,1))=.45
        _OrbitSpeed("중력 암편 공전 속도 배율",Range(0,2))=.75
        _Opacity("전체 불투명도",Range(0,1))=.85
        [Enum(Off,0,On,1)] _DepthWrite("고체 표면 깊이 기록",Float)=0
        _Distortion("굴절 왜곡",Range(0,.08))=.018
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite [_DepthWrite]
            Cull Off
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"

            TEXTURE2D(_NoiseTex); SAMPLER(sampler_NoiseTex);
            TEXTURE2D(_SurfaceTex); SAMPLER(sampler_SurfaceTex);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURE2D(_RoughnessTex); SAMPLER(sampler_RoughnessTex);
            TEXTURECUBE(_ReflectionCube);SAMPLER(sampler_ReflectionCube);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor, _SecondColor, _NoiseTex_ST, _SurfaceTex_ST;
                float _Element, _Flow, _Opacity, _Distortion;
                float _SurfaceScale, _NormalStrength, _Roughness, _Metallic;
                float _SurfaceExposure,_SurfaceSaturation;
                float _Absorption, _Transmission, _EmissionStrength, _CoreOpacity;
                float _AmbientFill,_SurfaceReflection;
                float _ReflectionStrength,_ReflectionMipCount;
                float _OwnershipTint;
                float _OrbitSpeed;
                float _DeformationStrength,_FoamStrength;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 world : TEXCOORD0;
                half3 normal : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float3 local : TEXCOORD3;
                float4 detail : TEXCOORD4;
                half4 tangent : TEXCOORD5;
                half fog : TEXCOORD6;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 p=input.positionOS.xyz;
                float phase=dot(TransformObjectToWorld(float3(0,0,0)),float3(.73,.21,.57))+input.color.g*6.283;
                float t=_Time.y*_Flow+phase;
                float3 displacement=0;
                if(_Element==0)
                    displacement=input.normalOS*(sin(p.y*11+t*2.8+p.x*7)*.037+sin(p.z*13+t*1.9)*.023);
                else if(_Element==1)
                    displacement=float3(sin(p.y*7-t*4)*.07,sin(p.y*9-t*5)*.035,cos(p.y*6-t*3)*.04)*saturate(p.y+.75);
                else if(_Element==2 || _Element==3)
                    displacement=input.normalOS*sin(p.x*8+p.y*7-t*1.7)*.035;
                else if(_Element==6)
                    displacement=float3(sin(p.y*31+t*23),sin(p.z*27+t*19),cos(p.x*33+t*21))*.016;
                else if(_Element==7 || _Element==10 || _Element==11 || _Element==12)
                    displacement=input.normalOS*sin(p.y*12+t*2+phase)*.012*input.color.r;
                else if(_Element==13)
                    displacement=float3(sin(p.y*9+t*1.8),0,cos(p.y*7+t*1.3))*.018*input.color.r;
                // CPU 표시 반경과 메시 bounds가 예약한 0.18을 넘지 않는다.
                displacement*=_DeformationStrength;
                displacement*=min(1,.18/max(length(displacement),.0001));
                p+=displacement;
                float3 normalOS=input.normalOS;
                float4 tangentOS=input.tangentOS;
                if(_Element==11 && _OrbitSpeed>0)
                {
                    // 한 암편의 모든 정점은 같은 위상으로 공전하고 법선·접선도 함께 회전한다.
                    float angle=_Time.y*_Flow*_OrbitSpeed*lerp(.35,1.2,input.color.g)+input.color.g*6.283;
                    float sine,cosine;sincos(angle,sine,cosine);
                    float2x2 rotation=float2x2(cosine,-sine,sine,cosine);
                    p.xz=mul(rotation,p.xz);normalOS.xz=mul(rotation,normalOS.xz);tangentOS.xz=mul(rotation,tangentOS.xz);
                }
                output.local=p;
                output.world=TransformObjectToWorld(p);
                output.positionCS=TransformWorldToHClip(output.world);
                output.normal=TransformObjectToWorldNormal(normalOS);
                output.tangent=half4(TransformObjectToWorldDir(tangentOS.xyz,false),tangentOS.w*GetOddNegativeScale());
                output.uv=input.uv;output.detail=input.color;
                output.fog=ComputeFogFactor(output.positionCS.z);
                return output;
            }
            half4 Frag(Varyings input, FRONT_FACE_TYPE face : FRONT_FACE_SEMANTIC) : SV_Target
            {
                float t=_Time.y*_Flow;
                float accent=saturate(input.detail.x),phase=input.detail.y*6.283;
                float2 uv=input.uv*_SurfaceScale;
                bool fluid=_Element==0 || _Element==4 || _Element==8;
                bool mappedSolid=_Element==5 || _Element==8 || _Element==9 || _Element==11 || _Element==12 || _Element==13;
                float2 flow=_Element==0?float2(t*.035,-t*.047):float2(0,0);
                float3 normalTS=UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,uv+flow),_NormalStrength);
                if(_Element==0)
                {
                    float3 second=UnpackNormalScale(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,uv*1.73-flow*.7),_NormalStrength*.55);
                    normalTS=normalize(float3(normalTS.xy+second.xy,normalTS.z*second.z));
                }
                float3 normal=SafeNormalize(input.normal);
                float tangentLength=dot(input.tangent.xyz,input.tangent.xyz);
                if(tangentLength>1e-8 && abs(input.tangent.w)>.5)
                {
                    float3 tangent=input.tangent.xyz*rsqrt(tangentLength);
                    float3 bitangent=cross(normal,tangent)*input.tangent.w;
                    normal=SafeNormalize(tangent*normalTS.x+bitangent*normalTS.y+normal*normalTS.z);
                }
                normal*=IS_FRONT_VFACE(face,1,-1);
                float3 view=GetWorldSpaceNormalizeViewDir(input.world);
                float fresnel=pow(1-saturate(abs(dot(normal,view))),5);
                float3 sourceSurface=SAMPLE_TEXTURE2D(_SurfaceTex,sampler_SurfaceTex,uv).rgb;
                float surfaceLuminance=dot(sourceSurface,float3(.2126,.7152,.0722));
                float3 surface=mappedSolid?saturate(lerp(surfaceLuminance.xxx,sourceSurface,_SurfaceSaturation)*_SurfaceExposure):sourceSurface;
                float rough=SAMPLE_TEXTURE2D(_RoughnessTex,sampler_RoughnessTex,uv).r*_Roughness;
                float3 albedo=_BaseColor.rgb*surface;
                float3 emission=0;
                float alpha=_CoreOpacity;
                float frost=0;
                if(_Element==0)
                {
                    bool crest=accent>.45 && accent<.78;
                    float turbulence=.5+.27*sin(input.local.x*37+t*2.1)+.23*sin(input.local.z*43-t*3.3+phase);
                    float foam=crest?smoothstep(.57,.87,turbulence)*smoothstep(.18,.42,input.local.y):0;
                    foam*=_FoamStrength;
                    albedo=lerp(albedo,_SecondColor.rgb,foam);
                    rough=lerp(rough,.5,foam);
                    emission=_SecondColor.rgb*foam*.12;
                    if(crest)alpha*=.72+.28*pow(fresnel,.52);
                    alpha=lerp(alpha,1,foam);
                }
                if(_Element==4 || _Element==8)
                {
                    // 메시 공간을 따라 이어지는 깨진 판의 층이다. 인접 면이 같은 균열을 공유한다.
                    float3 p=input.local;
                    float layers=dot(p,float3(2.2,4.1,-1.65))+.26*sin(p.x*8-p.z*5);
                    float fissure=1-smoothstep(.018,.055,abs(frac(layers+.5)-.5));
                    float crystalline=_Element==4?1:step(.4,accent);
                    if(_Element==4)
                        frost=smoothstep(.38,.83,.5+.27*sin(p.x*19+p.y*11)+.23*sin(p.z*23-p.y*13))*saturate(accent+.52);
                    float3 crystalTint=lerp(_BaseColor.rgb,_SecondColor.rgb,_Element==4?frost*.44:.12+.16*saturate(sin(phase)));
                    float growth=pow(saturate(.5+.5*sin(p.y*29+p.x*6+phase)),18);
                    emission=_SecondColor.rgb*(_Element==4?fissure*.22:growth*.065+pow(fresnel,2)*.055)*crystalline;
                    albedo=lerp(surface*.36,crystalTint,crystalline);
                    rough=lerp(.86,lerp(max(.035,rough),.58,frost),crystalline);
                    alpha=lerp(1,alpha,crystalline);
                }
                else if(_Element==1)
                {
                    float feather=sin(saturate(input.uv.y)*3.14159);
                    float heat=saturate(.7-input.local.y*.65);
                    emission=lerp(_BaseColor.rgb,_SecondColor.rgb,heat)*_EmissionStrength;
                    alpha*=feather*(1-smoothstep(.45,.92,input.local.y));
                }
                else if(_Element==3)
                {
                    albedo=lerp(_BaseColor.rgb,_SecondColor.rgb,.25)*surface;
                    alpha*=.5*(1-fresnel);
                }
                else if(_Element==2)
                {
                    float edge=1-smoothstep(.08,.5,abs(input.uv.y-.5));
                    float current=.55+.45*sin(input.uv.x*19-t*5+phase);
                    albedo=lerp(float3(.36,.42,.43),_SecondColor.rgb,.5);
                    emission=_SecondColor.rgb*current*.12;
                    alpha*=lerp(1,edge*(.35+.4*current),step(.8,accent));
                    rough=.18;
                }
                else if(_Element==5)
                {
                    float grain=SAMPLE_TEXTURE2D(_SurfaceTex,sampler_SurfaceTex,uv*3.7).r;
                    albedo*=.68+.64*saturate(grain*_SurfaceExposure);
                    rough=max(.72,rough);
                }
                else if(_Element==9)
                {
                    float3 p=input.local;
                    float mineralBand=dot(p,float3(1.26,3.05,1.08))+.31*sin(p.x*9-p.z*5)+.14*sin(p.y*17+p.z*11);
                    float distanceToSeam=abs(frac(mineralBand+.5)-.5);
                    float seam=1-smoothstep(.012,.033,distanceToSeam);
                    float cavity=1-smoothstep(.045,.105,distanceToSeam);
                    albedo*=1-.5*cavity;
                    emission=_SecondColor.rgb*seam*(1-accent)*_EmissionStrength;
                }
                else if(_Element==13)
                {
                    float leaf=step(.8,accent),moss=step(.3,accent)*(1-leaf);
                    float sideDistance=abs(input.uv.x-.5);
                    float midrib=1-smoothstep(.012,.048,sideDistance);
                    float branching=1-smoothstep(.05,.16,abs(frac(input.uv.y*4.5+sideDistance*2.8+.5)-.5));
                    float vein=saturate(midrib+branching*.35)*(1-smoothstep(.75,1,input.uv.y));
                    albedo=lerp(float3(.38,.22,.095)*(.55+surface*.65),_BaseColor.rgb*(.35+surface*.65),moss);
                    float3 leafColor=_BaseColor.rgb*(.88+.45*input.detail.y+.2*input.uv.y)+_SecondColor.rgb*vein*.17;
                    albedo=lerp(albedo,saturate(leafColor),leaf);
                    rough=lerp(.9,.43+vein*.13,leaf);
                }
                if(_Element==6 || _Element==7 || _Element==10 || _Element==11 || _Element==12)
                {
                    float strand=_Element==6?1:step(.8,accent);
                    float pulse=_Element==6?.55+.45*sin(input.uv.x*35+floor(t*16+phase)*7):.75+.25*sin(input.uv.x*35-t*4+phase);
                    emission=lerp(emission,_SecondColor.rgb*_EmissionStrength*(.5+pulse*.5),strand);
                    albedo=lerp(albedo,float3(.01,.012,.014),strand);
                    if(_Element==10 || _Element==11)
                        alpha*=lerp(1,.25+pulse*.28,strand);
                    if(strand>.5 && input.detail.z>.4 && input.detail.z<.6)
                    {
                        float along=saturate(input.uv.x);
                        float edge=1-smoothstep(.1,.5,abs(input.uv.y-.5));
                        float ends=smoothstep(0,.09,along)*(1-smoothstep(.65,1,along));
                        float current=.45+.55*saturate(.5+.5*sin(along*17+t*4+phase));
                        float core=_Element==11?lerp(1.9,.38,along):1;
                        emission=_SecondColor.rgb*_EmissionStrength*current*core;
                        alpha=_CoreOpacity*edge*ends*(_Element==10?.48:.72);
                    }
                    if(_Element==10 && accent<.5)
                    {
                        albedo=0;
                        emission=_SecondColor.rgb*pow(fresnel,1.15)*_EmissionStrength*.34;
                    }
                    if(_Element==7 && strand<.5)
                    {
                        albedo=0;
                        float facing=pow(saturate(abs(dot(normal,view))),3);
                        emission=_SecondColor.rgb*_EmissionStrength*(.22+facing)*(1+.12*sin(t*3+phase));
                    }
                    if(_Element==6)
                    {
                        float facing=pow(saturate(abs(dot(normal,view))),4);
                        emission=lerp(_BaseColor.rgb,_SecondColor.rgb,facing)*_EmissionStrength*(.35+.65*pulse);
                        if(accent<.5)emission*=1.2;
                    }
                    if(_Element==12 && strand<.5)
                        emission+=_SecondColor.rgb*pow(fresnel,.85)*.12;
                    if(_Element==12 && strand>.5)
                        emission*=lerp(float3(1.55,.34,.64),float3(.3,.85,1.65),smoothstep(-.12,.12,input.local.x));
                    if(_Element==11 && accent>.4 && accent<.8)
                    {
                        albedo=float3(.008,.006,.014);
                        emission=_SecondColor.rgb*pow(fresnel,2)*_EmissionStrength*.22;
                        rough=.09;
                    }
                }
                albedo=lerp(albedo,_BaseColor.rgb*sourceSurface,saturate(_OwnershipTint));
                InputData lighting=(InputData)0;
                lighting.positionWS=input.world;
                lighting.normalWS=normal;
                lighting.viewDirectionWS=view;
                lighting.shadowCoord=TransformWorldToShadowCoord(input.world);
                half3 fill=lerp(half3(.55,.64,.74),half3(1,1,.88),saturate(normal.y*.5+.5));
                if(mappedSolid && _OwnershipTint<.5)
                    fill*=lerp(.67,1.35,saturate(dot(normal,float3(-.43,.83,.35))));
                lighting.bakedGI=SampleSH(normal)+_AmbientFill*fill;
                lighting.normalizedScreenSpaceUV=GetNormalizedScreenSpaceUV(input.positionCS);
                lighting.shadowMask=half4(1,1,1,1);
                SurfaceData material=(SurfaceData)0;
                material.albedo=albedo;material.metallic=_Metallic;
                material.specular=half3(.04,.04,.04);material.smoothness=1-clamp(rough,.035,.97);
                material.normalTS=half3(0,0,1);material.occlusion=1;
                material.emission=emission;material.alpha=1;
                half3 color=UniversalFragmentPBR(lighting,material).rgb;
                float3 localReflection=0;
                if(_ReflectionStrength>0)
                {
                    float level=saturate(rough)*(1.7-.7*saturate(rough))*_ReflectionMipCount;
                    float3 reflected=SAMPLE_TEXTURECUBE_LOD(_ReflectionCube,sampler_ReflectionCube,reflect(-view,normal),level).rgb;
                    float3 reflectance=lerp(float3(.04,.04,.04),albedo,_Metallic);
                    localReflection=reflected*lerp(reflectance,float3(1,1,1),fresnel)*_ReflectionStrength;
                }
                if(fluid && _Transmission>0)
                {
                    float transmission=_Element==8?step(.4,accent)*_Transmission:_Transmission;
                    float2 screen=GetNormalizedScreenSpaceUV(input.positionCS);
                    float2 viewNormal=TransformWorldToViewDir(normal).xy;
                    float3 refracted=SampleSceneColor(saturate(screen+viewNormal*_Distortion*(1-fresnel)));
                    float thickness=lerp(.16,.86,saturate(abs(dot(normal,view))));
                    if(_Element==0 && accent>.45 && accent<.78)thickness*=.46;
                    float3 absorption=exp(-(1-saturate(_BaseColor.rgb))*_Absorption*thickness);
                    color=lerp(color,refracted*absorption,transmission*(1-fresnel)*(1-frost*.53))+_SecondColor.rgb*pow(fresnel,.6)*_SurfaceReflection;
                    alpha=max(alpha,fresnel*.85);
                }
                return half4(MixFog(color+localReflection,input.fog),saturate(alpha*_Opacity*input.detail.w));
            }
            ENDHLSL
        }
    }
}
