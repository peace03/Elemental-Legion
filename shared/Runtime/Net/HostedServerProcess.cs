using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ElementalLegion.Shared.Net
{
    /// <summary>게임에 포함된 서버만 실행하고 이번 요청이 만든 자식 프로세스를 정리한다.</summary>
    public sealed class HostedServerProcess : IDisposable
    {
        private Process process;
        private HostedServerProcess(Process process)
        { this.process = process; _ = Drain(process); }
        public int Id { get { try { return process?.Id ?? -1; } catch (InvalidOperationException) { return -1; } } }
        public bool HasExited { get { try { return process == null || process.HasExited; } catch (InvalidOperationException) { return true; } } }

        public static string StandbyInput(Uri serviceOrigin, HostStandbyOffer offer)
        {
            if (serviceOrigin == null || !serviceOrigin.IsAbsoluteUri || serviceOrigin.Scheme != "https" ||
                serviceOrigin.UserInfo.Length != 0 || serviceOrigin.Query.Length != 0 || serviceOrigin.Fragment.Length != 0)
                throw new ArgumentException("보안 중계 주소가 필요합니다.", nameof(serviceOrigin));
            HostMigrationProtocol.WriteStandby(new LiteNetLib.Utils.NetDataWriter(), offer.RoomId, offer.Key, offer.Generation);
            return new JObject
            {
                ["serviceUrl"] = serviceOrigin.GetLeftPart(UriPartial.Authority), ["roomId"] = offer.RoomId,
                ["hostKey"] = offer.Key, ["standby"] = true, ["generation"] = offer.Generation
            }.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static async Task<HostedServerProcess> StartAsync(string executable, string launchInput, bool standby, CancellationToken stop, double timeoutSeconds = 8, string tracePath = null)
        {
            if (string.IsNullOrEmpty(executable) || !Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new ArgumentException("설치된 방 서버 실행 파일이 필요합니다.", nameof(executable));
            if (string.IsNullOrEmpty(launchInput) || launchInput.Length > 1024)
                throw new ArgumentException("방 서버 실행 정보가 잘못됐습니다.", nameof(launchInput));
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds <= 0)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            stop.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo
            {
                FileName = executable, WorkingDirectory = Path.GetDirectoryName(executable),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true
            };
            if (string.Equals(Path.GetExtension(executable), ".dll", StringComparison.OrdinalIgnoreCase))
            { start.FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"; start.ArgumentList.Add(executable); }
            start.ArgumentList.Add("--host-room");
            if (!string.IsNullOrEmpty(tracePath)) start.Environment["ELEMENTAL_LEGION_LOAD_TRACE"] = Path.GetFullPath(tracePath);
            Process launched = Process.Start(start) ?? throw new InvalidOperationException("방 서버를 실행하지 못했습니다.");
            bool ready = false;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                Task<string> response = ReadStartup(launched, launchInput);
                _ = response.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                Task completed = await Task.WhenAny(response, Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
                if (completed != response || await response.ConfigureAwait(false) != (standby ? "STANDBY_READY" : "HOST_READY"))
                    throw new InvalidOperationException("방 서버에서 준비 응답을 받지 못했습니다.");
                ready = true;
                return new HostedServerProcess(launched);
            }
            finally { if (!ready) Stop(launched); }
        }

        private static async Task<string> ReadStartup(Process process, string input)
        {
            await process.StandardInput.WriteLineAsync(input).ConfigureAwait(false);
            process.StandardInput.Close();
            return await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        }

        private static async Task Drain(Process process)
        {
            try
            {
                var buffer = new char[1024];
                while (await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { }
            }
            catch (Exception error) when (error is IOException || error is ObjectDisposedException || error is InvalidOperationException) { }
            finally { process.Dispose(); }
        }

        private static void Stop(Process value)
        {
            if (value == null) return;
            try { if (!value.HasExited) value.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { Trace.TraceWarning("이번 요청이 시작한 방 서버를 종료하지 못했습니다."); }
            finally { value.Dispose(); }
        }

        public void Dispose() => Stop(Interlocked.Exchange(ref process, null));
    }
}
