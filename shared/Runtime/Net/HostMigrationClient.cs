using System;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ElementalLegion.Shared.Net
{
    /// <summary>Unity와 부하 봇이 같은 예비 서버 실행·승계 종료 경로를 사용하게 한다.</summary>
    public sealed class HostMigrationClient : IDisposable
    {
        private readonly ClientConnection connection;
        private readonly Uri origin;
        private readonly string executable;
        private readonly double startupTimeoutSeconds;
        private readonly string tracePrefix;
        private string offerKey;
        private CancellationTokenSource startup;
        private Task<HostedServerProcess> launching;
        private HostedServerProcess server;
        private ulong offeredGeneration;
        private bool disposed;
        public bool LeaveCompleted { get; private set; }
        public string Error { get; private set; } = string.Empty;
        public int ServerProcessId => server?.Id ?? -1;

        public HostMigrationClient(ClientConnection connection, Uri serviceOrigin, string executable, double startupTimeoutSeconds = 5, string tracePrefix = null)
        {
            this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
            origin = serviceOrigin ?? throw new ArgumentNullException(nameof(serviceOrigin));
            this.executable = executable;
            if (double.IsNaN(startupTimeoutSeconds) || double.IsInfinity(startupTimeoutSeconds) || startupTimeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(startupTimeoutSeconds));
            this.startupTimeoutSeconds = startupTimeoutSeconds;
            this.tracePrefix = tracePrefix;
            connection.HostStandbyRequested += Offer;
            connection.MigrationChanged += Changed;
            connection.HostingCapable = !string.IsNullOrEmpty(executable) && Path.IsPathRooted(executable) && File.Exists(executable);
        }

        public void Poll()
        {
            if (disposed || launching == null || !launching.IsCompleted) return;
            Task<HostedServerProcess> result = launching;
            launching = null;
            try { server = result.GetAwaiter().GetResult(); }
            catch (Exception error) when (error is InvalidOperationException || error is OperationCanceledException ||
                error is IOException || error is System.ComponentModel.Win32Exception || error is ArgumentException)
            { Error = "예비 서버를 준비하지 못했습니다."; connection.HostingCapable = false; }
            startup?.Dispose(); startup = null;
        }

        public void RequestLeave()
        {
            LeaveCompleted = !connection.IsOnline || !connection.IsHostServer;
            if (!LeaveCompleted) connection.RequestHostLeave();
        }

        private void Changed(HostMigrationNotice state)
        {
            if (state.Reason == "left" || state.Phase == MigrationPhase.Failed) LeaveCompleted = true;
        }

        private void Offer(HostStandbyOffer offer)
        {
            if (disposed || offer.Generation < offeredGeneration || offer.Generation == offeredGeneration && offer.Key == offerKey) return;
            CancelStartup();
            offeredGeneration = offer.Generation; offerKey = offer.Key;
            server?.Dispose(); server = null;
            startup = new CancellationTokenSource();
            string trace = string.IsNullOrEmpty(tracePrefix) ? null : tracePrefix + "." + offer.Generation.ToString(CultureInfo.InvariantCulture) + ".ticks.ndjson";
            launching = HostedServerProcess.StartAsync(executable, HostedServerProcess.StandbyInput(origin, offer), true, startup.Token, startupTimeoutSeconds, trace);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            connection.HostStandbyRequested -= Offer; connection.MigrationChanged -= Changed;
            CancelStartup();
            server?.Dispose(); server = null;
        }

        private void CancelStartup()
        {
            startup?.Cancel(); startup?.Dispose(); startup = null;
            if (launching != null) _ = launching.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) task.Result.Dispose();
                else _ = task.Exception;
            }, TaskScheduler.Default);
            launching = null;
        }
    }
}
