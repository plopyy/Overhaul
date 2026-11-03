using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Overhaul.Magic
{
    // Frozen creatures (spells with FreezeDuration, SE_Spell): no move, no attack, animation stopped, no damage of its own,
    // refreshed by a new hit instead of stacking. The creature stands in an ice block seen by every player: a network
    // object (Overhaul_FrozenRock1 tall for humanoids, Overhaul_FrozenRock2 long for the others, from overhaul_spellvfx)
    // spawned by the creature's owner, following it and sized to its body. FrozenRocks.cfg picks the block per prefab,
    // 0 = never frozen; creatures not listed take the tall block when their animation rig is humanoid.
    internal static class Freeze
    {
        internal const string FileName = "FrozenRocks.cfg";
        static Dictionary<string, int> rocks;

        static Dictionary<string, int> Rocks()
        {
            if (rocks != null) return rocks;
            rocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(Path.GetDirectoryName(typeof(Freeze).Assembly.Location), FileName);
            string text;
            try { text = File.Exists(path) ? File.ReadAllText(path) : Utility.ConfigSections.Defaults(FileName); }
            catch (Exception e) { Utility.Log.LogError("FrozenRocks.cfg unreadable: " + e.Message); return rocks; }
            foreach (var line in text.Split('\n'))
            {
                var parts = line.Split('#')[0].Split('=');
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out int kind)) rocks[parts[0].Trim()] = Mathf.Clamp(kind, 0, 2);
            }
            return rocks;
        }

        // 0 never frozen, 1 tall block, 2 long block.
        internal static int Rock(Character character)
        {
            if (!character || character.IsPlayer()) return 0;
            if (Rocks().TryGetValue(Utils.GetPrefabName(character.gameObject), out int kind)) return kind;
            var animator = character.GetComponentInChildren<Animator>();
            return animator && animator.avatar && animator.avatar.isHuman ? 1 : 2;
        }

        // The block around a character, spawned by its owner (the status effect's machine); null when it cannot be made.
        internal static GameObject Encase(Character character, int rock)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(rock == 1 ? "Overhaul_FrozenRock1" : "Overhaul_FrozenRock2") : null;
            if (!prefab) return null;
            var block = UnityEngine.Object.Instantiate(prefab, character.transform.position, character.transform.rotation);
            var view = block.GetComponent<ZNetView>();
            if (view && view.IsValid())
            {
                view.GetZDO().Set(FrozenBlock.TargetKey, character.GetZDOID());
                view.GetZDO().Set(FrozenBlock.SizeKey, BlockSize(character, rock, block.GetComponentInChildren<MeshFilter>()?.sharedMesh));
            }
            return block;
        }

        internal static void Release(GameObject block)
        {
            if (!block) return;
            var view = block.GetComponent<ZNetView>();
            if (view && view.IsValid() && view.IsOwner()) { view.Destroy(); return; }
            UnityEngine.Object.Destroy(block);
        }

        // Scale of the block mesh (4 m tall or long) around the body: width and depth by one factor (the block keeps its
        // footprint's proportions) 1.35 times the body, height 1.3 times the body's. Body = its main skinned mesh, measured
        // in the character's own axes.
        static Vector3 BlockSize(Character character, int rock, Mesh mesh)
        {
            var body = character.GetComponentsInChildren<SkinnedMeshRenderer>().Where(r => r.enabled && r.sharedMesh).OrderByDescending(r => r.sharedMesh.vertexCount).FirstOrDefault();
            Vector3 size = new Vector3(1, 2, 1);
            if (body)
            {
                var lb = body.localBounds; var min = Vector3.positiveInfinity; var max = Vector3.negativeInfinity;
                for (int i = 0; i < 8; i++)
                {
                    var corner = lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = character.transform.InverseTransformPoint(body.rootBone ? body.rootBone.TransformPoint(corner) : body.transform.TransformPoint(corner));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                }
                size = Vector3.Max(max - min, Vector3.one * .3f);
            }
            var rb = mesh ? mesh.bounds.size : new Vector3(2.3f, 4, 2.3f);
            float flat = Mathf.Max(size.x / rb.x, size.z / rb.z) * 1.35f;
            return new Vector3(flat, size.y * 1.3f / rb.y, flat);
        }
    }

    // An ice block (Overhaul_FrozenRock1/2): on every machine, sits on the character named in its ZDO, turned with it,
    // at the size its owner measured.
    internal sealed class FrozenBlock : MonoBehaviour
    {
        internal const string TargetKey = "overhaul_frozen_target";
        internal static readonly int SizeKey = "overhaul_frozen_size".GetStableHashCode();
        ZNetView view;
        GameObject target;
        bool sized;

        void Awake() => view = GetComponent<ZNetView>();

        void LateUpdate()
        {
            if (!view || !view.IsValid()) return;
            if (!sized) { var s = view.GetZDO().GetVec3(SizeKey, Vector3.zero); if (s != Vector3.zero) { transform.localScale = s; sized = true; } }
            if (!target && ZNetScene.instance) target = ZNetScene.instance.FindInstance(view.GetZDO().GetZDOID(TargetKey));
            if (target) transform.SetPositionAndRotation(target.transform.position, target.transform.rotation);
            else if (view.IsOwner() && sized) view.Destroy(); // the creature is gone (killed, unloaded)
        }
    }
}
