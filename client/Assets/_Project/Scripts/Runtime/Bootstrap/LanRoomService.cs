using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ElementalLegion.Shared.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace ElementalLegion.Client.Bootstrap
{
    /// <summary>LAN 방을 검색하고 게임에 포함된 로컬 방 서버를 실행한다.</summary>
    internal static class LanRoomService
    {
        internal sealed class Room
        {
            internal string Address;
            internal LanRoomListing Listing;
        }

        internal static async Task<IReadOnlyList<Room>> Discover(int port, CancellationToken stop)
        {
            var rooms = new List<Room>();
            var local = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                foreach (UnicastIPAddressInformation address in adapter.GetIPProperties().UnicastAddresses) local.Add(address.Address);
            var listener = new EventBasedNetListener();
            var manager = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = true };
            uint nonce = BitConverter.ToUInt32(RandomBytes(4), 0);
            listener.NetworkReceiveUnconnectedEvent += (source, reader, type) =>
            {
                if (type != UnconnectedMessageType.BasicMessage || rooms.Count >= 64 || !IsTrusted(source.Address) ||
                    !M5Protocol.TryReadRoomListing(reader, out uint received, out LanRoomListing listing) || nonce != received) return;
                string address = local.Contains(source.Address) ? IPAddress.Loopback.ToString() : source.Address.ToString();
                if (rooms.Exists(room => room.Address == address && room.Listing.Port == listing.Port)) return;
                rooms.Add(new Room { Address = address, Listing = listing });
            };
            if (!manager.Start()) throw new InvalidOperationException("LAN 방 검색용 소켓을 열지 못했습니다.");
            try
            {
                var query = new NetDataWriter();
                M5Protocol.WriteDiscovery(query, nonce);
                manager.SendUnconnectedMessage(query, new IPEndPoint(IPAddress.Loopback, port));
                manager.SendBroadcast(query, port);
                var elapsed = Stopwatch.StartNew();
                while (elapsed.ElapsedMilliseconds < 1000)
                {
                    await Task.Delay(50, stop);
                    manager.PollEvents(64);
                }
                return rooms;
            }
            finally { manager.Stop(); }
        }

        internal static async Task<(int Port, byte[] Proof)> Launch(string title, int minimum, int capacity, CancellationToken stop)
        {
            byte[] proof = RandomBytes(32);
            try
            {
                int port = await LaunchLan(new[] { "--listen", "--lan-room", "--title", title, "--minimum",
                    minimum.ToString(CultureInfo.InvariantCulture), "--capacity", capacity.ToString(CultureInfo.InvariantCulture), "--port", "0" },
                    BitConverter.ToString(proof).Replace("-", ""), stop);
                return (port, proof);
            }
            catch { Array.Clear(proof, 0, proof.Length); throw; }
        }

        internal static Task<int> Reopen(Guid room, CancellationToken stop) =>
            LaunchLan(new[] { "--listen", "--resume-lan-room", room.ToString("N") }, null, stop);

        /// <summary>등록을 확인한 뒤 방장이 입장할 때까지 호출자가 프로세스를 관리한다.</summary>
        internal static Task<HostedLaunch> LaunchHosted(string launchInput, bool useTestRelayBuild, CancellationToken stop) =>
            Launch(new ProcessStartInfo { Arguments = "--host-room" }, launchInput, "인터넷 방 서버", (process, line) =>
                line == "HOST_READY" ? new HostedLaunch(process, stop) : throw new InvalidOperationException("인터넷 방 서버가 릴레이에 연결하지 못했습니다."),
                stop, useTestRelayBuild);

        /// <summary>입장 전에 요청을 취소하거나 연결이 실패하면 이 요청이 시작한 서버를 종료한다.</summary>
        internal sealed class HostedLaunch : IDisposable
        {
            private Process process;
            private readonly CancellationTokenRegistration cancellation;

            internal HostedLaunch(Process process, CancellationToken stop)
            {
                this.process = process;
                cancellation = stop.Register(Stop);
            }

            internal void Detach()
            {
                cancellation.Dispose();
                Process detached = Interlocked.Exchange(ref process, null);
                if (detached != null) _ = DrainOutput(detached);
            }

            public void Dispose()
            {
                cancellation.Dispose();
                Stop();
            }

            private void Stop() => StopProcess(Interlocked.Exchange(ref process, null));
        }

        internal static Guid? LatestSavedRoom()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) return null;
            string worlds = Path.Combine(root, "ElementalLegion", "LanServer", "worlds");
            if (!Directory.Exists(worlds)) return null;
            Guid? latest = null;
            DateTime latestWrite = DateTime.MinValue;
            try
            {
                foreach (string file in Directory.EnumerateFiles(worlds, "world-*.json*"))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length < 43 || !Guid.TryParseExact(name.Substring(6, 32), "N", out Guid room)) continue;
                    string normalName = "world-" + room.ToString("N") + ".json";
                    if (name != normalName && name != normalName + ".bak" || File.Exists(Path.Combine(worlds, normalName + ".closed"))) continue;
                    DateTime write = File.GetLastWriteTimeUtc(file);
                    if (write <= latestWrite) continue;
                    latest = room;
                    latestWrite = write;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return latest;
        }

        private static Task<int> LaunchLan(string[] arguments, string input, CancellationToken stop)
        {
            var start = new ProcessStartInfo();
            foreach (string arg in arguments) start.ArgumentList.Add(arg);
            return Launch(start, input, "LAN 서버", (process, line) =>
            {
                if (line == null || !line.StartsWith("ROOM_PORT=", StringComparison.Ordinal) ||
                    !int.TryParse(line.Substring(10), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port <= 0 || port > 65535)
                    throw new InvalidOperationException("LAN 서버를 시작하지 못했습니다.");
                _ = DrainOutput(process);
                return port;
            }, stop);
        }

        private static async Task<T> Launch<T>(ProcessStartInfo start, string input, string serverName,
            Func<Process, string, T> accept, CancellationToken stop, bool useTestRelayBuild = false)
        {
            stop.ThrowIfCancellationRequested();
            string executable = Path.Combine(Application.dataPath, "Server", "ElementalLegion.Server.exe");
#if UNITY_EDITOR
            string repo = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
            if (useTestRelayBuild)
            {
                // 5번 실행기가 방장 서버와 중계의 버전과 게임 데이터를 맞춘다.
                executable = Path.Combine(repo, ".artifacts", "relay-test", "build", "bin", "ElementalLegion.Server", "release", "ElementalLegion.Server.exe");
            }
            else if (!File.Exists(executable))
            {
                string[] candidates =
                {
                    Environment.GetEnvironmentVariable("ELEMENTAL_LEGION_LAN_SERVER"),
                    Path.Combine(repo, "client", "Temp", "ServerRun", "dotnet-artifacts", "bin", "ElementalLegion.Server", "release", "ElementalLegion.Server.exe"),
                    Path.Combine(repo, "server", "Server", "bin", "Release", "net10.0", "ElementalLegion.Server.exe")
                };
                executable = Array.Find(candidates, candidate => !string.IsNullOrEmpty(candidate) && Path.IsPathRooted(candidate) && File.Exists(candidate));
            }
#endif
            if (!File.Exists(executable)) throw new InvalidOperationException($"이 빌드에 {serverName} 실행 파일이 없습니다.");
            start.FileName = executable;
            start.WorkingDirectory = Path.GetDirectoryName(executable);
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = input != null;
            start.RedirectStandardOutput = true;

            Process process = Process.Start(start);
            if (process == null) throw new InvalidOperationException($"{serverName}를 실행하지 못했습니다.");
            bool started = false;
            try
            {
                Task<string> readyLine = ReadStartup(process, input);
                _ = readyLine.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                Task completed = await Task.WhenAny(readyLine, Task.Delay(TimeSpan.FromSeconds(8), stop)).ConfigureAwait(false);
                stop.ThrowIfCancellationRequested();
                if (completed != readyLine) throw new InvalidOperationException($"{serverName}에서 준비 응답을 받지 못했습니다.");
                T result = accept(process, await readyLine.ConfigureAwait(false));
                started = true;
                return result;
            }
            finally
            {
                if (!started) StopProcess(process);
            }
        }

        private static void StopProcess(Process process)
        {
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception)
            { UnityEngine.Debug.LogWarning("이번 요청이 시작한 방 서버를 바로 종료하지 못했습니다."); }
            finally { process.Dispose(); }
        }

        private static async Task<string> ReadStartup(Process process, string input)
        {
            if (input != null)
            {
                await process.StandardInput.WriteLineAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            return await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        }

        private static async Task DrainOutput(Process process)
        {
            try
            {
                char[] buffer = new char[1024];
                while (await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) != 0) { }
            }
            catch (IOException) { }
            finally { process.Dispose(); }
        }

        private static byte[] RandomBytes(int size)
        {
            var bytes = new byte[size];
            RandomNumberGenerator.Fill(bytes);
            return bytes;
        }

        internal static bool IsTrusted(IPAddress address)
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
            byte[] bytes = address.GetAddressBytes();
            return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254);
        }
    }
}
