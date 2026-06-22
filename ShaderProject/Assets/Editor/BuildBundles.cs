using System.IO;
using UnityEditor;
using UnityEngine;

public static class BuildBundles
{
    // Called via: Unity.exe -batchmode -executeMethod BuildBundles.Build
    public static void Build()
    {
        // Label both shaders into one bundle
        string[] shaderPaths = new[]
        {
            "Assets/Shaders/SelectionMask.shader",
            "Assets/Shaders/SelectionOutline.shader",
            "Assets/Shaders/DepthCapture.shader",
            "Assets/Shaders/GizmoOccluded.shader",
            "Assets/Shaders/GizmoOnTop.shader",
        };
        foreach (var path in shaderPaths)
        {
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null)
            {
                Debug.LogError($"[BuildBundles] Could not find asset at {path}");
                EditorApplication.Exit(1);
                return;
            }
            importer.assetBundleName = "babyblocks_shaders";
            Debug.Log($"[BuildBundles] Labeled {path} -> babyblocks_shaders");
        }

        string bundleOut = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "BundleOutput"));
        Directory.CreateDirectory(bundleOut);
        Debug.Log($"[BuildBundles] Building bundles to {bundleOut}");

        var manifest = BuildPipeline.BuildAssetBundles(
            bundleOut,
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
            Debug.LogError($"[BuildBundles] Bundle file not found at {src}. Files in output: {string.Join(", ", files)}");
            EditorApplication.Exit(1);
            return;
        }

        // Copy into the mod project
        // Application.dataPath = ShaderProject/Assets → go up twice to reach the repo root,
        // then into BabyBlocks/ (the mod subfolder) which is distinct from the ShaderProject.
        string dest = Path.GetFullPath(Path.Combine(
            Application.dataPath, "..", "..", "BabyBlocks", "Shaders", "babyblocks_shaders.bundle"));
        Directory.CreateDirectory(Path.GetDirectoryName(dest));
        File.Copy(src, dest, overwrite: true);

        Debug.Log($"[BuildBundles] SUCCESS — bundle written to {dest}");
        EditorApplication.Exit(0);
    }
}
