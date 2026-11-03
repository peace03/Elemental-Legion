using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace ElementalLegion.Client.Editor
{
    /// <summary>Windows 게임 배포에 플레이어가 실행할 자체 포함 방 서버를 묶는다.</summary>
    public sealed class RoomServerBuild : IPostprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64) return;

            string clientRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string repository = Path.GetFullPath(Path.Combine(clientRoot, ".."));
            string executable = Path.GetFullPath(Path.Combine(clientRoot, report.summary.outputPath));
            string script = Path.Combine(clientRoot, "Tools", "Publish-LanServer.ps1");
            var start = new ProcessStartInfo("powershell.exe")
            {
                WorkingDirectory = repository,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-PlayerPath", executable })
                start.ArgumentList.Add(argument);

            try
            {
                using Process publish = Process.Start(start) ?? throw new BuildFailedException("방 서버를 게시할 프로세스를 열지 못했습니다.");
                var output = publish.StandardOutput.ReadToEndAsync();
                var errors = publish.StandardError.ReadToEndAsync();
                if (!publish.WaitForExit((int)TimeSpan.FromMinutes(5).TotalMilliseconds))
                {
                    try { publish.Kill(); }
                    catch (InvalidOperationException) { }
                    throw new BuildFailedException("방 서버 게시 시간이 초과됐습니다. .NET 10 SDK와 NuGet 연결을 확인하세요.");
                }
                if (publish.ExitCode != 0)
                    throw new BuildFailedException("방 서버 게시 실패\n" + output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
                Debug.Log(output.GetAwaiter().GetResult());
            }
            catch (Win32Exception error)
            {
                throw new BuildFailedException("방 서버 게시 도구를 시작하지 못했습니다. " + error.Message);
            }
        }
    }
}
