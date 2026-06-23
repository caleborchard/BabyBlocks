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

            bool active = (control.transform.position - playerPos).sqrMagnitude <= PhysicsActiveRadiusSqr;
            if (active)
            {
                if (rb.isKinematic) rb.isKinematic = false;
                rb.useGravity = true;
            }
            else if (!rb.isKinematic)
            {
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
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

                // Undo any baked ghost-cube collider carves so moving/selecting
                // props in the editor isn't affected by holes cut for gameplay.
                GhostCollisionCutter.RestoreAllColliderCarves();
            }
            else
            {
                ExitEditorPhysicsMode();

                // Bake collider carves for every ghost cube now that editing is
                // done — this is the only point props' MeshColliders get cut, so
                // anything moved into a hole while editing is picked up here.
                GhostCollisionCutter.BakeAllColliderCarves();
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
                        RestoreBasePose(obj);
                        FreezeRigidBodyObject(obj, true);
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
                    FreezeRigidBodyObject(obj, false);
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
                if (!obj.editorFreezeStateValid)
                {
                    obj.editorFreezeVelocity        = Vector3.zero;
                    obj.editorFreezeAngularVelocity  = Vector3.zero;
                    obj.editorFreezeIsKinematic      = rb.isKinematic;
                    obj.editorFreezeUseGravity       = rb.useGravity;
                    obj.editorFreezeConstraints      = rb.constraints;
                    obj.editorFreezeStateValid       = true;
                }
                rb.velocity        = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic     = true;
                rb.useGravity      = false;
                rb.constraints     = RigidbodyConstraints.FreezeAll;
            }
            else if (obj.editorFreezeStateValid)
            {
                rb.constraints     = obj.editorFreezeConstraints;
                rb.isKinematic     = obj.editorFreezeIsKinematic;
                rb.useGravity      = obj.editorFreezeUseGravity;
                rb.velocity        = obj.editorFreezeVelocity;
                rb.angularVelocity = obj.editorFreezeAngularVelocity;
                obj.editorFreezeStateValid = false;
            }
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
            }
            else
            {
                rb.constraints = RigidbodyConstraints.None;
                rb.isKinematic = false;
                rb.useGravity  = true;
            }
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
                if (leo.physicsMode == PhysicsMode.Grabable) SyncGrabOffset(leo);
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

        internal static void AddRigidBodyComponent(GameObject go, Collider[] colls, string propId = null)
        {
            // MaterialBaker.Bake(go, propId); // disabled for now
            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true)) mc.convex = true;
            var rb = go.GetComponent<Rigidbody>() ?? go.AddComponent<Rigidbody>();
            rb.mass                   = 5f;
            rb.isKinematic            = false;
            rb.useGravity             = true;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation          = RigidbodyInterpolation.Interpolate;
        }

        internal static void AddGrabableComponent(GameObject go, bool isHat, Collider[] colls, string propId = null)
        {
            if (go == null) return;
            // MaterialBaker.Bake(go, propId); // disabled for now
            if (colls == null) colls = Array.Empty<Collider>();

            foreach (var mc in go.GetComponentsInChildren<MeshCollider>(true)) mc.convex = true;

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

            g.rb    = rb;
            g.rbs   = new Il2CppReferenceArray<Rigidbody>(1); g.rbs[0] = rb;
            g.colls = new Il2CppReferenceArray<Collider>(colls.Length);
            for (int i = 0; i < colls.Length; i++) if (colls[i] != null) g.colls[i] = colls[i];
            g.crusher = crusher;
            g.type    = isHat ? GrabableType.hat : GrabableType.questItem;

            if (isHat && g is Hat hat)
            {
                hat.enableOnWear  = Array.Empty<GameObject>();
                hat.disableOnWear = Array.Empty<GameObject>();
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

        static void AddFloater(GameObject go)
        {
            if (go == null) return;

            var existing = go.transform.Find("Floater");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing.gameObject);

            Mesh readableMesh = null;
            var sourceMf = go.GetComponentInChildren<MeshFilter>();
            if (sourceMf?.sharedMesh != null)
                readableMesh = BuildPhysicsMesh(sourceMf.sharedMesh);

            if (readableMesh == null)
            {
                MelonLogger.Warning("[BabyBlocks] AddFloater: could not build readable mesh, skipping Floater.");
                return;
            }

            var floater = new GameObject("Floater");
            floater.SetActive(false);
            floater.transform.SetParent(go.transform, false);
            floater.layer = go.layer;

            var mf = floater.AddComponent<MeshFilter>();
            mf.sharedMesh = readableMesh;

            var wo = floater.AddComponent<WaterObject>();
            wo.convexifyMesh         = true;
            wo.simplifyMesh          = true;
            wo.targetTriangleCount   = 16;
            wo.calculateWaterNormals = true;
            wo.GenerateSimMesh();

            floater.SetActive(true);
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
