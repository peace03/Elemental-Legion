using ElementalLegion.Client.Bootstrap;
using UnityEditor;

namespace ElementalLegion.Client.Editor
{
    /// <summary>런타임 어셈블리의 Editor 의존성 없이 승계 완료 후 Play를 종료한다.</summary>
    [InitializeOnLoad]
    public static class ClientFlowQuitBridge
    {
        static ClientFlowQuitBridge()
        {
            ClientFlow.EditorQuitRequested += StopPlaying;
        }

        private static void StopPlaying() => EditorApplication.isPlaying = false;
    }
}
