using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace BabyBlocks
{
    // F7: dump physics-relevant component hierarchy of the selected prop + any native
    //     Skateboard objects in the scene. Skips pure render components (MeshFilter,
    //     MeshRenderer, LODGroup, SkinnedMeshRenderer) to keep output manageable.
    internal static class PropHierarchyDiag
    {
        const string Tag = "[PROP-DIAG]";

        internal static void Dump()
        {
            bool dumpedAny = false;

            var sel = LevelEditor.SelectedObjects;
            if (sel != null && sel.Count > 0)
            {
                foreach (var leo in sel)
                {
                    if (leo == null) continue;
                    MelonLogger.Msg($"{Tag} ===== CUSTOM: {leo.gameObject.name} key={leo.addressableKey} mode={leo.physicsMode} =====");
                    DumpHierarchy(leo.transform.root, 0);
                    dumpedAny = true;
                }
            }

            // Native: find objects with a Skateboard component (the wagon's controller)
            // rather than all Rigidbodies, which sweeps up every tree in the scene.
            var container = LevelEditorManager.PropsContainer;
            var player    = PlayerMovement.me;
            var allSkateboards = UnityEngine.Object.FindObjectsOfType<Skateboard>();
            for (int i = 0; i < allSkateboards.Length; i++)
            {
                var sk = allSkateboards[i];
                if (sk == null) continue;
                if (container != null && sk.transform.IsChildOf(container.transform)) continue;
                if (player   != null && sk.transform.IsChildOf(player.transform)) continue;
                var root = sk.transform.root;
                MelonLogger.Msg($"{Tag} ===== NATIVE Skateboard: {GetPath(sk.transform)} =====");
                DumpHierarchy(root, 0);
                dumpedAny = true;
            }

            if (!dumpedAny)
                MelonLogger.Msg($"{Tag} Nothing to dump — select a prop, or be near a native wagon.");
        }

        static void DumpHierarchy(Transform t, int depth)
        {
            if (t == null) return;
            string pad = new string(' ', depth * 2);
            var go = t.gameObject;
            MelonLogger.Msg($"{Tag}{pad}[{(go.activeSelf ? "+" : "-")}] \"{t.name}\" layer={go.layer}");

            var comps = t.GetComponents<Component>();
            for (int i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null || c is Transform) continue;
                DumpComponent(c, pad + "  ");
            }

            for (int i = 0; i < t.childCount; i++)
                DumpHierarchy(t.GetChild(i), depth + 1);
        }

        static void DumpComponent(Component c, string pad)
        {
            string typeName = "(unknown)";
            try { typeName = c.GetIl2CppType()?.Name ?? c.GetType().Name; } catch { }

            try { DumpComponentInner(c, pad, typeName); }
            catch (Exception) { MelonLogger.Msg($"{Tag}{pad}{typeName}"); }
        }

        static void DumpComponentInner(Component c, string pad, string typeName)
        {
            var rb = c.TryCast<Rigidbody>();
            if (rb != null)
            {
                MelonLogger.Msg($"{Tag}{pad}Rigidbody  mass={rb.mass:F2} kinematic={rb.isKinematic} gravity={rb.useGravity}" +
                    $" drag={rb.drag:F3} angDrag={rb.angularDrag:F3} constraints={rb.constraints}" +
                    $" vel={Fmt(rb.velocity)} angVel={Fmt(rb.angularVelocity)} sleeping={rb.IsSleeping()}");
                return;
            }

            var mc = c.TryCast<MeshCollider>();
            if (mc != null)
            {
                MelonLogger.Msg($"{Tag}{pad}MeshCollider  enabled={mc.enabled} trigger={mc.isTrigger} convex={mc.convex} mesh={(mc.sharedMesh != null ? mc.sharedMesh.name : "null")}");
                return;
            }

            var bc = c.TryCast<BoxCollider>();
            if (bc != null)
            {
                MelonLogger.Msg($"{Tag}{pad}BoxCollider  enabled={bc.enabled} trigger={bc.isTrigger} center={Fmt(bc.center)} size={Fmt(bc.size)}");
                return;
            }

            var sph = c.TryCast<SphereCollider>();
            if (sph != null)
            {
                MelonLogger.Msg($"{Tag}{pad}SphereCollider  enabled={sph.enabled} trigger={sph.isTrigger} r={sph.radius:F3}");
                return;
            }

            var cap = c.TryCast<CapsuleCollider>();
            if (cap != null)
            {
                MelonLogger.Msg($"{Tag}{pad}CapsuleCollider  enabled={cap.enabled} trigger={cap.isTrigger} r={cap.radius:F3} h={cap.height:F3} dir={cap.direction}");
                return;
            }

            var joint = c.TryCast<Joint>();
            if (joint != null)
            {
                string connected = joint.connectedBody != null ? joint.connectedBody.name : "null";
                MelonLogger.Msg($"{Tag}{pad}{typeName}  connectedBody={connected} breakForce={joint.breakForce:F1} breakTorque={joint.breakTorque:F1}");
                var hj = c.TryCast<HingeJoint>();
                if (hj != null)
                {
                    MelonLogger.Msg($"{Tag}{pad}  axis={Fmt(hj.axis)} useMotor={hj.useMotor} useSpring={hj.useSpring} useLimits={hj.useLimits}");
                    if (hj.useLimits) { var lim = hj.limits; MelonLogger.Msg($"{Tag}{pad}  limits min={lim.min:F1} max={lim.max:F1}"); }
                }
                var cj = c.TryCast<ConfigurableJoint>();
                if (cj != null)
                    MelonLogger.Msg($"{Tag}{pad}  xMotion={cj.xMotion} yMotion={cj.yMotion} zMotion={cj.zMotion} angX={cj.angularXMotion} angY={cj.angularYMotion} angZ={cj.angularZMotion}");
                var sj = c.TryCast<SpringJoint>();
                if (sj != null)
                    MelonLogger.Msg($"{Tag}{pad}  spring={sj.spring:F1} damper={sj.damper:F1} minDist={sj.minDistance:F3} maxDist={sj.maxDistance:F3}");
                return;
            }

            // Skip pure render components — not useful for physics diagnosis
            if (c.TryCast<MeshRenderer>() != null) return;
            if (c.TryCast<MeshFilter>()   != null) return;
            if (c.TryCast<LODGroup>()     != null) return;

            // Everything else: just the type name (scripts, audio, etc.)
            MelonLogger.Msg($"{Tag}{pad}{typeName}");
        }

        static string GetPath(Transform t)
        {
            if (t == null) return "(null)";
            if (t.parent == null) return t.name;
            return GetPath(t.parent) + "/" + t.name;
        }

        static string Fmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
    }
}
