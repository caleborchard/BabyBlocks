using System;
using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppNWH.DWP2.WaterObjects;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;

namespace BabyBlocks
{
    // Rigidbody/Grabable/Hat component setup and per-object physics state: physics-mesh
    // building, collider construction, freeze/unfreeze for editor-mode transitions, and
    // the per-frame active/sleeping update for loose Rigidbody props. Group-root
    // management lives in GroupManager.
    internal static class PhysicsObjectManager
    {
        const float PhysicsActiveRadius    = 25f;
        const float PhysicsActiveRadiusSqr = PhysicsActiveRadius * PhysicsActiveRadius;

        internal static readonly HashSet<int> PhysicsControlSeen = new();
        static readonly List<LevelEditorObject> _heldObjectsToRestore = new();
        static readonly List<Vector3> _heldScalesToRestore = new();

        // Tracks LevelEditorObject GO instance IDs that are currently waiting for their
        // first collision in gameplay ("freeze until hit" pending state).
        // Uses a HashSet instead of a component to avoid Il2CppInterop's generic
        // AddComponent<T>/GetComponent<T> static-constructor crash for mod-defined types
        // (same pattern as BbHatSunglassesFlag).  OnCollisionEnter is handled directly
        // on LevelEditorObject, which is already registered and working.
        static readonly HashSet<int> _freezeUntilHitPending = new();

        internal static bool HasFreezeUntilHit(LevelEditorObject obj)
            => obj?.gameObject != null && _freezeUntilHitPending.Contains(obj.gameObject.GetInstanceID());

        static void AddFreezeUntilHit(LevelEditorObject obj)
        {
            if (obj?.gameObject != null)
                _freezeUntilHitPending.Add(obj.gameObject.GetInstanceID());
        }

        static void RemoveFreezeUntilHit(LevelEditorObject obj)
        {
            if (obj?.gameObject != null)
                _freezeUntilHitPending.Remove(obj.gameObject.GetInstanceID());
        }

        // Called by LevelEditorObject.OnCollisionEnter when the prop is first hit.
        internal static void OnFreezeUntilHitTriggered(LevelEditorObject obj)
            => RemoveFreezeUntilHit(obj);

        // Called by ModNetworking when a peer's freeze-until-hit prop was hit.
        internal static void RemoveFreezeUntilHitForNetworkPeer(LevelEditorObject obj)
            => RemoveFreezeUntilHit(obj);

        // Behaviour components disabled on keepHierarchy props when entering editor
        // mode (keyed by LEO instance ID). Re-enabled when gameplay resumes.
        static readonly Dictionary<int, List<Behaviour>> _frozenKHBehaviours = new();

        static readonly Dictionary<int, Dictionary<string, Mesh>> _physicsMeshCache = new();

        internal static void ReleasePhysicsMeshes(PropInfo info)
        {
            if (info == null) return;
            foreach (var part in info.parts)
            {
                if (part?.mesh == null) continue;
                int id = part.mesh.GetInstanceID();
                if (_physicsMeshCache.TryGetValue(id, out var physMap))
                {
                    _physicsMeshCache.Remove(id);
                    foreach (var phys in physMap.Values)
                    {
                        if (phys != null) UnityEngine.Object.Destroy(phys);
                    }
                }
            }
            foreach (var cp in info.colliderParts)
            {
                if (cp?.mesh == null) continue;
                int id = cp.mesh.GetInstanceID();
                if (_physicsMeshCache.TryGetValue(id, out var physMap))
                {
                    _physicsMeshCache.Remove(id);
                    foreach (var phys in physMap.Values)
                    {
                        if (phys != null) UnityEngine.Object.Destroy(phys);
                    }
                }
            }
        }

