// Unity Editor script — BabyBlocks/Build Shader Bundle menu item.
// Place this file under Assets/Editor/ in a Unity 2022.3 URP project.
// Place TintOverlay.shader under Assets/BabyBlocks/TintOverlay.shader.
// Then run:  BabyBlocks > Build Shader Bundle
// Copy the output file  Bundles/babyblocks_shaders  to:
//   %AppData%/../LocalLow/[company]/[game]/... or wherever MelonLoader UserData is,
//   inside the  UserData/BabyBlocks/  folder.
using System.IO;
using UnityEditor;

public static class BuildBundles
{
    const string BundleName = "babyblocks_shaders";
    const string ShaderPath = "Assets/BabyBlocks/TintOverlay.shader";
    const string OutputDir  = "Bundles";

    [MenuItem("BabyBlocks/Build Shader Bundle")]
    static void Build()
    {
        // Tag the shader asset
        var importer = AssetImporter.GetAtPath(ShaderPath);
        if (importer == null)
        {
            EditorUtility.DisplayDialog("Error",
                $"Shader not found at {ShaderPath}.\nMake sure TintOverlay.shader is at that path.",
                "OK");
            return;
        }
        importer.assetBundleName = BundleName;
        importer.SaveAndReimport();

        Directory.CreateDirectory(OutputDir);
        BuildPipeline.BuildAssetBundles(
            OutputDir,
            BuildAssetBundleOptions.None,
            BuildTarget.StandaloneWindows64
        );

        EditorUtility.DisplayDialog("Done",
            $"Bundle built to  {OutputDir}/{BundleName}\n\n" +
            "Copy that file (no extension) to:\n" +
            "  UserData/BabyBlocks/babyblocks_shaders",
            "OK");
    }
}
