using System;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using PackageClient = UnityEditor.PackageManager.Client;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ElementalLegion.Client.Editor
{
    public static class ProjectBootstrap
    {
        private static AddAndRemoveRequest request;
        private static double deadline;

        public static void InstallPackages()
        {
            request = PackageClient.AddAndRemove(new[]
            {
                "file:../../shared",
                "com.unity.nuget.newtonsoft-json@3.2.2",
                "com.unity.visualeffectgraph@17.6.0"
            }, new[]
            {
                "com.unity.ai.navigation",
                "com.unity.collab-proxy",
                "com.unity.timeline",
                "com.unity.visualscripting"
            });
            deadline = EditorApplication.timeSinceStartup + 600;
            EditorApplication.update += Poll;
        }

        private static void Poll()
        {
            if (!request.IsCompleted)
            {
                if (EditorApplication.timeSinceStartup < deadline) return;
                Debug.LogError("[Bootstrap] Package installation timed out.");
                EditorApplication.Exit(1);
                return;
            }

            EditorApplication.update -= Poll;
            if (request.Status != StatusCode.Success)
            {
                Debug.LogError("[Bootstrap] " + request.Error.message);
                EditorApplication.Exit(1);
                return;
            }

            Debug.Log("[Bootstrap] Packages installed. Run Validate in a fresh Editor process.");
            EditorApplication.Exit(0);
        }

        public static void Validate()
        {
            try
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>("Packages/com.elementallegion.shared/Data/gamedata.json");
                if (asset == null) throw new InvalidOperationException("Shared game data TextAsset is missing.");
                var assembly = System.Reflection.Assembly.Load("ElementalLegion.Shared");
                var type = assembly.GetType("ElementalLegion.Shared.Data.GameDataDocument", true);
                var document = Activator.CreateInstance(type, asset.text);
                var hash = (string)type.GetProperty("Sha256").GetValue(document);
                var networkAssembly = System.Reflection.Assembly.Load("LiteNetLib");
                if (networkAssembly.GetName().Version.ToString() != "1.3.5.0")
                    throw new InvalidOperationException("Unexpected LiteNetLib version.");
                PlayerSettings.companyName = "ElementalLegion";
                PlayerSettings.productName = "Elemental Legion";
                PlayerSettings.SetApiCompatibilityLevel(UnityEditor.Build.NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
                AssetDatabase.SaveAssets();
                File.WriteAllText("Library/BootstrapValidation.txt", $"Unity={Application.unityVersion}\nGameData={hash}\nLiteNetLib={networkAssembly.GetName().Version}\n");
                Debug.Log("[Bootstrap] VALIDATED: shared assembly, JSON TextAsset, LiteNetLib. SHA-256=" + hash);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }
    }
}
