using System;
using System.Reflection;
using ElementalLegion.Client.Editor;
using ElementalLegion.Client.Legion;
using ElementalLegion.Shared.Net;
using ElementalLegion.Shared.Sim;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Point = System.Numerics.Vector2;

namespace ElementalLegion.Client.Tests
{
    /// <summary>현재 표시 유닛만 공유 점광원을 사용하고 시야 해제 시 소등하는지 검증한다.</summary>
    public sealed class ElementLocalLightingTests
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject root;
        private ElementalVisuals visuals;

        [SetUp]
        public void SetUp()
        {
            root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(ClientSceneBuilder.PrefabPath));
            visuals = root.GetComponent<ElementalVisuals>();
            visuals.Initialize();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(root);

        [Test]
        public void PoolSelectsAtMostFourCurrentlyDisplayedNonGhostEmitters()
        {
            var profiles = Read<ElementArtProfile[]>("profiles");
            int fire = Array.FindIndex(profiles, profile => profile.ElementId == "fire");
            int water = Array.FindIndex(profiles, profile => profile.ElementId == "water");
            for (int index = 0; index < 8; index++) Display(index, index + 1, fire, false);
            Display(8, 99, fire, true);
            Display(9, 100, water, false);
            Invoke("UpdateLocalLights", 10);
            var lights = Read<Light[]>("localLights");
            var ids = Read<int[]>("localLightSourceIds");
            int active = 0;
            for (int slot = 0; slot < lights.Length; slot++)
            {
                if (lights[slot] == null || !lights[slot].enabled) continue;
                active++;
                Assert.That(ids[slot], Is.InRange(1, 8));
                Assert.That(lights[slot].type, Is.EqualTo(LightType.Point));
                Assert.That(lights[slot].shadows, Is.EqualTo(LightShadows.None));
                Assert.That(lights[slot].transform.position.y, Is.EqualTo(Read<float>("bodyHeight")));
                for (int other = 0; other < slot; other++) Assert.That(ids[other], Is.Not.EqualTo(ids[slot]));
            }
            Assert.That(active, Is.EqualTo(4));
            Invoke("UpdateLocalLights", 0);
            AssertLightsOff();
        }

        [Test]
        public void ForgetClearAndReinitializationReleaseLightSourcesWithoutDuplicatingPool()
        {
            int fire = Array.FindIndex(Read<ElementArtProfile[]>("profiles"), profile => profile.ElementId == "fire");
            Display(0, 77, fire, false);
            Invoke("UpdateLocalLights", 1);
            visuals.Forget(new ObservedEntityId(ObservedEntityKind.Core, 77));
            Assert.That(Array.Exists(Read<Light[]>("localLights"), light => light != null && light.enabled), Is.True);
            visuals.Forget(new ObservedEntityId(ObservedEntityKind.Unit, 77));
            AssertLightsOff();
            Invoke("UpdateLocalLights", 1);
            visuals.Clear();
            AssertLightsOff();
            Invoke("InitializeLocalLights");
            Invoke("InitializeLocalLights");
            Assert.That(Read<GameObject>("poolRoot").GetComponentsInChildren<Light>(true).Length, Is.EqualTo(4));
            root.SetActive(false);
            root.SetActive(true);
            visuals.Initialize();
            Assert.That(Read<GameObject>("poolRoot").GetComponentsInChildren<Light>(true).Length, Is.EqualTo(4));
            AssertLightsOff();
        }

        private void Display(int index, int id, int element, bool ghost)
        {
            var units = Read<Array>("displayUnits");
            Type type = units.GetType().GetElementType();
            object item = Activator.CreateInstance(type);
            var position = new Point(index, 0);
            type.GetField("Unit", Hidden).SetValue(item, new NetworkUnit(id, 1, element, position, false, 100, 0, false, 0, ghost));
            type.GetField("Position", Hidden).SetValue(item, position);
            units.SetValue(item, index);
        }

        private void AssertLightsOff()
        {
            foreach (var light in Read<Light[]>("localLights")) if (light != null) Assert.That(light.enabled, Is.False);
            foreach (int id in Read<int[]>("localLightSourceIds")) Assert.That(id, Is.Zero);
        }

        private T Read<T>(string field) => (T)typeof(ElementalVisuals).GetField(field, Hidden).GetValue(visuals);
        private void Invoke(string method, params object[] args) => typeof(ElementalVisuals).GetMethod(method, Hidden).Invoke(visuals, args);
    }
}