        // Game meshes ship without Read/Write enabled; GetVertexBuffer/GetIndexBuffer bypass that.
        // Position is assumed Float32×3 at byte-offset 0 in stream 0 (standard Unity layout).
        internal static Mesh BuildPhysicsMesh(Mesh source, HashSet<int> ignoredSubmeshes = null)
        {
            if (source == null) return null;
            int id = source.GetInstanceID();
            string cacheKey = ignoredSubmeshes == null || ignoredSubmeshes.Count == 0
                ? string.Empty
                : string.Join(",", ignoredSubmeshes.OrderBy(v => v));
            if (_physicsMeshCache.TryGetValue(id, out var physMap))
            {
                if (physMap == null) return null; // known-bad mesh; don't retry
                if (physMap.TryGetValue(cacheKey, out var hit))
                    return hit;
            }

            Mesh result = null;
            try
            {
                bool posOk = false;
                foreach (var a in source.GetVertexAttributes())
                {
                    if (a.attribute == UnityEngine.Rendering.VertexAttribute.Position
                        && a.format    == UnityEngine.Rendering.VertexAttributeFormat.Float32
                        && a.dimension == 3
                        && a.stream    == 0)
                    { posOk = true; break; }
                }
                if (!posOk) { _physicsMeshCache[id] = null; return null; }

                var vb         = source.GetVertexBuffer(0);
                int floatsPerV = vb.stride / 4;
                int vCount     = source.vertexCount;
                var floatBuf   = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>(vCount * floatsPerV);
                vb.GetData(floatBuf.Cast<Il2CppSystem.Array>());
                vb.Release();

                var positions = new Vector3[vCount];
                for (int i = 0; i < vCount; i++)
                {
                    int b = i * floatsPerV;
                    positions[i] = new Vector3(floatBuf[b], floatBuf[b + 1], floatBuf[b + 2]);
                }

                var ib     = source.GetIndexBuffer();
                int iCount = ib.count;
                int[] tris = new int[iCount];
                if (ib.stride == 2)
                {
                    var idxBuf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<ushort>(iCount);
                    ib.GetData(idxBuf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = idxBuf[i];
                }
                else
                {
                    var idxBuf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>(iCount);
                    ib.GetData(idxBuf.Cast<Il2CppSystem.Array>());
                    for (int i = 0; i < iCount; i++) tris[i] = idxBuf[i];
                }

                int[] filteredTris = tris;
                if (ignoredSubmeshes != null && ignoredSubmeshes.Count > 0 && source.subMeshCount > 0)
                {
                    var combined = new List<int>();
                    for (int sub = 0; sub < source.subMeshCount; sub++)
                    {
                        if (ignoredSubmeshes.Contains(sub)) continue;

                        var sm = source.GetSubMesh(sub);
                        int start = sm.indexStart;
                        int count = sm.indexCount;
                        if (start < 0 || count <= 0 || start + count > tris.Length) continue;

                        for (int i = start; i < start + count; i++)
                            combined.Add(tris[i]);
                    }

                    if (combined.Count > 0)
                        filteredTris = combined.ToArray();
                }

                ib.Release();

                // When submeshes were filtered out, strip unreferenced vertices so the convex
                // hull (used in Rigidbody mode) doesn't include geometry from ignored submeshes.
                if (filteredTris != tris)
                {
                    var used   = new HashSet<int>(filteredTris);
                    var newPos = new Vector3[used.Count];
                    var remap  = new int[vCount];
                    int ni = 0;
                    for (int i = 0; i < vCount; i++)
                    {
                        if (used.Contains(i)) { remap[i] = ni; newPos[ni++] = positions[i]; }
                    }
                    positions = newPos;
                    for (int i = 0; i < filteredTris.Length; i++)
                        filteredTris[i] = remap[filteredTris[i]];
                }

                result = new Mesh { name = source.name + "_phys" };
                result.vertices  = positions;
                result.triangles = filteredTris;
                result.RecalculateNormals();
                result.RecalculateBounds();

                if (!_physicsMeshCache.TryGetValue(id, out physMap))
                {
                    physMap = new Dictionary<string, Mesh>();
                    _physicsMeshCache[id] = physMap;
                }
                physMap[cacheKey] = result;
            }
            catch { result = null; }
            return result;
        }

        public static void ApplyColliderParts(GameObject root, PropInfo info, bool applyRenderMesh = false)
        {
            if (root == null || info == null) return;
            int layer = LevelEditorManager.PropLayer;

            // Render mesh override takes priority when explicitly requested.
            // Falls through to pre-cooked colliders otherwise.
            if (!applyRenderMesh)
            {
                if (!info.HasColliderParts) return;

                for (int i = 0; i < info.colliderParts.Count; i++)
                {
                    var cp = info.colliderParts[i];
                    var go = new GameObject($"PropCollider_{i}");
                    go.transform.SetParent(root.transform, false);
                    go.transform.localPosition = cp.localPosition;
                    go.transform.localRotation = cp.localRotation;
                    go.transform.localScale    = cp.localScale;
                    go.layer = layer;
                    switch (cp.type)
                    {
                        case PropColliderPart.ColliderType.Mesh:
                            var mc = go.AddComponent<MeshCollider>();
                            // Use an owned physics mesh so scene unloads can't evict the CPU mesh data.
                            var physMesh = BuildPhysicsMesh(cp.mesh);
                            mc.sharedMesh = physMesh ?? cp.mesh;
                            mc.convex     = cp.convex;
                            break;
                        case PropColliderPart.ColliderType.Box:
                            var bc2 = go.AddComponent<BoxCollider>();
                            bc2.center = cp.center;
                            bc2.size   = cp.size;
                            break;
                        case PropColliderPart.ColliderType.Sphere:
                            var sc = go.AddComponent<SphereCollider>();
                            sc.center = cp.center;
                            sc.radius = cp.radius;
                            break;
                        case PropColliderPart.ColliderType.Capsule:
                            var cc = go.AddComponent<CapsuleCollider>();
                            cc.center    = cp.center;
                            cc.radius    = cp.radius;
                            cc.height    = cp.height;
                            cc.direction = cp.direction;
                            break;
                    }
                }
                return;
            }

            var seenBounds = new HashSet<string>();
            int colIdx = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled) continue;

                var b   = mf.sharedMesh.bounds;
                string key = $"{b.center.x:F2},{b.center.y:F2},{b.center.z:F2}|{b.size.x:F2},{b.size.y:F2},{b.size.z:F2}";
                if (!seenBounds.Add(key)) continue;

                var go = new GameObject($"PropCollider_{colIdx++}");
                go.transform.SetParent(root.transform, false);
                go.transform.localPosition = mf.transform.localPosition;
                go.transform.localRotation = mf.transform.localRotation;
                go.transform.localScale    = mf.transform.localScale;
                go.layer = layer;

                var ignoredSubs = PropMetadataStore.GetColliderIgnoredSubmeshes(info.id);
                var physMesh = BuildPhysicsMesh(mf.sharedMesh, ignoredSubs);
                var mc = go.AddComponent<MeshCollider>();
                if (physMesh != null)
                    mc.sharedMesh = physMesh;
                else if (ignoredSubs == null || ignoredSubs.Count == 0)
                    mc.sharedMesh = mf.sharedMesh;
                else
                    UnityEngine.Object.Destroy(go);
            }
        }

