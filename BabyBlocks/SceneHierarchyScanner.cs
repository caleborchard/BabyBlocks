using System;
using System.IO;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BabyBlocks
{
    // Scans every currently-loaded Unity scene and writes a full hierarchy dump
    // (GameObject names, world positions, all component types, mesh/LOD details)
    // to a text file under <persistentDataPath>/BabyBlocks/HierarchyScans/.
    //
    // Keys (only active while the freecam is running):
    //   F8            – scan all loaded scenes now
    //   Shift + F8    – toggle auto-scan (fires every AutoScanInterval seconds)
    //
    // The output files are plain UTF-8 text so they can be reused in other projects.
    public static class SceneHierarchyScanner
    {
        public static string OutputDir { get; private set; }

        static bool   _autoScanActive;
        static float  _autoScanTimer;
        const  float  AutoScanInterval = 15f;
        const  int    ObjectCap        = 100_000;

        // Scene-change tracking — detect new scene loads without event hooks
        static int    _lastKnownSceneCount;
        static string _lastKnownSceneNames = "";

        // ── Initialization ───────────────────────────────────────────────────

        public static void Init()
        {
            OutputDir = Path.Combine(
                Application.persistentDataPath, "BabyBlocks", "HierarchyScans");

            try   { Directory.CreateDirectory(OutputDir); }
            catch (Exception e)
            {
                MelonLogger.Error($"[SceneScanner] Could not create output dir: {e.Message}");
            }

            MelonLogger.Msg($"[SceneScanner] Initialized. Output → {OutputDir}");
            MelonLogger.Msg("[SceneScanner] F8 = scan now  |  Shift+F8 = toggle auto-scan");
        }

        // ── Per-frame update (called from Core.OnUpdate) ─────────────────────

        public static void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (Input.GetKey(KeyCode.LeftShift))
                    ToggleAutoScan();
                else
                    ScanAllLoadedScenes();
            }

            if (_autoScanActive)
            {
                _autoScanTimer -= Time.unscaledDeltaTime;
                if (_autoScanTimer <= 0f)
                {
                    ScanAllLoadedScenes(quiet: true);
                    _autoScanTimer = AutoScanInterval;
                }
            }

            // Detect scene loads/unloads without event hooks (IL2CPP-safe polling).
            CheckSceneChanges();
        }

        static void CheckSceneChanges()
        {
            int count = SceneManager.sceneCount;
            if (count == _lastKnownSceneCount)
            {
                // Quick name-hash check to detect swaps at the same count.
                var names = BuildSceneNameString(count);
                if (names == _lastKnownSceneNames) return;
                _lastKnownSceneNames = names;
                LogCurrentScenes(count, "CHANGED");
                return;
            }

            string namesNew = BuildSceneNameString(count);
            bool loaded = count > _lastKnownSceneCount;
            _lastKnownSceneCount = count;
            _lastKnownSceneNames = namesNew;
            LogCurrentScenes(count, loaded ? "LOADED" : "UNLOADED");
        }

        static string BuildSceneNameString(int sceneCount)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sceneCount; i++)
            {
                try
                {
                    var s = SceneManager.GetSceneAt(i);
                    sb.Append(s.name).Append('|');
                }
                catch { sb.Append("?|"); }
            }
            return sb.ToString();
        }

        static void LogCurrentScenes(int count, string changeType)
        {
            var sb = new StringBuilder();
            sb.Append($"[SceneScanner] Scene {changeType} — now {count} scene(s): ");
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var s = SceneManager.GetSceneAt(i);
                    if (i > 0) sb.Append(", ");
                    sb.Append($"\"{s.name}\"(bi={s.buildIndex})");
                }
                catch { sb.Append("?"); }
            }
            MelonLogger.Msg(sb.ToString());
        }

        static void ToggleAutoScan()
        {
            _autoScanActive = !_autoScanActive;
            _autoScanTimer  = AutoScanInterval;
            MelonLogger.Msg(_autoScanActive
                ? $"[SceneScanner] Auto-scan ON  (every {AutoScanInterval}s)"
                : "[SceneScanner] Auto-scan OFF");
        }

        // ── Main scan ────────────────────────────────────────────────────────

        public static void ScanAllLoadedScenes(bool quiet = false)
        {
            int sceneCount = SceneManager.sceneCount;
            if (sceneCount == 0)
            {
                if (!quiet) MelonLogger.Warning("[SceneScanner] No scenes currently loaded.");
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var    sb        = new StringBuilder(1 << 20); // 1 MB initial capacity
            var    summary   = new StringBuilder();

            string camPos = GetCamPos();
            sb.AppendLine("BabyBlocks Scene Hierarchy Scan");
            sb.AppendLine($"Timestamp  : {timestamp}");
            sb.AppendLine($"Game time  : {Time.time:F1}s");
            sb.AppendLine($"Camera pos : {camPos}");
            sb.AppendLine($"Scenes     : {sceneCount}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine();

            int totalObjects     = 0;
            int totalInteresting = 0;
            int scannedScenes    = 0;
            bool cappedOut       = false;

            for (int si = 0; si < sceneCount; si++)
            {
                if (cappedOut) break;
                var scene = SceneManager.GetSceneAt(si);
                if (!scene.isLoaded) continue;
                scannedScenes++;

                GameObject[] roots;
                try   { roots = scene.GetRootGameObjects(); }
                catch (Exception e)
                {
                    sb.AppendLine($"SCENE [{si}] \"{scene.name}\" — GetRootGameObjects failed: {e.Message}");
                    sb.AppendLine();
                    continue;
                }

                int rootCount = roots != null ? roots.Length : 0;
                sb.AppendLine($"SCENE [{si}]");
                sb.AppendLine($"  name       = \"{scene.name}\"");
                sb.AppendLine($"  path       = \"{scene.path}\"");
                sb.AppendLine($"  buildIndex = {scene.buildIndex}");
                sb.AppendLine($"  rootCount  = {rootCount}");
                sb.AppendLine(new string('-', 60));

                int sceneObjects     = 0;
                int sceneInteresting = 0;

                if (roots != null)
                {
                    for (int ri = 0; ri < roots.Length; ri++)
                    {
                        if (roots[ri] == null) continue;
                        if (totalObjects + sceneObjects >= ObjectCap)
                        {
                            sb.AppendLine($"  *** OBJECT CAP ({ObjectCap}) REACHED — remaining objects skipped ***");
                            cappedOut = true;
                            break;
                        }
                        AppendGameObject(roots[ri].transform, sb, 1,
                            ref sceneObjects, ref sceneInteresting,
                            ObjectCap - (totalObjects + sceneObjects));
                    }
                }

                sb.AppendLine($"  >> {sceneObjects} objects  {sceneInteresting} with mesh/LOD");
                sb.AppendLine();

                summary.AppendLine($"  [{si}] \"{scene.name}\": {sceneObjects} objects, {sceneInteresting} with mesh/LOD");
                totalObjects     += sceneObjects;
                totalInteresting += sceneInteresting;
            }

            sb.AppendLine(new string('=', 80));
            sb.AppendLine("SUMMARY");
            sb.Append(summary);
            sb.AppendLine($"Total: {scannedScenes} scene(s), {totalObjects} objects, {totalInteresting} with mesh/LOD");

            string filename = $"hierarchy_{timestamp}.txt";
            string filepath = Path.Combine(OutputDir, filename);

            try
            {
                File.WriteAllText(filepath, sb.ToString(), Encoding.UTF8);
                long fileSize = new FileInfo(filepath).Length;
                string sizeStr = fileSize > 1_000_000
                    ? $"{fileSize / 1_000_000.0:F1} MB"
                    : $"{fileSize / 1000} KB";

                MelonLogger.Msg($"[SceneScanner] {scannedScenes} scene(s)  {totalObjects} objects  {totalInteresting} mesh/LOD  {sizeStr}");
                if (!quiet)
                    MelonLogger.Msg($"[SceneScanner] → {filepath}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[SceneScanner] Write failed: {e.Message}");
            }
        }

        // ── Hierarchy traversal ──────────────────────────────────────────────

        static void AppendGameObject(Transform t, StringBuilder sb, int depth,
            ref int objCount, ref int interestingCount, int remaining)
        {
            if (remaining <= 0) return;
            objCount++;
            remaining--;

            var    go     = t.gameObject;
            string indent = new string(' ', depth * 2);

            // Gather all component type names in one pass
            var  comps     = go.GetComponents<Component>();
            var  compNames = new StringBuilder();
            bool hasLOD    = false;
            bool hasMesh   = false;

            if (comps != null)
            {
                for (int i = 0; i < comps.Length; i++)
                {
                    if (comps[i] == null) continue;
                    string typeName;
                    try   { typeName = comps[i].GetType().Name; }
                    catch { typeName = "?"; }

                    if (compNames.Length > 0) compNames.Append(", ");
                    compNames.Append(typeName);

                    if (typeName == "LODGroup")                               hasLOD  = true;
                    if (typeName == "MeshRenderer" ||
                        typeName == "SkinnedMeshRenderer")                    hasMesh = true;
                }
            }

            if (hasLOD || hasMesh) interestingCount++;

            // Object header
            var wpos = t.position;
            var lscl = t.localScale;
            sb.AppendLine(
                $"{indent}[{(go.activeInHierarchy ? "+" : "-")}] \"{go.name}\"" +
                $"  layer={LayerMask.LayerToName(go.layer)}({go.layer})" +
                $"  tag={go.tag}");
            sb.AppendLine(
                $"{indent}  pos=({wpos.x:F3},{wpos.y:F3},{wpos.z:F3})" +
                $"  scl=({lscl.x:F3},{lscl.y:F3},{lscl.z:F3})");
            sb.AppendLine(
                $"{indent}  components=[{compNames}]");

            // Detailed metadata for rendering-relevant components
            AppendComponentDetails(go, sb, indent + "  ");

            // Recurse into children
            for (int i = 0; i < t.childCount; i++)
            {
                if (remaining <= objCount) break; // respect cap
                AppendGameObject(t.GetChild(i), sb, depth + 1,
                    ref objCount, ref interestingCount, remaining - objCount + 1);
            }
        }

        static void AppendComponentDetails(GameObject go, StringBuilder sb, string indent)
        {
            // MeshFilter
            try
            {
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null)
                    sb.AppendLine($"{indent}MeshFilter: \"{mf.sharedMesh.name}\"  verts={mf.sharedMesh.vertexCount}");
            }
            catch { }

            // MeshRenderer
            try
            {
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    var mats = mr.sharedMaterials;
                    if (mats != null && mats.Length > 0)
                    {
                        var matNames = new string[mats.Length];
                        for (int i = 0; i < mats.Length; i++)
                            matNames[i] = mats[i] != null ? $"\"{mats[i].name}\"" : "null";
                        sb.AppendLine($"{indent}MeshRenderer: [{string.Join(", ", matNames)}]");
                    }
                }
            }
            catch { }

            // SkinnedMeshRenderer
            try
            {
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr != null && smr.sharedMesh != null)
                {
                    int boneCount = 0;
                    try { var b = smr.bones; if (b != null) boneCount = b.Length; } catch { }
                    sb.AppendLine($"{indent}SkinnedMeshRenderer: \"{smr.sharedMesh.name}\"  bones={boneCount}");
                }
            }
            catch { }

            // LODGroup — most important for asset recovery
            try
            {
                var lod = go.GetComponent<LODGroup>();
                if (lod != null)
                {
                    var lods = lod.GetLODs();
                    if (lods != null)
                    {
                        sb.AppendLine($"{indent}LODGroup: {lods.Length} LODs  enabled={lod.enabled}");
                        for (int i = 0; i < lods.Length; i++)
                        {
                            int rCount = lods[i].renderers != null ? lods[i].renderers.Length : 0;
                            sb.AppendLine(
                                $"{indent}  LOD[{i}]: screenRelHeight={lods[i].screenRelativeTransitionHeight:F4}" +
                                $"  renderers={rCount}");
                        }
                    }
                }
            }
            catch { }

            // Rigidbody
            try
            {
                var rb = go.GetComponent<Rigidbody>();
                if (rb != null)
                    sb.AppendLine($"{indent}Rigidbody: mass={rb.mass:F2}  kinematic={rb.isKinematic}  useGravity={rb.useGravity}");
            }
            catch { }

            // Light
            try
            {
                var light = go.GetComponent<Light>();
                if (light != null)
                    sb.AppendLine($"{indent}Light: type={light.type}  intensity={light.intensity:F2}  range={light.range:F2}");
            }
            catch { }

            // Animator
            try
            {
                var anim = go.GetComponent<Animator>();
                if (anim != null)
                {
                    string ctrlName = anim.runtimeAnimatorController != null
                        ? $"\"{anim.runtimeAnimatorController.name}\""
                        : "null";
                    sb.AppendLine($"{indent}Animator: controller={ctrlName}");
                }
            }
            catch { }
        }

        // ── Utilities ────────────────────────────────────────────────────────

        static string GetCamPos()
        {
            try
            {
                var cam = Camera.main;
                if (cam == null) return "none";
                var p = cam.transform.position;
                return $"({p.x:F1}, {p.y:F1}, {p.z:F1})";
            }
            catch { return "error"; }
        }
    }
}
