Shader "ElementalLegion/ElementBody"
{
 Properties { _BaseColor("Color",Color)=(0,0.5,1,1) _SecondColor("Hot/edge",Color)=(1,1,1,1) _Element("Element",Float)=0 _NoiseTex("Natural texture",2D)="white"{} _Flow("Flow",Float)=1 _Opacity("Opacity",Range(0,1))=0.85 _Distortion("Distortion",Range(0,0.2))=0.025 }
 SubShader
 {
  Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
  Pass
  {
   Tags { "LightMode"="UniversalForward" } Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
   HLSLPROGRAM
   #pragma vertex Vert
   #pragma fragment Frag
   #pragma multi_compile_instancing
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
   #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareOpaqueTexture.hlsl"
   TEXTURE2D(_NoiseTex);SAMPLER(sampler_NoiseTex);
   CBUFFER_START(UnityPerMaterial)
   float4 _BaseColor,_SecondColor;float4 _NoiseTex_ST;float _Element,_Flow,_Opacity,_Distortion;
   CBUFFER_END
   struct A {float4 positionOS:POSITION;float3 normalOS:NORMAL;float2 uv:TEXCOORD0;UNITY_VERTEX_INPUT_INSTANCE_ID};
   struct V {float4 positionCS:SV_POSITION;float3 world:TEXCOORD0;float3 normal:TEXCOORD1;float2 uv:TEXCOORD2;float3 local:TEXCOORD3;UNITY_VERTEX_OUTPUT_STEREO};
   float Hash(float3 p){p=frac(p*0.1031);p+=dot(p,p.yzx+33.33);return frac((p.x+p.y)*p.z);}
   float Noise(float3 p){float3 i=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(lerp(Hash(i),Hash(i+float3(1,0,0)),f.x),lerp(Hash(i+float3(0,1,0)),Hash(i+float3(1,1,0)),f.x),f.y),lerp(lerp(Hash(i+float3(0,0,1)),Hash(i+float3(1,0,1)),f.x),lerp(Hash(i+float3(0,1,1)),Hash(i+1),f.x),f.y),f.z);}
   V Vert(A v)
   {
    UNITY_SETUP_INSTANCE_ID(v);V o;UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);float3 p=v.positionOS.xyz;
    float t=_Time.y*_Flow;float n=Noise(p*5+float3(0,-t,0));
    if(_Element==0)p+=v.normalOS*(sin(p.y*12+t*3)*0.045+n*0.12);
    if(_Element==1||_Element==3){p.xz*=1-saturate(p.y+0.2)*0.45;p.y+=(n-0.3)*0.4;p.x+=sin(t*4+p.y*6)*0.13;}
    if(_Element==2||_Element==8||_Element==11)p.xz=mul(float2x2(cos(t+p.y*4),-sin(t+p.y*4),sin(t+p.y*4),cos(t+p.y*4)),p.xz);
    o.local=p;o.world=TransformObjectToWorld(p);o.positionCS=TransformWorldToHClip(o.world);o.normal=TransformObjectToWorldNormal(v.normalOS);o.uv=v.uv;return o;
   }
   half4 Frag(V i):SV_Target
   {
    float3 normal=normalize(i.normal),view=GetWorldSpaceNormalizeViewDir(i.world);float rim=pow(1-saturate(abs(dot(normal,view))),2);
    float t=_Time.y*_Flow;float n=Noise(i.local*9+float3(t*0.3,-t*2,t*0.2));n=n*0.7+Noise(i.local*22-t)*0.3;
    Light sun=GetMainLight();float lit=saturate(dot(normal,sun.direction))*0.65+0.35;
    float3 color=_BaseColor.rgb*lit;float alpha=_Opacity;
    if(_Element==0){float3 wave=UnpackNormal(SAMPLE_TEXTURE2D(_NoiseTex,sampler_NoiseTex,i.uv*2+float2(t*0.06,-t*0.04)));normal=normalize(normal+wave.xzy*0.3);float3 refraction=SampleSceneColor(GetNormalizedScreenSpaceUV(i.positionCS)+normal.xz*_Distortion);color=lerp(_BaseColor.rgb,refraction,0.35)+_SecondColor.rgb*(rim*0.8+pow(saturate(dot(reflect(-sun.direction,normal),view)),48));alpha=0.8;}
    else if(_Element==1){float fire=saturate(n*1.3-i.local.y*0.7);color=lerp(_BaseColor.rgb*0.8,_SecondColor.rgb*1.6,fire);alpha*=smoothstep(0.35,0.85,fire)*0.5;}
    else if(_Element==2){float vortex=pow(saturate(sin(atan2(i.local.z,i.local.x)*3+i.local.y*20-t*6)*0.5+0.5),8);color=lerp(_BaseColor.rgb,_SecondColor.rgb,rim);alpha*=vortex*0.65+rim*0.25;}
    else if(_Element==3){float smoke=n;color=_BaseColor.rgb*(0.5+smoke*0.5);alpha*=smoke*(0.3+rim*0.25);}
    else if(_Element==4||_Element==8){color+=_SecondColor.rgb*(pow(rim,2)+step(0.82,n)*0.5);alpha=0.8;}
    else if(_Element==5){color*=0.55+n;alpha=1;}
    else if(_Element==6){float arc=Noise(i.local*34+t*14);color=lerp(_BaseColor.rgb,_SecondColor.rgb*3,arc);alpha=0.9;}
    else if(_Element==7){color=_BaseColor.rgb*(rim*2+0.4)+_SecondColor.rgb*pow(n,6)*5;}
    else if(_Element==9){color*=pow(n,3)*3;color+=_SecondColor.rgb*rim;alpha*=step(0.35,n);}
    else if(_Element==10){color=_BaseColor.rgb*0.15+_SecondColor.rgb*pow(rim,4)*2;alpha=0.95;}
    else if(_Element==11){color=_BaseColor.rgb*0.15+_SecondColor.rgb*(rim+pow(sin(i.local.y*32+t*3)*0.5+0.5,12))*1.2;}
    else if(_Element==12){float field=pow(saturate(sin(i.local.y*36+t*3)),20);color+=_SecondColor.rgb*(field+rim)*1.5;alpha*=rim+field*0.5;}
    else {color+=_SecondColor.rgb*(rim+pow(n,6))*1.5;alpha*=0.65+rim*0.35;}
    return half4(color,alpha);
   }
   ENDHLSL
  }
 }
}
