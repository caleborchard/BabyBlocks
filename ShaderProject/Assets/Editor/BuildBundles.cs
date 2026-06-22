using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildBundles
{
    // Called via: Unity.exe -batchmode -executeMethod BuildBundles.Build
    public static void Build()
    {
        string[] shaderPaths = new[]
        {
            "Assets/Shaders/SelectionMask.shader",
            "Assets/Shaders/SelectionOutline.shader",
            "Assets/Shaders/DepthCapture.shader",
            "Assets/Shaders/GizmoSolid.shader",
            "Assets/Shaders/GizmoOccluded.shader",
            "Assets/Shaders/GizmoOnTop.shader",
        };

        // Verify every shader exists in the asset database before building.
        foreach (var path in shaderPaths)
        {
            var obj = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (obj == null)
            {
                Debug.LogError($"[BuildBundles] Could not load shader at {path}");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[BuildBundles] Verified {path}");
        }

        // Build explicitly via AssetBundleBuild[] so we don't depend on the asset
        // database label state, which is unreliable across batchmode runs.
        var build = new AssetBundleBuild
        {
            assetBundleName  = "babyblocks_shaders",
            assetNames       = shaderPaths,
        };

        string bundleOut = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BundleOutput"));
        Directory.CreateDirectory(bundleOut);
        Debug.Log($"[BuildBundles] Building bundle to {bundleOut}");

        var manifest = BuildPipeline.BuildAssetBundles(
            bundleOut,
            new[] { build },
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64);

        if (manifest == null)
        {
            Debug.LogError("[BuildBundles] BuildAssetBundles returned null — build failed.");
            EditorApplication.Exit(1);
            return;
        }

        string src = Path.Combine(bundleOut, "babyblocks_shaders");
        if (!File.Exists(src))
        {
            string[] files = Directory.GetFiles(bundleOut);
            Debug.LogError($"[BuildBundles] Bundle file not found at {src}. Files: {string.Join(", ", files)}");
            EditorApplication.Exit(1);
            return;
        }

        string dest = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "..", "BabyBlocks", "Shaders", "babyblocks_shaders.bundle"));
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        File.Copy(src, dest, overwrite: true);

        Debug.Log($"[BuildBundles] SUCCESS — bundle written to {dest}");
        EditorApplication.Exit(0);
    }
}
