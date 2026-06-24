using System.Collections.Generic;
using System.Linq;
using Il2Cpp;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace BabyBlocks
{
    // Group management: group-root creation/dissolution and the bulk
    // activate/deactivate/freeze transitions for groups of Rigidbody/Hat/Grabable props.
    internal static class GroupManager
    {
        public static int AllocateGroupId() => LevelEditorManager.Instance._nextGroupId++;

        // Group-root helpers
        internal static GameObject GetGroupRoot(int groupId)
        {
            if (groupId <= 0) return null;
            LevelEditorManager.Instance._groupRoots.TryGetValue(groupId, out var r);
            return r != null ? r : null;
        }

        static void SetGroupRoot(int groupId, GameObject root)
        {
            if (groupId > 0 && root != null) LevelEditorManager.Instance._groupRoots[groupId] = root;
        }

        static void RemoveGroupRoot(int groupId) => LevelEditorManager.Instance._groupRoots.Remove(groupId);

        // Display scale: the "virtual" group scale shown in the Properties Panel text boxes.
        // The group root GO's localScale is always (1,1,1) to avoid Unity's shear artifact
        // (non-uniform parent scale + rotated children = implicit skew). Scale is instead
        // applied directly to each member's localScale and localPosition.
        static readonly Dictionary<int, Vector3> _groupDisplayScales = new();

        public static Vector3 GetGroupDisplayScale(int groupId)
        {
            if (groupId > 0 && _groupDisplayScales.TryGetValue(groupId, out var s)) return s;
            return Vector3.one;
        }

        public static void SetGroupDisplayScale(int groupId, Vector3 scale) =>
            _groupDisplayScales[groupId] = scale;

        public static void RemoveGroupDisplayScale(int groupId) =>
            _groupDisplayScales.Remove(groupId);

        public static IEnumerable<KeyValuePair<int, Vector3>> GetAllGroupDisplayScales() =>
            _groupDisplayScales;

        internal static void FreezeRigidBodyGroupForEditor(int physicsGroupId)
        {
            var mgr = LevelEditorManager.Instance;
            var members = mgr.Objects.Where(o => o != null && o.physicsMode == PhysicsMode.Rigidbody && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;

            var root = FindGroupRoot(members) ?? ActivateRigidbodyGroup(members);
            if (root == null) return;

            var centroid = Vector3.zero;
            foreach (var m in members)
                centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            PhysicsObjectManager.SetHierarchyCollisions(root, false);
            root.transform.position = centroid;
            foreach (var m in members) PhysicsObjectManager.RestoreBasePose(m);
            PhysicsObjectManager.FreezeRigidBodyGameObject(root, true);
            PhysicsObjectManager.SetHierarchyCollisions(root, true);
            foreach (var m in members) m.isPhysicsManaged = true;
        }

        internal static void UnfreezeRigidBodyGroup(int physicsGroupId)
        {
            var mgr = LevelEditorManager.Instance;
            var members = mgr.Objects.Where(o => o != null && o.physicsMode == PhysicsMode.Rigidbody && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;
            var root = FindGroupRoot(members);
            if (root == null) return;
            PhysicsObjectManager.FreezeRigidBodyGameObject(root, false);
            foreach (var m in members) m.isPhysicsManaged = true;
        }

        internal static void CleanupGroupRoot(int groupId)
        {
            if (groupId <= 0) return;
            var root = GetGroupRoot(groupId);
            if (root == null) { RemoveGroupRoot(groupId); return; }
            for (int i = 0; i < root.transform.childCount; i++)
                if (root.transform.GetChild(i).GetComponent<LevelEditorObject>() != null) return;
            RemoveGroupRoot(groupId);
            UnityEngine.Object.Destroy(root);
        }

        [HideFromIl2Cpp]
        static GameObject FindGroupRoot(List<LevelEditorObject> members)
        {
            if (members == null || members.Count == 0) return null;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;
            return GetGroupRoot(gid);
        }

        internal static void DeactivateGroupForEditor(int physicsGroupId)
        {
            var mgr = LevelEditorManager.Instance;
            var members = mgr.Objects.Where(o => o != null && o.physicsGroupId == physicsGroupId).ToList();
            if (members.Count == 0) return;

            var root = GetGroupRoot(physicsGroupId);
            if (root != null)
            {
                PhysicsObjectManager.SetHierarchyCollisions(root, false);
                var propsContainer = LevelEditorManager.PropsContainer;
                while (root.transform.childCount > 0)
                {
                    var child = root.transform.GetChild(0);
                    child.SetParent(propsContainer != null ? propsContainer.transform : null, true);
                    PhysicsObjectManager.SetHierarchyCollisions(child.gameObject, true);
                    var childLeo = child.GetComponent<LevelEditorObject>();
                    if (childLeo != null) { PhysicsObjectManager.RestoreBasePose(childLeo); childLeo.isPhysicsManaged = false; }
                }
                RemoveGroupRoot(physicsGroupId);
                UnityEngine.Object.Destroy(root);
            }
            else
            {
                foreach (var member in members)
                {
                    PhysicsObjectManager.RemoveGrabableComponents(member.gameObject);
                    PhysicsObjectManager.RestoreBasePose(member);
                    member.isPhysicsManaged = false;
                }
            }

            // Recreate the static logical group root so GetGroupRoot(groupId) stays valid
            // for the preview system while in editor mode.
            foreach (var grp in members.Where(m => m != null && m.groupId > 0).GroupBy(m => m.groupId))
                EnsureStaticGroupRoot(grp.Key, grp.ToList());
        }

        public static void ApplyGroups()
        {
            var mgr = LevelEditorManager.Instance;
            var rigidbodySolos   = new List<LevelEditorObject>();
            var rigidbodyGroups  = new Dictionary<int, List<LevelEditorObject>>();
            var wearableSolos    = new List<LevelEditorObject>();
            var wearableGroups   = new Dictionary<int, List<LevelEditorObject>>();
            int maxGroupId = 0;

            foreach (var leo in mgr.Objects)
            {
                if (leo == null || leo.physicsMode == PhysicsMode.Static) continue;
                if (leo.groupId       > maxGroupId) maxGroupId = leo.groupId;
                if (leo.physicsGroupId > maxGroupId) maxGroupId = leo.physicsGroupId;

                if (leo.physicsMode == PhysicsMode.Rigidbody)
                {
                    if (leo.physicsGroupId <= 0) { rigidbodySolos.Add(leo); }
                    else
                    {
                        if (!rigidbodyGroups.TryGetValue(leo.physicsGroupId, out var list)) { list = new List<LevelEditorObject>(); rigidbodyGroups[leo.physicsGroupId] = list; }
                        list.Add(leo);
                    }
                    continue;
                }

                if (leo.physicsGroupId <= 0) { wearableSolos.Add(leo); }
                else
                {
                    if (!wearableGroups.TryGetValue(leo.physicsGroupId, out var list)) { list = new List<LevelEditorObject>(); wearableGroups[leo.physicsGroupId] = list; }
                    list.Add(leo);
                }
            }

            foreach (var leo in rigidbodySolos)
            {
                var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
                PhysicsObjectManager.AddRigidBodyComponent(leo.gameObject, colls, leo.addressableKey);
                PhysicsObjectManager.FreezeRigidBodyObject(leo, mgr._editorModeActive);
                PhysicsObjectManager.MarkPhysicsChunkIndependent(leo);
                leo.isPhysicsManaged = true;
            }

            foreach (var kvp in rigidbodyGroups)
            {
                var members = kvp.Value;
                if (members.Count == 0) continue;
                var root = FindGroupRoot(members) ?? ActivateRigidbodyGroup(members);
                if (root != null) PhysicsObjectManager.FreezeRigidBodyGameObject(root, mgr._editorModeActive);
                foreach (var m in members) { PhysicsObjectManager.MarkPhysicsChunkIndependent(m); m.isPhysicsManaged = true; }
            }

            foreach (var leo in wearableSolos)
            {
                var colls = leo.gameObject.GetComponentsInChildren<Collider>(true);
                PhysicsObjectManager.AddGrabableComponent(leo.gameObject, leo.physicsMode == PhysicsMode.Hat, colls, leo.addressableKey);
                PhysicsObjectManager.SyncHatHairAmount(leo);
                if (leo.physicsMode == PhysicsMode.Grabable || leo.physicsMode == PhysicsMode.Hat) PhysicsObjectManager.SyncGrabOffset(leo);
                PhysicsObjectManager.MarkPhysicsChunkIndependent(leo);
                leo.isPhysicsManaged = true;
            }

            foreach (var kvp in wearableGroups)
            {
                var members = kvp.Value;
                if (members.Count == 0) continue;
                ActivateWearableGroup(members, members[0].physicsMode);
            }

            if (maxGroupId >= mgr._nextGroupId) mgr._nextGroupId = maxGroupId + 1;
        }

        [HideFromIl2Cpp]
        internal static GameObject ActivateRigidbodyGroup(List<LevelEditorObject> members)
        {
            if (members == null || members.Count == 0) return null;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;
            var propsContainer = LevelEditorManager.PropsContainer;

            var existingRoot = FindGroupRoot(members);
            if (existingRoot != null)
            {
                existingRoot.name = "PhysicsGroup";
                PhysicsObjectManager.RemoveGrabableComponents(existingRoot);
                var colls2 = new List<Collider>();
                foreach (var m in members)
                {
                    var p = m.transform.parent?.gameObject;
                    if (p == null || p.GetInstanceID() != existingRoot.GetInstanceID())
                        m.transform.SetParent(existingRoot.transform, true);
                    foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls2.Add(c);
                    m.isPhysicsManaged = true;
                }
                PhysicsObjectManager.AddRigidBodyComponent(existingRoot, colls2.ToArray());
                PhysicsObjectManager.FreezeRigidBodyGameObject(existingRoot, LevelEditorManager.Instance._editorModeActive);
                foreach (var m in members) PhysicsObjectManager.MarkPhysicsChunkIndependent(m);
                return existingRoot;
            }

            var centroid = Vector3.zero;
            foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            var root = new GameObject("PhysicsGroup");
            root.transform.position = centroid;
            if (propsContainer != null) root.transform.SetParent(propsContainer.transform, true);

            var colls = new List<Collider>();
            foreach (var m in members)
            {
                foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls.Add(c);
                m.gameObject.transform.SetParent(root.transform, true);
                m.isPhysicsManaged = true;
            }
            PhysicsObjectManager.AddRigidBodyComponent(root, colls.ToArray());
            PhysicsObjectManager.FreezeRigidBodyGameObject(root, LevelEditorManager.Instance._editorModeActive);
            foreach (var m in members) PhysicsObjectManager.MarkPhysicsChunkIndependent(m);
            if (gid > 0) SetGroupRoot(gid, root);
            return root;
        }

        [HideFromIl2Cpp]
        internal static void ActivateWearableGroup(List<LevelEditorObject> members, PhysicsMode mode)
        {
            if (members == null || members.Count == 0) return;
            int gid = members[0].physicsGroupId > 0 ? members[0].physicsGroupId : members[0].groupId;
            var propsContainer = LevelEditorManager.PropsContainer;

            var existingRoot = FindGroupRoot(members);
            if (existingRoot != null)
            {
                existingRoot.name = "PhysicsGroup";
                PhysicsObjectManager.RemoveGrabableComponents(existingRoot);
                var colls2 = new List<Collider>();
                foreach (var m in members)
                {
                    var p = m.transform.parent?.gameObject;
                    if (p == null || p.GetInstanceID() != existingRoot.GetInstanceID())
                        m.transform.SetParent(existingRoot.transform, true);
                    foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls2.Add(c);
                    m.isPhysicsManaged = true;
                }
                PhysicsObjectManager.AddGrabableComponent(existingRoot, mode == PhysicsMode.Hat, colls2.ToArray());
                if (mode == PhysicsMode.Hat)
                {
                    var hat = existingRoot.GetComponent<Hat>();
                    if (hat != null)
                    {
                        foreach (var m in members)
                            if (m != null && BbHatSunglassesFlag.Has(m)) { hat.isSunglasses = true; break; }
                    }
                }
                if (mode == PhysicsMode.Hat && members.Count > 0) PhysicsObjectManager.SyncHatHairAmount(members[0]);
                if ((mode == PhysicsMode.Grabable || mode == PhysicsMode.Hat) && members.Count > 0) PhysicsObjectManager.SyncGrabOffset(members[0]);
                foreach (var m in members) PhysicsObjectManager.MarkPhysicsChunkIndependent(m);
                return;
            }

            var centroid = Vector3.zero;
            foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
            centroid /= members.Count;

            var root = new GameObject("PhysicsGroup");
            root.transform.position = centroid;
            if (propsContainer != null) root.transform.SetParent(propsContainer.transform, true);

            var colls = new List<Collider>();
            foreach (var m in members)
            {
                foreach (var c in m.gameObject.GetComponentsInChildren<Collider>(true)) colls.Add(c);
                m.gameObject.transform.SetParent(root.transform, true);
                m.isPhysicsManaged = true;
            }
            PhysicsObjectManager.AddGrabableComponent(root, mode == PhysicsMode.Hat, colls.ToArray());
            if (mode == PhysicsMode.Hat)
            {
                var hat = root.GetComponent<Hat>();
                if (hat != null)
                {
                    foreach (var m in members)
                        if (m != null && BbHatSunglassesFlag.Has(m)) { hat.isSunglasses = true; break; }
                }
            }
            if (mode == PhysicsMode.Hat && members.Count > 0) PhysicsObjectManager.SyncHatHairAmount(members[0]);
            if (mode == PhysicsMode.Grabable && members.Count > 0) PhysicsObjectManager.SyncGrabOffset(members[0]);
            foreach (var m in members) PhysicsObjectManager.MarkPhysicsChunkIndependent(m);
            if (gid > 0) SetGroupRoot(gid, root);
        }

        [HideFromIl2Cpp]
        internal static void DissolveGroup(int groupId)
        {
            if (groupId <= 0) return;
            var mgr = LevelEditorManager.Instance;
            var root = GetGroupRoot(groupId);
            if (root != null)
            {
                PhysicsObjectManager.RemoveGrabableComponents(root);
                var propsContainer = LevelEditorManager.PropsContainer;
                while (root.transform.childCount > 0)
                {
                    var child = root.transform.GetChild(0);
                    child.SetParent(propsContainer != null ? propsContainer.transform : null, true);
                    var childLeo = child.GetComponent<LevelEditorObject>();
                    if (childLeo != null)
                    {
                        if (childLeo.physicsMode != PhysicsMode.Static)
                            MaterialBaker.RestoreOriginal(childLeo.gameObject);
                        mgr.SyncLoopBase(childLeo);
                        childLeo.physicsMode      = PhysicsMode.Static;
                        childLeo.physicsGroupId   = 0;
                        childLeo.groupId          = 0;
                        childLeo.isPhysicsManaged = false;
                    }
                }
                RemoveGroupRoot(groupId);
                RemoveGroupDisplayScale(groupId);
                UnityEngine.Object.Destroy(root);
            }
            else
            {
                foreach (var obj in mgr.Objects)
                {
                    if (obj == null || obj.groupId != groupId) continue;
                    if (obj.physicsMode != PhysicsMode.Static)
                        MaterialBaker.RestoreOriginal(obj.gameObject);
                    PhysicsObjectManager.RemoveGrabableComponents(obj.gameObject);
                    obj.physicsMode      = PhysicsMode.Static;
                    obj.physicsGroupId   = 0;
                    obj.groupId          = 0;
                    obj.isPhysicsManaged = false;
                }
                RemoveGroupDisplayScale(groupId);
            }
        }

        [HideFromIl2Cpp]
        internal static void EnsureStaticGroupRoot(int groupId, List<LevelEditorObject> members)
        {
            if (groupId <= 0 || members == null || members.Count == 0) return;
            var root = GetGroupRoot(groupId);
            if (root == null)
            {
                var centroid = Vector3.zero;
                foreach (var m in members) centroid += m.hasLoopBasePosition ? m.loopBasePosition : m.transform.position;
                centroid /= members.Count;
                root = new GameObject("Group");
                root.transform.position = centroid;
                var propsContainer = LevelEditorManager.PropsContainer;
                if (propsContainer != null) root.transform.SetParent(propsContainer.transform, true);
                SetGroupRoot(groupId, root);
            }
            foreach (var m in members)
            {
                if (m == null) continue;
                var p = m.transform.parent?.gameObject;
                if (p == null || p.GetInstanceID() != root.GetInstanceID())
                    m.transform.SetParent(root.transform, true);
            }
        }
    }
}
