using System;
using System.Collections.Generic;
using UnityEngine;

namespace ElementalLegion.Client.Legion
{
    public sealed partial class ElementalVisuals
    {
        private const int MinimumLiveBodyParticles = 8;
        private const int MaximumBodyParticles = 24;
        private const float GoldenAngle = 2.399963f;
        private const float SmokeWispAspect = .64f;
        private static readonly float SmokeWispHalfWidth = Mathf.Sqrt(1 + SmokeWispAspect * SmokeWispAspect) * .5f;
        private static readonly int BodySheet = Shader.PropertyToID("_BodySheet");
        private static readonly List<ParticleSystemVertexStream> bodyFlipbookStreams = new List<ParticleSystemVertexStream>
        {
            ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Tangent,
            ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.UV2,
            ParticleSystemVertexStream.AnimBlend
        };
        private bool particleBodiesEnabled;

        private static void ConfigureBodyParticleRenderer(ParticleSystemRenderer renderer, ElementArtProfile profile)
        {
            if (!profile.ParticleBody) return;
            bool flipbook = profile.ParticleColumns > 1 && profile.ParticleRows > 1;
            renderer.sortMode = ParticleSystemSortMode.Distance;
            renderer.SetActiveVertexStreams(bodyFlipbookStreams);
            var properties = new MaterialPropertyBlock();
            properties.SetVector(BodySheet, new Vector4(flipbook ? profile.ParticleColumns : 1,
                flipbook ? profile.ParticleRows : 1, 1, flipbook ? 1 : 0));
            renderer.SetPropertyBlock(properties);
        }

        private void DrawBodyParticles(float seconds)
        {
            int budget = Mathf.Clamp(maxParticles - auxiliaryParticles, 0, bodyParticles.Length);
            Span<int> perUnit = stackalloc int[profiles.Length];
            AllocateBodyParticles(budget, perUnit);
            Vector3 viewRight = targetCamera.transform.right;
            Vector3 viewUp = targetCamera.transform.up;
            bodyParticleCount = 0;
            for (int element = 0; element < profiles.Length; element++)
            {
                int amount = perUnit[element];
                var layer = bodyLayers[element];
                if (amount == 0 || counts[element] == 0)
                {
                    if (layer.particleCount != 0) layer.Clear(false);
                    continue;
                }

                var profile = profiles[element];
                int written = 0;
                for (int unit = 0; unit < counts[element]; unit++)
                {
                    int start = written;
                    WriteBodyParticles(profile, matrices[element][unit], bodyUnitIds[element][unit], amount,
                        seconds, bodyFootprints[element][unit], viewRight, viewUp, ref written);
                    byte squad = bodySquadIds[element][unit];
                    if (squad < 1 || squad > 4) continue;
                    for (int index = start; index < written; index++)
                    {
                        ref ParticleSystem.Particle particle = ref bodyParticles[index];
                        float radians = particle.rotation * Mathf.Deg2Rad;
                        Vector3 size = particle.startSize3D;
                        float sine = Mathf.Abs(Mathf.Sin(radians)), cosine = Mathf.Abs(Mathf.Cos(radians));
                        Vector3 extent = viewRight * ((cosine * size.x + sine * size.y) * .5f) +
                            viewUp * ((sine * size.x + cosine * size.y) * .5f);
                        var bounds = new Bounds(targetCamera.WorldToScreenPoint(particle.position - extent), Vector3.zero);
                        bounds.Encapsulate(targetCamera.WorldToScreenPoint(particle.position + extent));
                        IncludeSquadBounds(squad, bounds);
                    }
                }

                Debug.Assert(written == amount * counts[element] && bodyParticleCount + written <= budget);
                if (!layer.isPlaying) layer.Play(false);
                layer.SetParticles(bodyParticles, written);
                bodyParticleCount += written;
            }
        }

