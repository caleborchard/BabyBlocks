using System;
using System.Collections.Generic;
using UnityEngine;

namespace BabyBlocks.Networking
{
    // Renders the custom Baby Blocks hats / grabables that OTHER connected players are
    // currently wearing or holding.
    //
    // The multiplayer client's native accessory sync only knows how to reconstruct
    // base-game hats/grabables (it looks the prop up by name in the game's Savables
    // GlobalObjectLoaders). A custom editor prop has no such loader, so the native path
    // can't represent it on a remote player. Instead, ModNetworking detects when the
    // LOCAL player picks up a custom prop and broadcasts it over the Baby Blocks channel;
    // this manager rebuilds a non-interactive visual copy on the matching remote player's
    // head/hand bone (resolved through ModNetworking's reflection bridge into the
    // multiplayer client's RemotePlayer).
    //
    // The copy is purely cosmetic — built with LevelEditor.CreateGhostObject (mesh +
    // materials only, no colliders/physics/LevelEditorObject) — so it never interferes
    // with the peer's own world copy of the same prop, which stays in their level exactly
    // as the base game leaves picked-up world items in place on other clients.
    //
    // Slots: 0 = hat, 1 = right hand (handItems[0]), 2 = left hand (handItems[1]).
    internal static class RemoteAccessoryManager
    {
        // A desired accessory that has never managed to attach (its RemotePlayer never
        // appeared, or left) is dropped after this long.
        const float UnresolvedTimeoutSeconds = 12f;

        // An attached accessory whose owner stopped re-broadcasting it (see
        // ModNetworking's AccessoryRebroadcastInterval) for this long is removed. This is a
        // safety net for the rare case a reliable "remove" packet is somehow missed — the
        // owner re-announces every worn accessory periodically, so a real removal stops the
        // refreshes and the copy is reclaimed here.
        const float AttachedStaleSeconds = 8f;

        class Worn
        {
            public string     propId;
            public Vector3    localPos;
            public Quaternion localRot;
            public Vector3    worldScale;
            public GameObject clone;        // null until (re)attached, or after its bone's player is gone
            public Transform  attachedBone;
            public float      lastSeenTime; // last time this don was (re)received
        }

        // Keyed by (player uuid, slot).
        static readonly Dictionary<(byte uuid, int slot), Worn> _worn = new();

        // Records/updates what a peer is wearing in a slot. Idempotent: re-receiving the
        // same prop just refreshes the keep-alive timestamp (and the pose, in case the
        // offset changed). A different prop in the same slot rebuilds the copy.
        public static void SetDesired(byte uuid, int slot, string propId, Vector3 localPos, Quaternion localRot, Vector3 worldScale)
        {
            if (string.IsNullOrEmpty(propId)) return;
            var key = (uuid, slot);
            if (!_worn.TryGetValue(key, out var w))
            {
                w = new Worn();
                _worn[key] = w;
            }

            if (w.clone != null && w.propId != propId)
            {
                UnityEngine.Object.Destroy(w.clone);
                w.clone = null;
                w.attachedBone = null;
            }

            w.propId     = propId;
            w.localPos   = localPos;
            w.localRot   = localRot;
            w.worldScale = worldScale;
            w.lastSeenTime = Time.unscaledTime;

            if (w.clone != null && w.attachedBone != null)
            {
                w.clone.transform.localPosition = localPos;
                w.clone.transform.localRotation = localRot;
                ApplyWorldScale(w.clone.transform, w.attachedBone, worldScale);
            }
        }

        public static void ClearDesired(byte uuid, int slot)
        {
            var key = (uuid, slot);
            if (_worn.TryGetValue(key, out var w))
            {
                if (w.clone != null) UnityEngine.Object.Destroy(w.clone);
                _worn.Remove(key);
            }
        }

        public static void Update()
        {
            if (_worn.Count == 0) return;
            float now = Time.unscaledTime;
            List<(byte uuid, int slot)> stale = null;

            foreach (var kv in _worn)
            {
                var w = kv.Value;

                // Clone destroyed out from under us (e.g. the RemotePlayer was disposed on
                // disconnect, taking its child copy with it) — forget the bone and retry.
                if (w.clone == null) w.attachedBone = null;

                if (w.clone == null)
                {
                    if (!TryBuild(kv.Key.uuid, kv.Key.slot, w)
                        && now - w.lastSeenTime > UnresolvedTimeoutSeconds)
                        (stale ??= new()).Add(kv.Key);
                }
                else if (now - w.lastSeenTime > AttachedStaleSeconds)
                {
                    (stale ??= new()).Add(kv.Key);
                }
            }

            if (stale != null)
                foreach (var key in stale)
                {
                    if (_worn.TryGetValue(key, out var w) && w.clone != null)
                        UnityEngine.Object.Destroy(w.clone);
                    _worn.Remove(key);
                }
        }

        static bool TryBuild(byte uuid, int slot, Worn w)
        {
            var bone = ModNetworking.GetRemoteAccessoryBone(uuid, slot);
            if (bone == null) return false;

            var info = PropLibrary.FindById(w.propId);
            if (info == null
                && w.propId.StartsWith("gpui://", StringComparison.OrdinalIgnoreCase)
                && GpuiPropScanner.GpuiScannedNames.Count == 0)
            {
                GpuiPropScanner.ScanGpuiProps();
                info = PropLibrary.FindById(w.propId);
                MaterialCatalog.InvalidateMaterialSources();
            }
            if (info == null) return false;

            PropLibrary.LoadPropData(info);
            if (!info.HasMesh) return false;

            var clone = LevelEditor.CreateGhostObject(info);
            if (clone == null) return false;
            clone.name = "BabyBlocks_RemoteAccessory";

            clone.transform.SetParent(bone, false);
            clone.transform.localPosition = w.localPos;
            clone.transform.localRotation = w.localRot;
            ApplyWorldScale(clone.transform, bone, w.worldScale);

            w.clone        = clone;
            w.attachedBone = bone;
            return true;
        }

        // Sets localScale so the copy's world scale matches worldScale despite the bone's
        // own (usually unit) lossy scale.
        static void ApplyWorldScale(Transform t, Transform bone, Vector3 worldScale)
        {
            var b = bone.lossyScale;
            t.localScale = new Vector3(
                b.x != 0f ? worldScale.x / b.x : worldScale.x,
                b.y != 0f ? worldScale.y / b.y : worldScale.y,
                b.z != 0f ? worldScale.z / b.z : worldScale.z);
        }

        public static void ClearAll()
        {
            foreach (var w in _worn.Values)
                if (w.clone != null) UnityEngine.Object.Destroy(w.clone);
            _worn.Clear();
        }
    }
}
