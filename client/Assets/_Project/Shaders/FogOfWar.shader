Shader "Elemental Legion/Space Fog"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off
        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D_X_FLOAT(_FogDepth);
        TEXTURE2D_X(_FogSceneColor);
        TEXTURE2D_X(_FogVolume);
        TEXTURE2D(_VisionMask);
        TEXTURE3D(_VolumeNoise); SAMPLER(sampler_VolumeNoise);
        float4 _FogMap, _FogDensity, _FogShape, _FogWind, _FogLighting;
        float _DecorativeFog;
        float4 _FogCyan, _FogViolet, _FogSunColor, _FogSunDirection, _FogAmbientColor;
        float4 _FogVolumeSize;
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
        Varyings Vert(uint vertexID : SV_VertexID)
        {
            Varyings result;
            result.positionCS = GetFullScreenTriangleVertexPosition(vertexID);
            result.uv = GetFullScreenTriangleTexCoord(vertexID);
            return result;
        }

        bool HasDepth(float depth)
        {
            #if UNITY_REVERSED_Z
            return depth > 0.000001;
            #else
            return depth < 0.999999;
            #endif
        }

        void WorldRay(float2 uv, out float3 origin, out float3 direction)
        {
            origin = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
            #if UNITY_REVERSED_Z
            float farDepth = 0;
            #else
            float farDepth = 1;
            #endif
            float3 farPoint = ComputeWorldSpacePosition(uv, farDepth, UNITY_MATRIX_I_VP);
            direction = normalize(farPoint - origin);
        }

        float3 Surface(float2 uv, float depth, float3 origin, float3 direction)
        {
            if (HasDepth(depth))
            {
                #if !UNITY_REVERSED_Z
                depth = lerp(UNITY_NEAR_CLIP_VALUE, 1, depth);
                #endif
                return ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
            }
            // 빈 배경은 바닥 평면으로 제한한다. 위를 향한 광선은 볼륨 경계까지만 적분한다.
            float distance = direction.y < -0.00001 ? max(0, -origin.y / direction.y) : _FogMap.x * 2;
            return origin + direction * distance;
        }

        float2 Vision(float3 samplePosition)
        {
            if (_DecorativeFog > 0.5) return float2(1, 1);
            float2 uv = samplePosition.xz / _FogMap.x;
            if (any(uv < 0) || any(uv > 1)) return 0;
            float2 gridPosition = samplePosition.xz / _FogMap.z;
            int2 cell = min((int2)floor(gridPosition), (int)_FogMap.y - 1);
            float2 exact = LOAD_TEXTURE2D(_VisionMask, cell).rg;
            // 탐색 차폐는 점 샘플로 고정한다. 노이즈·보간으로 미탐색 칸을 열지 않는다.
            float current = lerp(exact.r, SAMPLE_TEXTURE2D_LOD(_VisionMask, sampler_LinearClamp, gridPosition / _FogMap.y, 0).r, _FogShape.w);
            return float2(current * step(0.5, exact.g), step(0.5, exact.g));
        }

        float2 VolumeInterval(float3 origin, float3 direction)
        {
            float3 safeDirection = float3(direction.x >= 0 ? max(direction.x, 0.000001) : min(direction.x, -0.000001),
                direction.y >= 0 ? max(direction.y, 0.000001) : min(direction.y, -0.000001),
                direction.z >= 0 ? max(direction.z, 0.000001) : min(direction.z, -0.000001));
            float3 a = -origin / safeDirection;
            float3 b = (float3(_FogMap.x, _FogShape.x, _FogMap.x) - origin) / safeDirection;
            float3 nearT = min(a, b), farT = max(a, b);
            return float2(max(0, max(nearT.x, max(nearT.y, nearT.z))), min(farT.x, min(farT.y, farT.z)));
        }

        float4 Raymarch(Varyings input) : SV_Target
        {
            float depth = SAMPLE_TEXTURE2D_X_LOD(_FogDepth, sampler_PointClamp, input.uv, 0).r;
            float3 origin, direction; WorldRay(input.uv, origin, direction);
            float3 surface = Surface(input.uv, depth, origin, direction);
            float2 interval = VolumeInterval(origin, direction);
            interval.y = min(interval.y, max(0, dot(surface - origin, direction)));
            if (interval.y <= interval.x) return float4(0, 0, 0, 1);
            int samples = (int)_FogShape.z;
            float stepSize = (interval.y - interval.x) / samples;
            float transmittance = 1; float3 scatter = 0;
            float g = _FogLighting.z;
            float phase = (1 - g * g) / max(0.1, pow(max(0.001, 1 + g * g - 2 * g * dot(direction, _FogSunDirection.xyz)), 1.5));
            float3 illumination = _FogAmbientColor.rgb * _FogLighting.x + _FogSunColor.rgb * (_FogLighting.y * phase);
            [loop] for (int index = 0; index < samples; index++)
            {
                float3 samplePosition = origin + direction * (interval.x + (index + 0.5) * stepSize);
                float2 visibility = Vision(samplePosition);
                float3 noiseUV = (samplePosition - _FogWind.xyz * _Time.y) * _FogShape.y;
                float noise = SAMPLE_TEXTURE3D_LOD(_VolumeNoise, sampler_VolumeNoise, noiseUV, 0).r;
                float height = exp(-max(0, samplePosition.y) / _FogShape.x * _FogDensity.w);
                float density = _FogDensity.x * lerp(1, _FogDensity.y, visibility.x) * (0.25 + 1.5 * noise) * height;
                float absorption = exp(-density * stepSize);
                float3 tint = lerp(_FogCyan.rgb, _FogViolet.rgb, smoothstep(0.2, 0.8, noise));
                scatter += transmittance * (1 - absorption) * tint * illumination;
                transmittance *= absorption;
                if (transmittance < 0.01) break;
            }
            return float4(scatter, transmittance);
        }

        float EyeDistance(float2 uv)
        {
            float3 origin, direction; WorldRay(uv, origin, direction);
            float depth = SAMPLE_TEXTURE2D_X_LOD(_FogDepth, sampler_PointClamp, uv, 0).r;
            return length(Surface(uv, depth, origin, direction) - origin);
        }

        float4 Composite(Varyings input) : SV_Target
        {
            float depth = SAMPLE_TEXTURE2D_X_LOD(_FogDepth, sampler_PointClamp, input.uv, 0).r;
            float3 origin, direction; WorldRay(input.uv, origin, direction);
            float3 surface = Surface(input.uv, depth, origin, direction);
            float2 visibility = Vision(surface);
            if (visibility.y < 0.5) return float4(0, 0, 0, 1);
            float centerDistance = length(surface - origin);
            float2 pixel = input.uv * _FogVolumeSize.zw - 0.5;
            float2 baseUV = (floor(pixel) + 0.5) * _FogVolumeSize.xy;
            float2 fraction = frac(pixel);
            float4 volume = 0; float total = 0;
            [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++)
                {
                    float2 uv = saturate(baseUV + float2(x, y) * _FogVolumeSize.xy);
                    float bilinearWeight = (x == 0 ? 1 - fraction.x : fraction.x) * (y == 0 ? 1 - fraction.y : fraction.y);
                    float weight = max(0.00001, bilinearWeight * exp(-abs(EyeDistance(uv) - centerDistance) * _FogLighting.w));
                    volume += SAMPLE_TEXTURE2D_X_LOD(_FogVolume, sampler_PointClamp, uv, 0) * weight; total += weight;
                }
            volume /= total;
            float3 scene = SAMPLE_TEXTURE2D_X_LOD(_FogSceneColor, sampler_PointClamp, input.uv, 0).rgb;
            float brightness = lerp(_FogDensity.z, 1, visibility.x);
            return float4(scene * brightness * volume.a + volume.rgb, 1);
        }
        ENDHLSL
        Pass
        {
            Name "Raymarch"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Raymarch
            ENDHLSL
        }
        Pass
        {
            Name "DepthComposite"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }
    }
}
