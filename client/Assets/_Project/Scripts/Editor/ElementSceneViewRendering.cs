using System.Collections.Generic;
using ElementalLegion.Client.Legion;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace ElementalLegion.Client.Editor
{
    /// <summary>전장 오브젝트를 볼 수 있는 Scene 카메라를 렌더러에 전달한다.</summary>
    internal static class ElementSceneViewRendering
    {
        [InitializeOnLoadMethod]
        private static void Register()
        {
            ElementalVisuals.CollectSceneViewCameras = CollectVisibleCameras;
        }

        private static void CollectVisibleCameras(ElementalVisuals visuals, List<UnityEngine.Camera> cameras)
        {
            if (SceneVisibilityManager.instance.IsHidden(visuals.gameObject)) return;

            // 프리팹 편집 등 다른 스테이지에 전장 표현이 섞이지 않도록 한다.
            var views = SceneView.sceneViews;
            for (int index = 0; index < views.Count; index++)
            {
                var camera = ((SceneView)views[index]).camera;
                if (camera == null || camera == visuals.TargetCamera) continue;
                if (StageUtility.IsGameObjectRenderedByCamera(visuals.gameObject, camera))
                    cameras.Add(camera);
            }
        }
    }
}
