using System;
using System.IO;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace BabyBlocks
{
    // F3 toggles an on-screen FPS/perf overlay and starts logging samples to a CSV
    // (UserData/BabyBlocks/perf_log.csv) for diagnosing sustained low-FPS on large
    // custom maps. Scene-wide collider/renderer counts are refreshed periodically
    // since FindObjectsOfType over the whole scene is too heavy to run every frame.
    static class PerfMonitor
    {
        public static bool Active;

        const float SampleInterval = 1f;      // seconds between CSV rows / overlay refresh
        const float SceneStatsInterval = 5f;  // seconds between collider/renderer scans

        static float _accumTime;
        static int _accumFrames;
        static float _minFps = float.MaxValue;
        static float _displayFps;
        static float _displayMinFps;
        static float _displayFrameMs;

        static float _sceneStatsTimer;
        static int _colliderCount;
        static int _enabledColliderCount;
        static int _rendererCount;
        static int _enabledRendererCount;

        static StreamWriter _writer;

        static readonly string LogPath =
            Path.Combine(MelonEnvironment.UserDataDirectory, "BabyBlocks", "perf_log.csv");

        public static void Toggle()
        {
            Active = !Active;
            if (Active) Start(); else Stop();
        }

        static void Start()
        {
            _accumTime = 0f;
            _accumFrames = 0;
            _minFps = float.MaxValue;
            _sceneStatsTimer = 0f;
            RefreshSceneStats();

            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(LogPath, append: false);
                _writer.WriteLine("time,fps,minFps,frameMs,gcMemMB,objects,colliders,collidersEnabled,renderers,renderersEnabled,playerX,playerY,playerZ");
                _writer.Flush();
                MelonLogger.Msg($"[PerfMonitor] enabled, logging to {LogPath}");
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[PerfMonitor] failed to open log file: {e}");
                _writer = null;
            }
        }

        static void Stop()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null;
            MelonLogger.Msg("[PerfMonitor] disabled");
        }

        public static void OnUpdate()
        {
            if (!Active) return;

            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) return;

            float fps = 1f / dt;
            if (fps < _minFps) _minFps = fps;
            _accumTime += dt;
            _accumFrames++;

            _sceneStatsTimer += dt;
            if (_sceneStatsTimer >= SceneStatsInterval)
            {
                _sceneStatsTimer = 0f;
                RefreshSceneStats();
            }

            if (_accumTime >= SampleInterval)
            {
                _displayFps = _accumFrames / _accumTime;
                _displayFrameMs = (_accumTime / _accumFrames) * 1000f;
                _displayMinFps = _minFps;

                WriteSample();

                _accumTime = 0f;
                _accumFrames = 0;
                _minFps = float.MaxValue;
            }
        }

        static void RefreshSceneStats()
        {
            try
            {
                var colliders = UnityEngine.Object.FindObjectsOfType<Collider>(true);
                _colliderCount = colliders.Length;
                int enabledColliders = 0;
                foreach (var c in colliders)
                    if (c != null && c.enabled) enabledColliders++;
                _enabledColliderCount = enabledColliders;

                var renderers = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
                _rendererCount = renderers.Length;
                int enabledRenderers = 0;
                foreach (var r in renderers)
                    if (r != null && r.enabled) enabledRenderers++;
                _enabledRendererCount = enabledRenderers;
            }
            catch { }
        }

        static void WriteSample()
        {
            if (_writer == null) return;

            var camTransform = Camera.main != null ? Camera.main.transform : null;
            var pos = camTransform != null ? camTransform.position : Vector3.zero;
            float memMB = GC.GetTotalMemory(false) / (1024f * 1024f);
            int objCount = LevelEditorManager.Instance?.Objects.Count ?? 0;

            try
            {
                _writer.WriteLine(
                    $"{Time.realtimeSinceStartup:F1},{_displayFps:F1},{_displayMinFps:F1},{_displayFrameMs:F2},{memMB:F1}," +
                    $"{objCount},{_colliderCount},{_enabledColliderCount},{_rendererCount},{_enabledRendererCount}," +
                    $"{pos.x:F1},{pos.y:F1},{pos.z:F1}");
                _writer.Flush();
            }
            catch { }
        }

        public static void OnGUI()
        {
            if (!Active) return;

            const int x = 10, y = 10, w = 270, h = 116;
            GUI.Box(new Rect(x, y, w, h), "");

            var style = new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = Color.white } };
            int row = 0;
            void Line(string text)
            {
                GUI.Label(new Rect(x + 8, y + 6 + row * 20, w - 16, 20), text, style);
                row++;
            }

            Line($"FPS: {_displayFps:F1}  (min {_displayMinFps:F1})");
            Line($"Frame time: {_displayFrameMs:F2} ms");
            Line($"GC mem: {GC.GetTotalMemory(false) / (1024f * 1024f):F1} MB");
            Line($"Level objects: {LevelEditorManager.Instance?.Objects.Count ?? 0}");
            Line($"Colliders enabled: {_enabledColliderCount} / {_colliderCount}");
            Line($"Renderers enabled: {_enabledRendererCount} / {_rendererCount}");
        }
    }
}
