using System;
using System.Collections;
using ElementalLegion.Shared.Net;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ElementalLegion.Client.Bootstrap
{
    public sealed partial class ClientFlow
    {
#if UNITY_EDITOR
        /// <summary>호스트 승계와 세션 정리가 끝난 뒤 Editor 전용 코드에 Play 종료를 요청한다.</summary>
        public static event Action EditorQuitRequested;
#endif

        private HostMigrationClient hostMigration;
        private ClientConnection hostedConnection;
        private bool leavingHostedRoom, quitting, quitApproved;

        private void OnEnable() => Application.wantsToQuit += WantsToQuit;
        private void OnDisable() => Application.wantsToQuit -= WantsToQuit;

        private void EnableHostMigration(ClientConnection transport)
        {
            DisposeHostMigration();
            hostedConnection = transport;
            hostMigration = new HostMigrationClient(transport, new Uri(onlineServiceUrl),
                LanRoomService.ServerExecutable(onlineServiceUrl == ReadRelayTestUrl()), network.MigrationCandidateSeconds);
        }

        private IEnumerator LeaveHostedSession()
        {
            if (leavingHostedRoom)
            {
                while (leavingHostedRoom) yield return null;
                yield break;
            }
            if (hostMigration == null) yield break;
            leavingHostedRoom = true;
            hostMigration.RequestLeave();
            double deadline = Time.unscaledTimeAsDouble + network.MigrationDeadlineSeconds;
            while (!hostMigration.LeaveCompleted && hostedConnection.State != ConnectionState.Disconnected && Time.unscaledTimeAsDouble < deadline)
            {
                // 전장에서는 Bootstrap이 수신을 처리하고, 대기실 종료에서는 여기서 계속 처리한다.
                if (game == null) hostedConnection.Poll();
                hostMigration.Poll();
                yield return null;
            }
            DisposeHostMigration();
            leavingHostedRoom = false;
        }

        private IEnumerator NavigateStandalone(string name)
        {
            yield return LeaveHostedSession();
            if (defeat != null && name == "ServerBrowser") startAtServerBrowser = true;
            game.EndSession();
            SceneManager.LoadSceneAsync("Bootstrap");
        }

        private IEnumerator CancelJoinAfterHandoff()
        {
            yield return LeaveHostedSession();
            cancelled = true;
            roomOperation?.Cancel();
        }

        private bool WantsToQuit()
        {
            if (quitApproved || hostedConnection?.IsHostServer != true) return true;
            Quit();
            return false;
        }

        private IEnumerator QuitAfterHandoff()
        {
            yield return LeaveHostedSession();
            game?.EndSession();
            quitApproved = true;
#if UNITY_EDITOR
            EditorQuitRequested?.Invoke();
#else
            Application.Quit();
#endif
        }

        private void DisposeHostMigration()
        {
            hostMigration?.Dispose(); hostMigration = null; hostedConnection = null;
        }
    }
}
