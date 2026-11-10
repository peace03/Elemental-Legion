Shader "ElementalLegion/ElementMatterParticles"
{
    Properties
    {
        _BaseMap("원본 플립북",2D)="white"{}
        [Normal] _NormalMap("연기 플립북 노멀",2D)="bump"{}
        [HDR] _BaseColor("저온·그늘 색",Color)=(1,.17,.015,1)
        [HDR] _SecondColor("고온·산란 색",Color)=(1,.86,.36,1)
        _Element("불 1 / 바람 2 / 연기 3 / 광점 7",Float)=1
        _EmissionStrength("발광 강도",Range(0,4))=1.4
        _Density("입자 밀도",Range(0,3))=1.4
        _SoftDepth("불투명 표면과 만나는 완충 거리(m)",Range(.01,1))=.24
        [HideInInspector] _BodySheet("Body sheet",Vector)=(1,1,0,0)
        [HideInInspector] _SrcBlend("Src",Float)=5
        [HideInInspector] _DstBlend("Dst",Float)=10
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForward" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            TEXTURE2D(_BaseMap);SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);SAMPLER(sampler_NormalMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor,_SecondColor,_BodySheet,_BaseMap_TexelSize;
                float _Element,_EmissionStrength,_Density,_SoftDepth;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS:POSITION;
                float3 normalOS:NORMAL;
                float4 tangentOS:TANGENT;
                float4 color:COLOR;
                float4 uv:TEXCOORD0;
                float blend:TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float3 world:TEXCOORD0;
                float4 uv:TEXCOORD1;
                half4 color:COLOR;
                half3 normal:TEXCOORD2;
                half3 tangent:TEXCOORD3;
                half3 bitangent:TEXCOORD4;
                half2 fogAndBlend:TEXCOORD5;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.world=TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS=TransformWorldToHClip(output.world);
                output.uv=input.uv;output.color=input.color;
                VertexNormalInputs frame=GetVertexNormalInputs(input.normalOS,input.tangentOS);
                output.normal=frame.normalWS;output.tangent=frame.tangentWS;output.bitangent=frame.bitangentWS;
                output.fogAndBlend=half2(ComputeFogFactor(output.positionCS.z),input.blend);
                return output;
            }

            float2 FlowingFrameUV(float2 uv,float2 cellUV,float2 flow)
            {
                float2 inset=_BaseMap_TexelSize.xy*_BodySheet.xy*.5;
                return (floor(uv*_BodySheet.xy)+clamp(cellUV+flow,inset,1-inset))/_BodySheet.xy;
            }

            half4 Frag(Varyings input):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                bool body=_BodySheet.z>.5;
                bool ember=body&&_Element==1&&input.color.r>.5;
                float2 uv=input.uv.xy;
                float2 cellUV=body?frac(uv*_BodySheet.xy):uv;
                float4 frame=0;
                if (!ember&&_Element!=7)
                {
                    if (body)
                    {
                        // 프레임 경계 안에서 소용돌이를 흐트러뜨려 옆 칸의 픽셀이 새지 않게 한다.
                        float time=_Time.y*(_Element==2?4.2:2.1)+input.color.b*17;
                        float2 flow=.026*float2(sin(cellUV.y*12+time),cos(cellUV.x*10-time));
                        uv=FlowingFrameUV(uv,cellUV,flow);
                        frame=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv);
                        if (_BodySheet.w>.5)
                        {
                            float2 nextUV=FlowingFrameUV(input.uv.zw,frac(input.uv.zw*_BodySheet.xy),flow);
                            frame=lerp(frame,SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,nextUV),input.fogAndBlend.y);
                        }
                    }
                    else frame=SAMPLE_TEXTURE2D(_BaseMap,sampler_BaseMap,uv);
                }
                float density=max(frame.r,max(frame.g,frame.b));
                float rawDepth=SampleSceneDepth(GetNormalizedScreenSpaceUV(input.positionCS));
                float sceneEye=unity_OrthoParams.w>.5?LinearDepthToEyeDepth(rawDepth):LinearEyeDepth(rawDepth,_ZBufferParams);
                float particleEye=-TransformWorldToView(input.world).z;
                float soft=saturate((sceneEye-particleEye)/max(.01,_SoftDepth));
                float alpha=frame.a*saturate(density*_Density);
                float3 color;
                if (_Element==1 || _Element==7)
                {
                    float heat=saturate(density*1.5);
                    if (ember || _Element==7)
                    {
                        float radius=length((cellUV-.5)*2);
                        alpha=exp(-radius*radius*5)*(1-smoothstep(.7,1,radius))*_Density;
                        heat=saturate(1-radius);
                    }
                    float3 hot=_SecondColor.rgb;
                    if (body)
                    {
                        heat=saturate(heat*lerp(.7,1.2,input.color.g));
                        hot=lerp(float3(.94,.34,.055),hot,heat*heat*.8);
                    }
                    color=lerp(_BaseColor.rgb,hot,heat*heat)*_EmissionStrength;
                    color*=body?input.color.b*(ember?1.25:.84):input.color.rgb;
                    color=MixFogColor(color,half3(0,0,0),input.fogAndBlend.x);
                }
                else
                {
                    float3 normalTS=UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap,sampler_NormalMap,uv));
                    float3 normal=normalize(TransformTangentToWorld(normalTS,float3x3(input.tangent,input.bitangent,input.normal)));
                    Light sun=GetMainLight(TransformWorldToShadowCoord(input.world));
                    float lit=saturate(dot(normal,sun.direction)*.5+.5);
                    float back=saturate(dot(GetWorldSpaceNormalizeViewDir(input.world),-sun.direction));
                    if (body)
                    {
                        bool dust=_Element==2;
                        // 광학 두께가 낮은 가장자리에만 빛을 더해 짙은 연기 중심부를 남긴다.
                        float thickness=density*_Density*(dust?1.5:4.8);
                        float relief=dot(normalTS.xy,float2(.65,-.76));
                        if (!dust) thickness=max(0,thickness+relief*sqrt(saturate(thickness))*.75-.085);
                        alpha=frame.a*(1-exp2(-thickness));
                        float selfShadow=exp2(-thickness*(1-lit)*.72);
                        float lightFactor=dust ? .19+lit*lit*.68 : .10+smoothstep(.32,.88,lit+relief*.26)*selfShadow*.8;
                        lightFactor*=lerp(.52,1,sun.shadowAttenuation);
                        lightFactor+=(dust?.17:.26)*back*back*(1-saturate(thickness));
                        color=lerp(_BaseColor.rgb,_SecondColor.rgb,saturate(lightFactor))*lerp(.39,1,input.color.b);
                    }
                    else
                    {
                        float illumination=.3+.7*lit;
                        color=lerp(_BaseColor.rgb,_SecondColor.rgb,illumination)*input.color.rgb+back*back*back*.16;
                    }
                    color=MixFog(color*(.5+sun.color*.5),input.fogAndBlend.x);
                }
                return half4(color,saturate(alpha*input.color.a*soft));
            }
            ENDHLSL
        }
    }
}
