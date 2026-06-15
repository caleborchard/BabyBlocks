using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Editor-only ghost previews mirroring what other connected players are currently
    // dragging out of the prop palette (before they drop it). Built via
    // LevelEditor.CreateGhostObject so they look identical to the local drag-preview ghost.
    // Visibility mirrors RemoteFreecamManager's rule (only shown while the local player has
    // the Baby Blocks editor open).
    internal static class RemotePropGhostManager
    {
        const float StaleTimeoutSeconds = 6f;

        // After a sender's drag ends (placed or cancelled), briefly ignore any further
        // UpdateGhost calls from them. PropPlaced/PropGhostEnd are ReliableOrdered while
        // ghost updates are mostly Unreliable, so a few late in-flight ghost packets can
        // otherwise arrive afterwards and resurrect a stray ghost next to the real prop.
        const float PostEndSuppressSeconds = 1f;

        class Ghost
        {
            public GameObject root;
            public string propId;
            public float lastUpdateTime;
        }

        static readonly Dictionary<byte, Ghost> _ghosts = new();
        static readonly Dictionary<byte, float> _suppressedUntil = new();

        public static void Update()
        {
            if (_ghosts.Count == 0) return;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            float now = Time.unscaledTime;

            List<byte> stale = null;
            foreach (var kv in _ghosts)
            {
                var g = kv.Value;
                if (g.root == null || now - g.lastUpdateTime > StaleTimeoutSeconds)
                {
                    (stale ??= new List<byte>()).Add(kv.Key);
                    continue;
                }

                if (g.root.activeSelf != editorActive)
                    g.root.SetActive(editorActive);
            }

            if (stale != null)
                foreach (var uuid in stale) Remove(uuid);
        }

        public static void UpdateGhost(byte uuid, PropInfo info, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            if (_suppressedUntil.TryGetValue(uuid, out var until))
            {
                if (Time.unscaledTime < until) return;
                _suppressedUntil.Remove(uuid);
            }

            if (!_ghosts.TryGetValue(uuid, out var g) || g.root == null || g.propId != info.id)
            {
                if (g?.root != null) UnityEngine.Object.Destroy(g.root);

                PropLibrary.LoadPropData(info);
                if (!info.HasMesh) return;

                var root = LevelEditor.CreateGhostObject(info);
                if (root == null) return;

                g = new Ghost { root = root, propId = info.id };
                _ghosts[uuid] = g;
            }

            g.root.transform.SetPositionAndRotation(position, rotation);
            g.root.transform.localScale = scale;
            g.lastUpdateTime = Time.unscaledTime;

            bool editorActive = FlyCamController.FlyCamActive && FlyCamController.CursorMode;
            if (g.root.activeSelf != editorActive)
                g.root.SetActive(editorActive);
        }

        public static void Remove(byte uuid)
        {
            if (_ghosts.TryGetValue(uuid, out var g))
            {
                if (g.root != null) UnityEngine.Object.Destroy(g.root);
                _ghosts.Remove(uuid);
            }
        }

        // Removes the sender's ghost (if any) and ignores further UpdateGhost calls from
        // them for a short grace period, so late in-flight ghost-update packets don't
        // resurrect it after the drag has already ended (placed or cancelled).
        public static void NotifyDragEnded(byte uuid)
        {
            Remove(uuid);
            _suppressedUntil[uuid] = Time.unscaledTime + PostEndSuppressSeconds;
        }

        public static void ClearAll()
        {
            foreach (var g in _ghosts.Values)
                if (g.root != null) UnityEngine.Object.Destroy(g.root);
            _ghosts.Clear();
            _suppressedUntil.Clear();
        }
    }
}
