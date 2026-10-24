using System.Collections.Generic;
using System.Linq;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Rarity
{
    // The mythic enchantments' visual effects: the prefabs of overhaul_mythicvfx (OverhaulLab MythicVfxBundle). Those that
    // are network objects (removing themselves) are registered with the game, so a copy spawned by its owner shows for
    // every player; the others (Mythic_Souls) are shown by each machine on its own, attached to what they follow.
    internal static class MythicVfx
    {
        static AssetBundle bundle;
        static readonly Dictionary<string, GameObject> local = new Dictionary<string, GameObject>();

        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_mythicvfx");
            if (!bundle) { Utility.Log.LogError("Mythic effects: overhaul_mythicvfx missing"); return; }
            foreach (var prefab in bundle.LoadAllAssets<GameObject>())
            {
                if (!prefab.GetComponent<ZNetView>()) { local[prefab.name] = prefab; continue; }
                // The spectral weapon builds its look from the item named in its ZDO, on every machine.
                if (prefab.name == "Mythic_SpectralWeapon" && !prefab.GetComponent<SpectralCopy>()) prefab.AddComponent<SpectralCopy>();
                // The level up follows its player on every machine.
                if (prefab.name == "Overhaul_LevelUp" && !prefab.GetComponent<FollowCharacter>()) prefab.AddComponent<FollowCharacter>();
                if (!PrefabManager.Instance.GetPrefab(prefab.name)) PrefabManager.Instance.AddPrefab(new CustomPrefab(prefab, false));
            }
        }

        // A copy of the effect at position, facing rotation; it removes itself once played.
        internal static GameObject Spawn(string name, Vector3 position, Quaternion rotation)
        {
            var prefab = ZNetScene.instance ? ZNetScene.instance.GetPrefab(name) : null;
            if (!prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            var copy = Object.Instantiate(prefab, position, rotation);
            // Its sounds play in the game's sound effects volume.
            var sfx = AudioMan.instance ? AudioMan.instance.m_masterMixer.FindMatchingGroups("SFX").FirstOrDefault() : null;
            if (sfx) foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true)) audio.outputAudioMixerGroup = sfx;
            return copy;
        }

        // A spectral copy of an item (its prefab name), owned by this machine: Mythic_SpectralWeapon with the item in its ZDO;
        // It pops up in a blue flash (Mythic_SpectralPop, Mythic_DaggerPop when small). trailWidth: a trail that wide at its middle instead of one along
        // the blade (for a weapon flying straight); 0: along the blade. scale: the copy's size; spin: degrees per second the model turns about its own up axis, the copy and its
        // trail keeping their heading; all seen by every player.
        internal static GameObject SpawnSpectral(string item, Vector3 position, Quaternion rotation, float trailWidth = 0, float scale = 1, bool small = false, float spin = 0)
        {
            Spawn(small ? "Mythic_DaggerPop" : "Mythic_SpectralPop", position, rotation);
            var copy = Spawn("Mythic_SpectralWeapon", position, rotation);
            var view = copy ? copy.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) { view.GetZDO().Set(SpectralCopy.ItemKey, item); view.GetZDO().Set(SpectralCopy.NarrowKey, trailWidth); view.GetZDO().Set(SpectralCopy.ScaleKey, scale); view.GetZDO().Set(SpectralCopy.SpinKey, spin); }
            return copy;
        }

        // A spectral weapon's hit: a blue burst (Mythic_SpectralHit, Mythic_DaggerHit when small) at point, facing back along dir.
        internal static void SpectralHit(Vector3 point, Vector3 dir, bool small = false) =>
            Spawn(small ? "Mythic_DaggerHit" : "Mythic_SpectralHit", point, dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(-dir) : Quaternion.identity);

        // The level up around the local player, seen by every player and following them.
        internal static void LevelUp(Player player)
        {
            var copy = Spawn("Overhaul_LevelUp", player.transform.position, player.transform.rotation);
            var view = copy ? copy.GetComponent<ZNetView>() : null;
            if (view && view.IsValid()) view.GetZDO().Set(FollowCharacter.Key, player.GetZDOID());
        }

        // A local (not networked) effect played once at position, removed after life seconds.
        internal static void Play(string name, Vector3 position, float life)
        {
            if (!local.TryGetValue(name, out var prefab) || !prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return; }
            Object.Destroy(Object.Instantiate(prefab, position, Quaternion.identity), life);
        }

        // A copy of a local (not networked) effect following parent, until destroyed.
        internal static GameObject Attach(string name, Transform parent)
        {
            if (!local.TryGetValue(name, out var prefab) || !prefab) { Utility.Log.LogWarning("Mythic effect " + name + " not found"); return null; }
            return Object.Instantiate(prefab, parent, false);
        }
    }

    // Look of Mythic_SpectralWeapon: the meshes of the held model ("attach") of the item named in its ZDO, placed as in the
    // model, on the spectral material of the prefab's Model slot.
    internal sealed class SpectralCopy : MonoBehaviour
    {
        internal static readonly int ItemKey = "overhaul_spectral_item".GetStableHashCode(), NarrowKey = "overhaul_spectral_narrow".GetStableHashCode(),
            ScaleKey = "overhaul_spectral_scale".GetStableHashCode(), SpinKey = "overhaul_spectral_spin".GetStableHashCode();
        bool built;
        Transform spinning;
        Quaternion rest;
        float spin, angle;

        void Update()
        {
            if (spinning)
            {
                angle += spin * Time.deltaTime;
                spinning.localRotation = rest * Quaternion.Euler(0, angle, 0);
            }
            if (built) return;
            var view = GetComponent<ZNetView>();
            string name = view && view.IsValid() ? view.GetZDO().GetString(ItemKey, "") : "";
            var item = name.Length > 0 && ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(name) : null;
            if (!item) return;
            built = true;
            var slot = transform.Find("Model");
            var material = slot ? slot.GetComponent<MeshRenderer>()?.sharedMaterial : null;
            if (!material) return;
            slot.localScale *= view.GetZDO().GetFloat(ScaleKey, 1);
            var model = item.transform.Find("attach") ?? item.transform;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!mf.sharedMesh || !mf.GetComponent<MeshRenderer>()) continue;
                var part = new GameObject(mf.name); part.transform.SetParent(slot, false);
                var m = model.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                part.transform.localPosition = m.GetColumn(3); part.transform.localRotation = m.rotation; part.transform.localScale = m.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var renderer = part.AddComponent<MeshRenderer>(); renderer.sharedMaterials = Enumerable.Repeat(material, mf.sharedMesh.subMeshCount).ToArray();
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                part.layer = gameObject.layer;
            }
            float width = view.GetZDO().GetFloat(NarrowKey, 0);
            if (width > 0) Narrow(transform, slot, width); else PlaceTrail(transform, slot, model);
            spin = view.GetZDO().GetFloat(SpinKey, 0);
            if (spin != 0) { spinning = slot; rest = slot.localRotation; }
        }

        // A trail of the given width at the middle of the copy.
        internal static void Narrow(Transform root, Transform slot, float width)
        {
            var trail = root.Find("Trail")?.GetComponent<TrailRenderer>();
            if (!trail) return;
            trail.transform.position = slot.position; trail.widthMultiplier = width; trail.Clear();
        }

        // The trail along the blade: at the middle of the weapon's own trail marks (equiped/trail/base and tip), as wide as
        // the blade between them; kept where it is when the weapon has no marks.
        internal static void PlaceTrail(Transform root, Transform slot, Transform model)
        {
            var trail = root.Find("Trail")?.GetComponent<TrailRenderer>();
            var from = model.Find("equiped/trail/base"); var to = model.Find("equiped/trail/tip");
            if (!trail || !from || !to) return;
            Vector3 a = slot.TransformPoint(model.InverseTransformPoint(from.position)), b = slot.TransformPoint(model.InverseTransformPoint(to.position));
            trail.transform.position = (a + b) / 2;
            trail.widthMultiplier = Vector3.Distance(a, b);
            trail.Clear();
        }
    }
}

    // An effect staying at the feet of the character named in its ZDO (Overhaul_LevelUp), on every machine.
    internal sealed class FollowCharacter : MonoBehaviour
    {
        internal const string Key = "overhaul_follow";
        ZNetView view;
        GameObject target;

        void Awake() => view = GetComponent<ZNetView>();

        void LateUpdate()
        {
            if (!target && view && view.IsValid() && ZNetScene.instance) target = ZNetScene.instance.FindInstance(view.GetZDO().GetZDOID(Key));
            if (target) transform.position = target.transform.position;
        }
    }