        private void AllocateBodyParticles(int budget, Span<int> perUnit)
        {
            Span<int> requested = stackalloc int[profiles.Length];
            int liveMinimum = 0;
            int fullCost = 0;
            for (int element = 0; element < profiles.Length; element++)
            {
                var profile = profiles[element];
                requested[element] = Mathf.Clamp(profile.BodyParticleCount,
                    profile.ParticleBody ? MinimumLiveBodyParticles : 2, MaximumBodyParticles);
                if (profile.ParticleBody) liveMinimum += counts[element] * MinimumLiveBodyParticles;
                fullCost += counts[element] * requested[element];
            }

            particleBodiesEnabled = liveMinimum <= budget;
            if (fullCost <= budget)
            {
                requested.CopyTo(perUnit);
                return;
            }

            // 각 원소의 순서에 관계없이 주 몸체를 먼저 확보하고, 남은 예산은 한 겹씩 나눈다.
            for (int element = 0; element < profiles.Length; element++)
                perUnit[element] = particleBodiesEnabled && profiles[element].ParticleBody ? MinimumLiveBodyParticles : 0;
            int remaining = particleBodiesEnabled ? budget - liveMinimum : budget;
            for (int tier = 1; tier <= MaximumBodyParticles; tier++)
            {
                int cost = 0;
                for (int element = 0; element < profiles.Length; element++)
                    if (perUnit[element] < tier && tier <= requested[element]) cost += counts[element];
                if (cost > remaining) break;
                for (int element = 0; element < profiles.Length; element++)
                    if (perUnit[element] < tier && tier <= requested[element]) perUnit[element] = tier;
                remaining -= cost;
            }
        }

        private void WriteBodyParticles(ElementArtProfile profile, Matrix4x4 pose, int identity, int amount,
            float seconds, float footprint, Vector3 viewRight, Vector3 viewUp, ref int written)
        {
            Vector3 center = pose.GetColumn(3);
            Vector3 forward = pose.GetColumn(2);
            float sideScale = ((Vector3)pose.GetColumn(0)).magnitude;
            float forwardScale = forward.magnitude;
            float movement = Mathf.Clamp01((forwardScale / Mathf.Max(.001f, sideScale) - 1) / Mathf.Max(.01f, profile.MovementStretch));
            Vector3 direction = forward.normalized;
            Vector3 trailing = direction * (-movement * profile.ParticleTrailLength);
            int tailCount = Mathf.Max(1, amount / 3);
            int tailStart = amount - tailCount;
            bool hasTrail = movement > .01f && profile.ParticleTrailLength > .001f;
            bool live = profile.ParticleBody;
            int style = profile.Style;
            float baseAspect = live && style == 2 ? 2.25f : Mathf.Clamp(profile.ParticleAspect, .1f, 4);
            float baseHalfWidth = Mathf.Sqrt(1 + baseAspect * baseAspect) * .5f;
            float particleScale = Mathf.Min(1.6f, Mathf.Sqrt(sideScale * forwardScale)) * profile.Size * profile.BodyParticleSize;
            float clock = seconds * profile.MotionFrequency * .35f;
            float tilt = 24 * movement * Vector3.Dot(direction, viewRight);
            uint seed = unchecked((uint)identity * 2654435761u);
            float offsetPhase = (seed >> 16) / 65536f;
            for (int index = 0; index < amount; index++)
            {
                float phase = Mathf.Repeat(clock + index * .618034f + offsetPhase, 1);
                float angle = index * GoldenAngle + offsetPhase * 6.283185f + seconds * 1.6f;
                bool smokeWisp = live && style == 3 && index % 7 == 6;
                float aspect = smokeWisp ? SmokeWispAspect : baseAspect;
                float halfBillboard = smokeWisp ? SmokeWispHalfWidth : baseHalfWidth;
                float size;
                Vector3 local;
                float rotation;
                Color tint = profile.Tint;
                float opacity = live
                    ? Mathf.SmoothStep(0, 1, phase / .12f) * (1 - Mathf.SmoothStep(0, 1, (phase - .72f) / .28f))
                    : Mathf.Sin(phase * Mathf.PI);
                if (live)
                {
                    LiveBodyParticle(style, index, phase, angle, smokeWisp, out local, out size, out rotation, out tint);
                    if (style == 1) rotation += tilt;
                    if (style == 2)
                    {
                        Vector3 tangent = pose.MultiplyVector(new Vector3(-local.z, 0, local.x));
                        rotation = Mathf.Atan2(Vector3.Dot(tangent, viewUp), Vector3.Dot(tangent, viewRight)) * Mathf.Rad2Deg;
                    }
                    opacity *= tint.a;
                }
                else
                {
                    BodyParticle(style, index, seconds, phase, angle, out local, out size);
                    rotation = style == 1 ? Mathf.Sin(index * GoldenAngle) * 5 : angle * Mathf.Rad2Deg;
                    if (style == 12 && index % 2 == 0) tint = new Color(.85f, .58f, .22f);
                    else if (style != 1 && style != 3 && index % 3 == 0) tint = Color.Lerp(tint, Color.white, .35f);
                }

                Vector3 offset = pose.MultiplyVector(local);
                size *= particleScale;
                if (hasTrail && index >= tailStart)
                {
                    float trailPhase = (index - tailStart + phase) / tailCount;
                    offset += trailing * trailPhase;
                    opacity *= 1 - trailPhase;
                    size *= 1 - .45f * trailPhase;
                }
                float travel = Mathf.Sqrt(offset.x * offset.x + offset.z * offset.z);
                float fit;
                if (live && (style == 1 || style == 3))
                {
                    // 반경 안에서 중심 이동부터 줄여 불·연기의 겹치는 물질층을 유지한다.
                    size = Mathf.Min(size, footprint * .88f / halfBillboard);
                    fit = Mathf.Min(1, (footprint - size * halfBillboard) / Mathf.Max(.001f, travel));
                }
                else
                {
                    fit = Mathf.Min(1, footprint / Mathf.Max(.001f, travel + size * halfBillboard));
                    size *= fit;
                }
                offset.x *= fit;
                offset.z *= fit;
                tint.a = profile.BodyParticleOpacity * opacity;
                ref ParticleSystem.Particle particle = ref bodyParticles[written++];
                particle.position = center + offset;
                particle.startColor = tint;
                particle.startSize3D = new Vector3(size * aspect, size, size);
                particle.startLifetime = 2;
                particle.remainingLifetime = Mathf.Max(.01f, 2 * (1 - phase));
                particle.randomSeed = unchecked(seed + (uint)index + 1);
                particle.rotation = rotation;
            }
        }