        // Physics-state helpers

        internal static Vector3 GetPlayerReferencePosition(Transform reference)
        {
            var player = PlayerMovement.me;
            if (player != null)
                return player.head != null ? player.head.position : player.transform.position;
            return reference != null ? reference.position : Vector3.zero;
        }

        internal static void UpdatePhysicsObjectState(LevelEditorObject obj, Vector3 playerPos)
        {
            if (LevelEditorManager.Instance._editorModeActive) return;
            var control = GetPhysicsControlObject(obj);
            if (control == null) return;

            int controlId = control.GetInstanceID();
            if (!PhysicsControlSeen.Add(controlId)) return;

            var rb = control.GetComponent<Rigidbody>();
            if (rb == null) return;
            if (control.GetComponent<Grabable>() != null) return; // grabables manage their own kinematic state

            // Props waiting for their first collision manage their own kinematic state.
            if (HasFreezeUntilHit(obj)) return;

            float distSqr = (control.transform.position - playerPos).sqrMagnitude;
            bool active = distSqr <= PhysicsActiveRadiusSqr;
            if (active)
            {
                if (rb.isKinematic)
                {
                    rb.isKinematic = false;
                    SetMeshCollidersConvex(control, true); // going dynamic: need convex
                    SetColliderLayers(control, LevelEditorManager.PropsDynamicLayer);
                }
                rb.useGravity = true;
            }
            else if (!rb.isKinematic)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                SetMeshCollidersConvex(control, false); // back to kinematic: concave OK
                SetColliderLayers(control, LevelEditorManager.PropLayer);
            }
        }

        static GameObject GetPhysicsControlObject(LevelEditorObject obj)
        {
            if (obj == null) return null;
            var grabable = obj.GetComponentInParent<Grabable>(true);
            if (grabable != null) return grabable.gameObject;
            var rb = obj.GetComponentInParent<Rigidbody>(true);
            if (rb != null) return rb.gameObject;
            return obj.gameObject;
        }

        internal static void MarkPhysicsChunkIndependent(LevelEditorObject obj)
        {
            if (obj == null) return;
            obj.chunkIndex = 255;
            obj.chunkCoord = new Vector2Int(-1, -1);
        }

        public static void SetEditorModeActive(bool active)
        {
            var mgr = LevelEditorManager.Instance;
            if (mgr._editorModeActive == active) return;
            mgr._editorModeActive = active;
            if (active)
            {
                ReleasePlayerHeldObjects();
                RestoreHeldObjectScales();
                EnterEditorPhysicsMode();

                // GhostCollisionCutter.RestoreAllColliderCarves(); // prop collider carving disabled
            }
            else
            {
                ExitEditorPhysicsMode();

                // GhostCollisionCutter.BakeAllColliderCarves(); // prop collider carving disabled
            }
        }

        static void ReleasePlayerHeldObjects()
        {
            var player = PlayerMovement.me;
            _heldObjectsToRestore.Clear();
            _heldScalesToRestore.Clear();
            if (player == null) return;

            if (player.currentHat != null)
            {
                var hatObj = player.currentHat.GetComponent<LevelEditorObject>();
                if (hatObj != null)
                {
                    _heldObjectsToRestore.Add(hatObj);
                    _heldScalesToRestore.Add(hatObj.transform.localScale);
                }
                player.KnockOffHat();
            }

            for (int i = 0; i < player.handItems.Length; i++)
            {
                if (player.handItems[i] != null)
                {
                    var handObj = player.handItems[i].GetComponent<LevelEditorObject>();
                    if (handObj != null)
                    {
                        _heldObjectsToRestore.Add(handObj);
                        _heldScalesToRestore.Add(handObj.transform.localScale);
                    }
                    player.DropHandItem(i);
                }
            }
        }

        static void RestoreHeldObjectScales()
        {
            int count = Mathf.Min(_heldObjectsToRestore.Count, _heldScalesToRestore.Count);
            for (int i = 0; i < count; i++)
            {
                var obj = _heldObjectsToRestore[i];
                if (obj != null) obj.transform.localScale = _heldScalesToRestore[i];
            }
            _heldObjectsToRestore.Clear();
            _heldScalesToRestore.Clear();
        }

        static void EnterEditorPhysicsMode()
        {
            var mgr = LevelEditorManager.Instance;
            var seenGroups = new HashSet<int>();
            foreach (var obj in mgr.Objects)
            {
                if (obj == null || obj.physicsMode == PhysicsMode.Static) continue;

                if (obj.physicsMode == PhysicsMode.Rigidbody)
                {
                    if (obj.physicsGroupId > 0)
                    {
                        if (!seenGroups.Add(obj.physicsGroupId)) continue;
                        GroupManager.FreezeRigidBodyGroupForEditor(obj.physicsGroupId);
                    }
                    else
                    {
                        FreezeRigidBodyObject(obj, true);   // zero velocity + go kinematic first
                        RestoreBasePose(obj);               // then snap — kinematic so no drift
                        if (IsKeepHierarchyRigidbody(obj))
                            DisableKeepHierarchyBehaviours(obj);
                        obj.isPhysicsManaged = true;
                    }
                    continue;
                }

                if (obj.physicsGroupId > 0)
                {
                    if (!seenGroups.Add(obj.physicsGroupId)) continue;
                    GroupManager.DeactivateGroupForEditor(obj.physicsGroupId);
                }
                else
                {
                    DeactivateSoloWearableForEditor(obj);
                }
            }
        }

        static void ExitEditorPhysicsMode()
        {
            var mgr = LevelEditorManager.Instance;
            var seenGroups = new HashSet<int>();
            foreach (var obj in mgr.Objects)
            {
                if (obj == null || obj.physicsMode != PhysicsMode.Rigidbody) continue;

                if (obj.physicsGroupId > 0)
                {
                    if (!seenGroups.Add(obj.physicsGroupId)) continue;
                    GroupManager.UnfreezeRigidBodyGroup(obj.physicsGroupId);
                }
                else
                {
                    if (IsKeepHierarchyRigidbody(obj))
                        ReEnableKeepHierarchyBehaviours(obj);
                    if (obj.freezeUntilHit)
                    {
                        // Stay kinematic but on the dynamic layer with convex colliders so
                        // player Rigidbodies can generate OnCollisionEnter on this LEO.
                        obj.editorFreezeStateValid = false;
                        SetMeshCollidersConvex(obj.gameObject, true);
                        SetColliderLayers(obj.gameObject, LevelEditorManager.PropsDynamicLayer);
                        AddFreezeUntilHit(obj);
                    }
                    else
                    {
                        FreezeRigidBodyObject(obj, false);
                    }
                }
                obj.isPhysicsManaged = true;
            }
            GroupManager.ApplyGroups();
        }

        internal static void FreezeRigidBodyObject(LevelEditorObject obj, bool freeze)
        {
            if (obj == null) return;
            var rb = obj.GetComponent<Rigidbody>();
            if (rb == null) return;

            if (freeze)
            {
                // Remove any pending freeze-until-hit watcher from a previous gameplay session.
                RemoveFreezeUntilHit(obj);

                if (!obj.editorFreezeStateValid)
                {
                    obj.editorFreezeVelocity        = Vector3.zero;
                    obj.editorFreezeAngularVelocity  = Vector3.zero;
                    // UpdatePhysicsObjectState puts Rigidbody props to sleep (kinematic)
                    // when far from the player. That transient sleeping state is NOT the
                    // logical gameplay state — always restore to dynamic + gravity.
                    if (obj.physicsMode == PhysicsMode.Rigidbody)
                    {
                        obj.editorFreezeIsKinematic = false;
                        obj.editorFreezeUseGravity  = true;
                    }
                    else
                    {
                        obj.editorFreezeIsKinematic = rb.isKinematic;
                        obj.editorFreezeUseGravity  = rb.useGravity;
                    }
                    obj.editorFreezeConstraints      = rb.constraints;
                    obj.editorFreezeStateValid       = true;
                }
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                rb.constraints     = RigidbodyConstraints.FreezeAll;
                SetMeshCollidersConvex(obj.gameObject, false); // kinematic: concave OK
                SetColliderLayers(obj.gameObject, LevelEditorManager.PropLayer);
            }
            else if (obj.editorFreezeStateValid)
            {
                rb.constraints     = obj.editorFreezeConstraints;
                rb.isKinematic     = obj.editorFreezeIsKinematic;
                rb.useGravity      = obj.editorFreezeUseGravity;
                rb.velocity        = obj.editorFreezeVelocity;
                rb.angularVelocity = obj.editorFreezeAngularVelocity;
                obj.editorFreezeStateValid = false;
                if (!rb.isKinematic)
                {
                    SetMeshCollidersConvex(obj.gameObject, true); // going dynamic: need convex
                    SetColliderLayers(obj.gameObject, LevelEditorManager.PropsDynamicLayer);
                }
            }
        }

        // Called when a freeze-until-hit prop is struck during gameplay.
        // collision is null for the network peer path (no collision data is sent over the wire).
        internal static void UnfreezeHitProp(LevelEditorObject obj, Collision collision = null)
        {
            if (obj == null) return;
            var rb = obj.GetComponent<Rigidbody>();
            if (rb == null) return;
            rb.constraints = RigidbodyConstraints.None;
            rb.isKinematic = false;
            rb.useGravity  = true;
            SetMeshCollidersConvex(obj.gameObject, true);
            SetColliderLayers(obj.gameObject, LevelEditorManager.PropsDynamicLayer);
            // The collision was resolved against a kinematic body (infinite mass), so no impulse
            // was transferred. Apply the impact velocity manually so the prop reacts immediately.
            if (collision != null)
                rb.AddForce(-collision.relativeVelocity, ForceMode.VelocityChange);
        }

        internal static void FreezeRigidBodyGameObject(GameObject go, bool freeze)
        {
            if (go == null) return;
            var rb = go.GetComponent<Rigidbody>();
            if (rb == null) return;
            if (freeze)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                rb.constraints     = RigidbodyConstraints.FreezeAll;
                SetMeshCollidersConvex(go, false); // kinematic: concave OK
                SetColliderLayers(go, LevelEditorManager.PropLayer);
            }
            else
            {
                rb.constraints = RigidbodyConstraints.None;
                rb.isKinematic = false;
                rb.useGravity  = true;
                SetMeshCollidersConvex(go, true); // going dynamic: need convex
                SetColliderLayers(go, LevelEditorManager.PropsDynamicLayer);
            }
        }

        static bool IsKeepHierarchyRigidbody(LevelEditorObject obj)
            => obj != null
            && !string.IsNullOrEmpty(obj.addressableKey)
            && PropMetadataStore.GetKeepOriginalHierarchy(obj.addressableKey)
            && obj.GetComponent<Rigidbody>() != null;

        // Disable all non-LevelEditorObject root Behaviours on a keepHierarchy prop so
        // native scripts (Skateboard etc.) don't fight RestoreBasePose while in editor.
        internal static void DisableKeepHierarchyBehaviours(LevelEditorObject obj)
        {
            if (obj == null) return;
            int id = obj.GetInstanceID();
            if (_frozenKHBehaviours.ContainsKey(id)) return;
            var disabled = new List<Behaviour>();
            var comps = obj.gameObject.GetComponents<Behaviour>();
            for (int i = 0; i < comps.Length; i++)
            {
                var b = comps[i];
                if (b == null || !b.enabled) continue;
                if (b.TryCast<LevelEditorObject>() != null) continue;
                b.enabled = false;
                disabled.Add(b);
            }
            _frozenKHBehaviours[id] = disabled;
        }

        // Re-enable Behaviours that DisableKeepHierarchyBehaviours previously disabled.
        internal static void ReEnableKeepHierarchyBehaviours(LevelEditorObject obj)
        {
            if (obj == null) return;
            int id = obj.GetInstanceID();
            if (!_frozenKHBehaviours.TryGetValue(id, out var list)) return;
            foreach (var b in list)
                if (b != null) b.enabled = true;
            _frozenKHBehaviours.Remove(id);
        }

        internal static void SetHierarchyCollisions(GameObject go, bool enabled)
        {
            if (go == null) return;
            foreach (var col in go.GetComponentsInChildren<Collider>(true))
                col.enabled = enabled;
        }

        static void DeactivateSoloWearableForEditor(LevelEditorObject obj)
        {
            SetHierarchyCollisions(obj.gameObject, false);
            RemoveGrabableComponents(obj.gameObject);
            RestoreBasePose(obj);
            SetHierarchyCollisions(obj.gameObject, true);
            obj.isPhysicsManaged = false;
        }

        internal static void RestoreBasePose(LevelEditorObject obj)
        {
            if (obj == null) return;
            if (obj.hasLoopBasePosition) obj.transform.position   = obj.loopBasePosition;
            if (obj.hasLoopBaseRotation) obj.transform.rotation   = obj.loopBaseRotation;
            if (obj.hasLoopBaseScale)    obj.transform.localScale = obj.loopBaseScale;
        }

        // Destroys PropCollider_* direct children and rebuilds them from prop metadata.
        // Called before physics activation to ensure collider meshes are fresh (so the
        // convex hull in Rigidbody mode only covers geometry from non-ignored submeshes),
        // and after clearing physics back to Static to restore non-convex colliders.
        static void RebuildPropColliders(LevelEditorObject leo)
        {
            if (leo == null || string.IsNullOrEmpty(leo.addressableKey)) return;
            var info = PropLibrary.FindById(leo.addressableKey);
            if (info == null) return;

            // Evict submesh-filtered cache entries so BuildPhysicsMesh produces fresh
            // vertex-stripped meshes (needed for correct convex hulls in Rigidbody mode).
            var ignoredSubs = PropMetadataStore.GetColliderIgnoredSubmeshes(info.id);
            if (ignoredSubs != null && ignoredSubs.Count > 0)
            {
                string cacheKey = string.Join(",", ignoredSubs.OrderBy(v => v));
                foreach (var mf in leo.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (mf?.sharedMesh == null) continue;
                    int meshId = mf.sharedMesh.GetInstanceID();
                    if (_physicsMeshCache.TryGetValue(meshId, out var physMap) && physMap != null)
                        physMap.Remove(cacheKey);
                }
            }

            var go = leo.gameObject;
            for (int i = go.transform.childCount - 1; i >= 0; i--)
            {
                var child = go.transform.GetChild(i);
                if (child != null && child.name != null && child.name.StartsWith("PropCollider_"))
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
            ApplyColliderParts(go, info, PropMetadataStore.GetUseRenderMeshCollider(info.id));
        }

        public static void ActivatePhysics(LevelEditorObject leo)
        {
            RebuildPropColliders(leo);
            var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
            if (leo.physicsMode == PhysicsMode.Rigidbody)
            {
                AddRigidBodyComponent(leo.gameObject, colls, leo.addressableKey);
                FreezeRigidBodyObject(leo, true);
            }
            else
            {
                AddGrabableComponent(leo.gameObject, leo.physicsMode == PhysicsMode.Hat, colls, leo.addressableKey);
                SyncHatHairAmount(leo);
                if (leo.physicsMode == PhysicsMode.Grabable || leo.physicsMode == PhysicsMode.Hat) SyncGrabOffset(leo);
                if (leo.physicsMode == PhysicsMode.Hat && BbHatSunglassesFlag.Has(leo))
                {
                    var hat = leo.gameObject.GetComponent<Hat>();
                    if (hat != null) hat.isSunglasses = true;
                }
            }
            MarkPhysicsChunkIndependent(leo);
            leo.isPhysicsManaged = true;
        }

        [HideFromIl2Cpp]
        internal static void SyncHatHairAmount(LevelEditorObject leo)
        {
            if (leo == null) return;
            Hat hat = leo.GetComponent<Hat>();
            if (hat == null)
            {
                var p = leo.transform.parent;
                while (p != null && hat == null) { hat = p.GetComponent<Hat>(); p = p.parent; }
            }
            if (hat != null) hat.hairAmt = Mathf.Clamp01(leo.hatHairAmt);
        }

        [HideFromIl2Cpp]
        internal static void SyncGrabOffset(LevelEditorObject leo)
        {
            if (leo == null) return;
            Grabable g = leo.GetComponent<Grabable>();
            if (g == null)
            {
                var p = leo.transform.parent;
                while (p != null && g == null) { g = p.GetComponent<Grabable>(); p = p.parent; }
            }
            if (g == null) return;
            var ptsR  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsR = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            var ptsL  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsL = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            ptsR.Add(leo.grabOffsetPos); rotsR.Add(Quaternion.Euler(leo.grabOffsetRot));
            ptsL.Add(leo.grabOffsetPos); rotsL.Add(Quaternion.Euler(leo.grabOffsetRot));
            g.grabLocPtsR = ptsR; g.grabLocRotsR = rotsR;
            g.grabLocPtsL = ptsL; g.grabLocRotsL = rotsL;
        }

        [HideFromIl2Cpp]
        internal static void SyncLoadedHatHairValues()
        {
            foreach (var leo in LevelEditorManager.Instance.Objects)
            {
                if (leo == null || leo.physicsMode != PhysicsMode.Hat) continue;
                SyncHatHairAmount(leo);
            }
        }

        // Re-applies baking (or the lack of it) to every placed instance of `propId` that
        // currently has physics, after the user toggles PropMetadataPanel's per-prop
        // "disable baking" setting in the Physics window. Restoring first undoes any
        // existing bake (no-op if it was never baked), then Bake() re-runs and either
        // re-bakes (using the disk cache when available) or, if disabled, leaves the
        // restored plain/native materials in place.
        internal static void RefreshBakingForProp(string propId)
        {
            if (string.IsNullOrEmpty(propId)) return;
            foreach (var leo in LevelEditorManager.Instance.Objects)
            {
                if (leo == null || leo.physicsMode == PhysicsMode.Static) continue;
                if (!string.Equals(leo.addressableKey, propId, StringComparison.Ordinal)) continue;

                MaterialBaker.RestoreOriginal(leo.gameObject);
                MaterialBaker.Bake(leo.gameObject, propId);
            }
        }

        // restoreMaterial=false is used when switching directly between physics modes
        // (Rigidbody/Grabable/Hat <-> each other) - the baked mesh/material is left in
        // place (and its _bakeStash entry kept) so MaterialBaker.Bake's "already baked"
        // guard skips recapturing it, instead of restoring the original then re-baking
        // from scratch. Only a transition to Static restores the original mesh/materials.
        public static void ClearPhysics(LevelEditorObject leo, bool restoreMaterial = true)
        {
            if (leo == null || leo.physicsMode == PhysicsMode.Static) return;
            var mgr = LevelEditorManager.Instance;

            if (leo.physicsGroupId <= 0)
            {
                if (restoreMaterial) MaterialBaker.RestoreOriginal(leo.gameObject);
                RemoveGrabableComponents(leo.gameObject);
                if (restoreMaterial) RebuildPropColliders(leo);
                leo.isPhysicsManaged = false;
                leo.physicsMode      = PhysicsMode.Static;
                leo.physicsGroupId   = 0;
                mgr.SyncLoopBase(leo);
            }
            else
            {
                int gid = leo.physicsGroupId;
                var root = GroupManager.GetGroupRoot(gid);
                if (root != null) { RemoveGrabableComponents(root); root.name = "Group"; }
                foreach (var obj in mgr.Objects)
                {
                    if (obj == null || obj.physicsGroupId != gid) continue;
                    if (restoreMaterial) MaterialBaker.RestoreOriginal(obj.gameObject);
                    if (restoreMaterial) RebuildPropColliders(obj);
                    obj.physicsMode      = PhysicsMode.Static;
                    obj.physicsGroupId   = 0;
                    obj.isPhysicsManaged = false;
                    mgr.SyncLoopBase(obj);
                }
            }
        }

        // Sets convex on all MeshColliders under `go`. Called when a Rigidbody prop transitions
        // between kinematic (editor, concave OK) and dynamic (game, convex required by PhysX).
        static void SetMeshCollidersConvex(GameObject go, bool convex)
        {
            if (go == null) return;
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true))
                mc.convex = convex;
        }

        // Switches all colliders under `go` to the given layer. Called alongside
        // SetMeshCollidersConvex so Rigidbody props use PropsDynamic (24) while active —
        // matching native dynamic props — and revert to Props (16) when kinematic/in-editor.
        static void SetColliderLayers(GameObject go, int layer)
        {
            if (go == null) return;
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
                c.gameObject.layer = layer;
        }

        // kg/m³ — wood-like density; yields ~2 kg for a football-sized sphere (r=0.1 m),
        // which matches the native Football prop mass.
        const float PropDensityKgM3 = 500f;
        const float PropMassMin     = 0.1f;
        const float PropMassMax     = 200f;

        static float ComputePropMass(Collider[] colls)
        {
            float volume = 0f;
            if (colls != null)
                foreach (var c in colls)
                    if (c != null && !c.isTrigger) volume += ColliderVolume(c);
            float mass = volume * PropDensityKgM3;
            return Mathf.Clamp(mass, PropMassMin, PropMassMax);
        }

        static float ColliderVolume(Collider c)
        {
            var s = c.transform.lossyScale;
            if (c is SphereCollider sc)
            {
                float r = sc.radius * MaxAbs(s);
                return (4f / 3f) * Mathf.PI * r * r * r;
            }
            if (c is BoxCollider bc)
            {
                var sz = bc.size;
                return Mathf.Abs(sz.x * s.x) * Mathf.Abs(sz.y * s.y) * Mathf.Abs(sz.z * s.z);
            }
            if (c is CapsuleCollider cc)
            {
                float r, cylH;
                switch (cc.direction)
                {
                    case 0:  // X-axis
                        r    = cc.radius * Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z));
                        cylH = Mathf.Max(0f, cc.height * Mathf.Abs(s.x) - 2f * r);
                        break;
                    case 2:  // Z-axis
                        r    = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.y));
                        cylH = Mathf.Max(0f, cc.height * Mathf.Abs(s.z) - 2f * r);
                        break;
                    default: // 1 = Y-axis
                        r    = cc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
                        cylH = Mathf.Max(0f, cc.height * Mathf.Abs(s.y) - 2f * r);
                        break;
                }
                return (4f / 3f) * Mathf.PI * r * r * r + Mathf.PI * r * r * cylH;
            }
            if (c is MeshCollider mc && mc.sharedMesh != null)
                return MeshSignedVolume(mc.sharedMesh, s);
            // Fallback: half of axis-aligned bounds volume
            var b = c.bounds;
            return b.size.x * b.size.y * b.size.z * 0.5f;
        }

        // Divergence-theorem signed-volume integral — exact for any closed mesh.
        static float MeshSignedVolume(Mesh mesh, Vector3 scale)
        {
            var verts = mesh.vertices;
            var tris  = mesh.triangles;
            float vol = 0f;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var p1 = Vector3.Scale(verts[tris[i]],     scale);
                var p2 = Vector3.Scale(verts[tris[i + 1]], scale);
                var p3 = Vector3.Scale(verts[tris[i + 2]], scale);
                vol += Vector3.Dot(p1, Vector3.Cross(p2, p3));
            }
            return Mathf.Abs(vol) / 6f;
        }

        static float MaxAbs(Vector3 v)
            => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        internal static void AddRigidBodyComponent(GameObject go, Collider[] colls, string propId = null)
        {
            // MaterialBaker.Bake(go, propId); // disabled for now
            // Note: convex is NOT set here. Colliders stay concave in editor (isKinematic=true).
            // SetMeshCollidersConvex is called at unfreeze time when the rb goes dynamic.
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.mass                   = ComputePropMass(colls);
            rb.isKinematic            = false;
            rb.useGravity             = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
            MelonLoader.MelonLogger.Msg($"[Physics] '{go.name}' mass={rb.mass:F2} kg");
        }

        internal static void AddGrabableComponent(GameObject go, bool isHat, Collider[] colls, string propId = null)
        {
            if (go == null) return;
            // MaterialBaker.Bake(go, propId); // disabled for now
            if (colls == null) colls = Array.Empty<Collider>();

            // Grabable/Hat rigidbodies are always kinematic, so convex is never required.
            var existingHat      = go.GetComponent<Hat>();
            var existingGrabable = go.GetComponent<Grabable>();
            Grabable g = null;

            if (isHat)
            {
                if (existingGrabable != null && existingHat == null) UnityEngine.Object.DestroyImmediate(existingGrabable);
                g = existingHat != null ? existingHat : go.AddComponent<Hat>();
            }
            else
            {
                if (existingHat != null) UnityEngine.Object.DestroyImmediate(existingHat);
                g = existingGrabable != null ? existingGrabable : go.AddComponent<Grabable>();
            }

            if (g == null) return;

            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.mass                   = 5f;
            rb.isKinematic            = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;

            var crusher = go.transform.Find("Crusher");
            if (crusher == null)
            {
                var co = new GameObject("Crusher");
                co.transform.SetParent(go.transform, false);
                crusher = co.transform;
            }

            g.rb  = rb;
            g.rbs = new Il2CppReferenceArray<Rigidbody>(1); g.rbs[0] = rb;

            // For hats: native WearHat accesses hat.colls[0] and casts it to BoxCollider
            // (all native game hats use BoxColliders). Prepend a tiny trigger BoxCollider so
            // the cast succeeds without affecting physics. The actual physics colliders follow
            // at indices 1+ and are still body-collision-ignored by IgnoreBodyCollisions.
            if (isHat)
            {
                var bc     = go.AddComponent<BoxCollider>();
                bc.size    = new Vector3(0.01f, 0.01f, 0.01f);
                bc.isTrigger = true;
                var hatColls = new Il2CppReferenceArray<Collider>(colls.Length + 1);
                hatColls[0] = bc;
                for (int i = 0; i < colls.Length; i++) if (colls[i] != null) hatColls[i + 1] = colls[i];
                g.colls = hatColls;
            }
            else
            {
                g.colls = new Il2CppReferenceArray<Collider>(colls.Length);
                for (int i = 0; i < colls.Length; i++) if (colls[i] != null) g.colls[i] = colls[i];
            }

            g.crusher = crusher;
            g.type    = isHat ? GrabableType.hat : GrabableType.questItem;

            if (isHat && g is Hat hat)
            {
                hat.enableOnWear  = new Il2CppReferenceArray<GameObject>(0);
                hat.disableOnWear = new Il2CppReferenceArray<GameObject>(0);
            }

            var ptsR  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsR = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            var ptsL  = new Il2CppSystem.Collections.Generic.List<Vector3>();
            var rotsL = new Il2CppSystem.Collections.Generic.List<Quaternion>();
            ptsR.Add(Vector3.zero);  rotsR.Add(Quaternion.identity);
            ptsL.Add(Vector3.zero);  rotsL.Add(Quaternion.identity);
            g.grabLocPtsR  = ptsR;  g.grabLocRotsR = rotsR;
            g.grabLocPtsL  = ptsL;  g.grabLocRotsL = rotsL;

            AddFloater(go);
        }

        // WaterObject.GenerateSimMesh hangs the main thread on high-poly meshes even with
        // simplifyMesh=true (the simplification step itself is the bottleneck). Cap at a safe
        // vertex count and fall back to a bounding-box mesh so the prop still floats correctly.
        const int FloaterVertexLimit = 5000;

        static void AddFloater(GameObject go)
        {
            if (go == null) return;

            var existing = go.transform.Find("Floater");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

            var sourceMf   = go.GetComponentInChildren<MeshFilter>();
            var sourceMesh = sourceMf?.sharedMesh;

            Mesh floaterMesh = null;
            if (sourceMesh != null)
            {
                if (sourceMesh.vertexCount <= FloaterVertexLimit)
                {
                    floaterMesh = BuildPhysicsMesh(sourceMesh);
                }
                else
                {
                    // Mesh too complex for GenerateSimMesh — use bounds box so the prop still floats.
                    floaterMesh = BuildBoundsMesh(sourceMesh.bounds);
                }
            }

            if (floaterMesh == null)
            {
                MelonLogger.Warning("[BabyBlocks] AddFloater: could not build floater mesh, skipping Floater.");
                return;
            }

            var floater = new GameObject("Floater");
            floater.SetActive(false);
            floater.transform.SetParent(go.transform, false);
            floater.layer = go.layer;

            var mf = floater.AddComponent<MeshFilter>();
            mf.sharedMesh = floaterMesh;

            var wo = floater.AddComponent<WaterObject>();
            wo.convexifyMesh         = true;
            wo.simplifyMesh          = true;
            wo.targetTriangleCount   = 16;
            wo.calculateWaterNormals = true;
            wo.GenerateSimMesh();

            floater.SetActive(true);
        }

        static Mesh BuildBoundsMesh(Bounds b)
        {
            var mesh = new Mesh();
            var c    = b.center;
            var e    = b.extents;
            mesh.vertices = new Vector3[]
            {
                new(c.x - e.x, c.y - e.y, c.z - e.z),
                new(c.x + e.x, c.y - e.y, c.z - e.z),
                new(c.x + e.x, c.y + e.y, c.z - e.z),
                new(c.x - e.x, c.y + e.y, c.z - e.z),
                new(c.x - e.x, c.y - e.y, c.z + e.z),
                new(c.x + e.x, c.y - e.y, c.z + e.z),
                new(c.x + e.x, c.y + e.y, c.z + e.z),
                new(c.x - e.x, c.y + e.y, c.z + e.z),
            };
            mesh.triangles = new int[]
            {
                0,2,1, 0,3,2,
                4,5,6, 4,6,7,
                0,1,5, 0,5,4,
                3,6,2, 3,7,6,
                0,4,7, 0,7,3,
                1,2,6, 1,6,5,
            };
            mesh.RecalculateNormals();
            return mesh;
        }

        internal static void RemoveGrabableComponents(GameObject go)
        {
            var hat = go.GetComponent<Hat>();
            if (hat != null) UnityEngine.Object.DestroyImmediate(hat);
            else
            {
                var grabable = go.GetComponent<Grabable>();
                if (grabable != null) UnityEngine.Object.DestroyImmediate(grabable);
            }
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) UnityEngine.Object.DestroyImmediate(rb);
            var crusher = go.transform.Find("Crusher");
            if (crusher != null) UnityEngine.Object.DestroyImmediate(crusher.gameObject);
            var floater = go.transform.Find("Floater");
            if (floater != null) UnityEngine.Object.DestroyImmediate(floater.gameObject);
        }
    }
}