        private static void LiveBodyParticle(int style, int index, float age, float angle, bool smokeWisp, out Vector3 offset,
            out float size, out float rotation, out Color tint)
        {
            float radius;
            float height;
            float variance = Mathf.Sin(index * GoldenAngle);
            rotation = 0;
            // 주 몸체 셰이더는 정점 색으로 잔불 여부, 열과 입자별 밝기를 받는다.
            if (style == 1)
            {
                bool ember = index % 5 == 4;
                radius = ember ? .17f + .21f * age : .08f + .14f * age;
                height = ember ? .06f + age * 1.02f : -.28f + age * .87f;
                size = ember ? .085f + variance * .025f : (1.08f - age * .64f) * (1 + .18f * variance);
                rotation = variance * (ember ? 26 : 10) + Mathf.Sin(angle * 1.2f) * 9;
                tint = new Color(ember ? 1 : 0, 1 - age * .58f, .69f + variance * .27f, ember ? .88f : 1);
            }
            else if (style == 2)
            {
                bool skirt = index % 4 == 0;
                angle += age * 10;
                radius = skirt ? .36f + age * .12f : .10f + age * age * .32f;
                height = skirt ? -.37f + age * .20f : -.29f + age * .97f;
                size = (skirt ? .29f : .19f + age * .24f) * (1 + .12f * variance);
                tint = new Color(1, 1, .80f + variance * .18f, skirt ? .85f : .96f);
            }
            else
            {
                bool billow = index % 3 == 0;
                angle += age * (2.9f + variance * 1.6f);
                radius = smokeWisp ? .12f + age * .31f : billow ? .18f + age * .21f : .12f + age * .24f;
                height = (smokeWisp ? .12f + age * .65f : -.27f + age * .57f) + variance * .10f;
                size = (smokeWisp ? .25f + age * .35f : billow ? .51f + age * .36f : .38f + age * .34f) * (1 + variance * .17f);
                rotation = angle * Mathf.Rad2Deg + variance * 21;
                tint = new Color(1, 1, smokeWisp ? .88f : .60f + variance * .33f, smokeWisp ? .70f : 1 - age * .16f);
            }
            offset = new Vector3(Mathf.Cos(angle) * radius, height, Mathf.Sin(angle) * radius);
        }
    }
}
