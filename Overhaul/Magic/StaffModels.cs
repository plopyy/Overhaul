using System.Collections.Generic;
using System.Linq;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using Overhaul.Utility;
using UnityEngine;

namespace Overhaul.Magic
{
    // Overhaul staffs (Design/SystemeMagie.md): one per biome from the Black Forest on, each a new item cloned from a
    // game staff (the game staffs are left as they are), crafted from a wood, the biome metal and a biome ingredient.
    // A staff with a Model gets it from the overhaul_staffs bundle (<Model>_Model prefabs), its pieces on the game's
    // own materials (a copy with a new diffuse when the model brings one), and the base staff's effects swapped for a
    // game object's; the others keep the base staff's model until theirs is made. Damage is replaced by the staff's
    // magic power (StaffMagic); attacks stay the base staff's until the grimoire spells are cast.
    internal static class StaffModels
    {
        internal sealed class Staff
        {
            public string Prefab, Base, Model, Name, Station;
            // Magic power at quality 1 and per upgrade level.
            public float Power, PowerPerLevel;
            // Model piece name -> (game prefab holding the material, its material name, diffuse texture of the bundle or null).
            public Dictionary<string, (string prefab, string material, string diffuse)> Materials;
            // Effects: the base staff's are removed and those around a game model are copied around one of ours (piece, game prefab).
            public (string piece, string prefab) Effects;
            // Material values changed on a piece's material (a copy): piece -> (property, value).
            public Dictionary<string, (string property, float value)[]> Tweaks;
            // A game object's effects (particles, lights) copied around a point of the model, at a size and in a colour:
            // source prefab, centre and height in the model's space, tint.
            public (string source, Vector3 center, float height, Color tint) Aura;
            // Wood, biome metal, biome ingredient: (prefab, amount, amount per upgrade level).
            public (string item, int amount, int perLevel)[] Recipe;
        }
        internal static readonly Staff[] Staffs =
        {
            // Black Forest: the staff of embers' model.
            new Staff { Prefab = "Overhaul_BlackForestStaff", Base = "StaffFireball", Name = "$overhaul_staff_blackforest", Station = "forge", Power = 20, PowerPerLevel = 5,
                Recipe = new[] { ("Wood", 10, 5), ("Bronze", 5, 3), ("SurtlingCore", 3, 1) } },
            // Swamp: the staff of fracturing's model and material, its red crystals and gems in ectoplasm green (SwampStaff_d:
            // the staff's texture recoloured; SwampStaff_Model splits Ecto, the crystals, from Wood); its flames give way.
            new Staff { Prefab = "Overhaul_SwampStaff", Base = "StaffClusterbomb", Model = "SwampStaff_Model", Name = "$overhaul_staff_swamp", Station = "forge", Power = 35, PowerPerLevel = 6,
                Materials = new Dictionary<string, (string, string, string)> {
                    { "Wood", ("StaffClusterbomb", "FragStaff_mat", "SwampStaff_d") }, { "Ecto", ("StaffClusterbomb", "FragStaff_mat", "SwampStaff_d") } },
                Effects = ("Ecto", "Ectoplasm"),
                // Around the head crystals, the fuling totem's glow and sparks as on the Totemic staff (same size), in
                // ectoplasm green.
                Aura = ("GoblinTotem", new Vector3(.07f, 1.78f, 0), .378f, new Color(.478f, .891f, .698f)),
                Recipe = new[] { ("ElderBark", 10, 5), ("Iron", 5, 3), ("Ectoplasm", 5, 2) } },
            // Mountains: the frost staff's model.
            new Staff { Prefab = "Overhaul_MountainStaff", Base = "StaffIceShards", Name = "$overhaul_staff_mountain", Station = "forge", Power = 50, PowerPerLevel = 7,
                Recipe = new[] { ("RoundLog", 10, 5), ("Silver", 5, 3), ("FreezeGland", 5, 2) } },
            // Plains: the staff of embers' wood with its own diffuse, black metal rings, a fuling totem.
            new Staff { Prefab = "Overhaul_PlainsStaff", Base = "StaffFireball", Model = "PlainsStaff_Model", Name = "$overhaul_staff_plains", Station = "forge", Power = 65, PowerPerLevel = 8,
                Materials = new Dictionary<string, (string, string, string)> {
                    { "Wood", ("StaffFireball", "staffs_mat", "PlainsStaff_d") }, { "BlackMetal", ("BlackMetal", "blackmetal", null) }, { "Totem", ("GoblinTotem", "Idol_mat", null) } },
                Effects = ("Totem", "GoblinTotem"),
                Recipe = new[] { ("FineWood", 10, 5), ("BlackMetal", 5, 3), ("GoblinTotem", 2, 1) } },
            // Mistlands: the staff of embers' wood, black metal bands and a black core, with the fuling totem's purple glow
            // and sparks around the core.
            new Staff { Prefab = "Overhaul_MistlandsStaff", Base = "StaffFireball", Model = "MistlandStaff_Model", Name = "$overhaul_staff_mistlands", Station = "piece_magetable", Power = 80, PowerPerLevel = 9,
                Materials = new Dictionary<string, (string, string, string)> {
                    { "Wood", ("StaffFireball", "staffs_mat", null) }, { "BlackMetal", ("BlackMetal", "blackmetal", null) }, { "BlackCore", ("BlackCore", "BlackCore_mat", null) } },
                Effects = ("BlackCore", "GoblinTotem"),
                Recipe = new[] { ("YggdrasilWood", 10, 5), ("BlackMetal", 8, 4), ("BlackCore", 2, 1) } },
            // Ashlands: staff of embers' model until its own is made.
            new Staff { Prefab = "Overhaul_AshlandsStaff", Base = "StaffFireball", Name = "$overhaul_staff_ashlands", Station = "piece_magetable", Power = 100, PowerPerLevel = 10,
                Recipe = new[] { ("Blackwood", 10, 5), ("FlametalNew", 5, 3), ("MoltenCore", 2, 1) } },
        };
        internal static readonly Dictionary<string, Staff> ByPrefab = Staffs.ToDictionary(s => s.Prefab);

        static AssetBundle bundle;
        internal static void Initialize() { PrefabManager.OnVanillaPrefabsAvailable += Register; ItemManager.OnItemsRegistered += Icons; }
        internal static void Shutdown() { PrefabManager.OnVanillaPrefabsAvailable -= Register; ItemManager.OnItemsRegistered -= Icons; }

        static void Register()
        {
            PrefabManager.OnVanillaPrefabsAvailable -= Register;
            if (!bundle) bundle = Utility.EmbeddedAssets.LoadBundle("Overhaul.Assets.overhaul_staffs");
            foreach (var s in Staffs)
            {
                if (PrefabManager.Instance.GetPrefab(s.Prefab)) continue;
                GameObject model = null;
                if (s.Model != null && !(model = bundle.LoadAsset<GameObject>(s.Model))) { Log.LogError("Staff model " + s.Model + " missing from overhaul_staffs"); continue; }
                var config = new ItemConfig { CraftingStation = s.Station, MinStationLevel = 1,
                    Requirements = s.Recipe.Select(r => new RequirementConfig(r.item, r.amount, r.perLevel, true)).ToArray() };
                var item = new CustomItem(s.Prefab, s.Base, config);
                var shared = item.ItemDrop.m_itemData.m_shared;
                shared.m_name = s.Name; shared.m_description = s.Name + "_description";
                // Magic power replaces the damage, in the generic damage (StaffMagic): the spell gives it its element.
                shared.m_damages = new HitData.DamageTypes { m_damage = s.Power }; shared.m_damagesPerLevel = new HitData.DamageTypes { m_damage = s.PowerPerLevel };
                if (model) Dress(item.ItemPrefab, model, s.Materials.ToDictionary(m => m.Key, m => Tweak(Material(PrefabManager.Instance.GetPrefab(m.Value.prefab), m.Value.material, m.Value.diffuse == null ? null : bundle.LoadAsset<Texture2D>(m.Value.diffuse)), s.Tweaks, m.Key)));
                if (model && s.Aura.source != null) AddAura(item.ItemPrefab, PrefabManager.Instance.GetPrefab(s.Aura.source), s.Aura.center, s.Aura.height, s.Aura.tint);
                if (s.Effects.prefab != null) SwapEffects(item.ItemPrefab, s.Effects.piece, PrefabManager.Instance.GetPrefab(s.Effects.prefab));
                ItemManager.Instance.AddItem(item);
            }
        }
        // ---------------------------------------------------------------- icons
        // A staff with its own model gets its own icon, rendered by the game itself (the editor does not light the game's
        // shaders like the game): the item held upright on a layer of its own, far from the world, photographed with its
        // own camera and light like the game's staff icons (head in close-up, staff on a diagonal, head up right, foot cut
        // off), its head glow and sparks included, on a transparent background. Once per session, on machines that draw (not a dedicated server).
        const int IconLayer = 31, IconSize = 128;
        static readonly Dictionary<string, Sprite> IconCache = new Dictionary<string, Sprite>();
        static void Icons()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            foreach (var s in Staffs.Where(s => s.Model != null))
            {
                var prefab = PrefabManager.Instance.GetPrefab(s.Prefab)?.GetComponent<ItemDrop>();
                if (!prefab) continue;
                try
                {
                    if (!IconCache.TryGetValue(s.Prefab, out var icon) || !icon) IconCache[s.Prefab] = icon = RenderIcon(prefab.gameObject);
                    if (icon) prefab.m_itemData.m_shared.m_icons = new[] { icon };
                }
                catch (System.Exception e) { Log.LogWarning("Staff icon " + s.Prefab + ": " + e.Message); }
            }
        }

        static Sprite RenderIcon(GameObject prefab)
        {
            bool init = ZNetView.m_forceDisableInit; ZNetView.m_forceDisableInit = true;
            GameObject staff;
            try { staff = Object.Instantiate(prefab, new Vector3(0, 5000, 0), Quaternion.identity); }
            finally { ZNetView.m_forceDisableInit = init; }
            var cam = new GameObject("Overhaul staff icon camera").AddComponent<Camera>();
            var light = new GameObject("Overhaul staff icon light").AddComponent<Light>();
            var rt = RenderTexture.GetTemporary(IconSize * 2, IconSize * 2, 24, RenderTextureFormat.ARGB32);
            try
            {
                foreach (var body in staff.GetComponentsInChildren<Rigidbody>(true)) body.isKinematic = true;
                // The item's ground glint (a particle system on its root) is left out.
                foreach (var ps in staff.GetComponents<ParticleSystem>()) ps.gameObject.GetComponent<ParticleSystemRenderer>().enabled = false;
                var lod = staff.GetComponent<LODGroup>(); if (lod) lod.ForceLOD(0);
                // Upright: the staff mesh's own axes made the world's (the item prefab lies it down on the ground).
                var mesh = staff.GetComponentsInChildren<MeshFilter>(true).First(m => m.name.StartsWith("default"));
                staff.transform.rotation = Quaternion.Inverse(Quaternion.Inverse(staff.transform.rotation) * mesh.transform.rotation);
                foreach (var t in staff.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = IconLayer;
                // Framed on the model alone (the particles would stretch the bounds).
                var renderers = staff.GetComponentsInChildren<MeshRenderer>(true).Where(x => x.enabled && x.gameObject.activeInHierarchy).ToArray();
                var bounds = renderers[0].bounds; foreach (var x in renderers) bounds.Encapsulate(x.bounds);
                float height = bounds.size.y;
                var center = new Vector3(bounds.center.x, bounds.max.y - height * .2f, bounds.center.z);

                cam.enabled = false; cam.cullingMask = 1 << IconLayer; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.orthographic = true; cam.orthographicSize = height * .23f; cam.nearClipPlane = .01f; cam.farClipPlane = 20;
                cam.transform.position = center + new Vector3(0, 0, 3); cam.transform.LookAt(center); cam.transform.Rotate(0, 0, 38, Space.Self);
                light.type = LightType.Directional; light.cullingMask = 1 << IconLayer; light.intensity = 1.3f; light.color = new Color(1, .96f, .9f);
                light.transform.rotation = Quaternion.LookRotation(new Vector3(-.4f, -.5f, -1f));
                // The staff's glow and sparks (its head effects) run a moment, so they show settled in the icon.
                foreach (var ps in staff.GetComponentsInChildren<ParticleSystem>(true)) if (ps.gameObject != staff) { ps.Simulate(2f, false, true); ps.Pause(); }
                cam.targetTexture = rt; cam.Render();
                var previous = RenderTexture.active; RenderTexture.active = rt;
                var big = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false); big.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); big.Apply();
                RenderTexture.active = previous;
                // Halved with alpha-weighted averaging (smooth edges on the transparent background).
                var icon = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                var src = big.GetPixels(); var dst = new Color[IconSize * IconSize];
                for (int y = 0; y < IconSize; y++)
                    for (int x = 0; x < IconSize; x++)
                    {
                        Color sum = Color.clear;
                        for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++) { var c = src[(y * 2 + j) * rt.width + x * 2 + i]; float a = Mathf.Clamp01(c.a); sum += new Color(c.r * a, c.g * a, c.b * a, a); }
                        sum /= 4; dst[y * IconSize + x] = sum.a > 0 ? new Color(sum.r / sum.a, sum.g / sum.a, sum.b / sum.a, sum.a) : Color.clear;
                    }
                icon.SetPixels(dst); icon.Apply(); Object.Destroy(big);
                return Sprite.Create(icon, new Rect(0, 0, IconSize, IconSize), new Vector2(.5f, .5f));
            }
            finally
            {
                cam.targetTexture = null; RenderTexture.ReleaseTemporary(rt);
                Object.Destroy(cam.gameObject); Object.Destroy(light.gameObject); Object.DestroyImmediate(staff);
            }
        }

        // A copy of the material with the staff's tweaks for that piece, or the material itself.
        static Material Tweak(Material material, Dictionary<string, (string property, float value)[]> tweaks, string piece)
        {
            if (!material || tweaks == null || !tweaks.TryGetValue(piece, out var values)) return material;
            var copy = new Material(material) { name = material.name + "_overhaul" };
            foreach (var (property, value) in values) copy.SetFloat(property, value);
            return copy;
        }
        // Copies the effects around the source's model (its mesh's siblings with particles or lights) to a point of the
        // staff model, scaled from the source mesh's height to the given one, their particles and lights tinted. Public
        // for the Unity preview.
        public static void AddAura(GameObject staff, GameObject source, Vector3 center, float height, Color tint)
        {
            var host = staff.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name.StartsWith("default"))?.transform;
            var theirs = source ? source.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.GetComponent<MeshRenderer>()) : null;
            if (!host || !theirs) return;
            var tb = theirs.sharedMesh.bounds; var tm = Matrix4x4.TRS(theirs.transform.localPosition, theirs.transform.localRotation, theirs.transform.localScale);
            Vector3 tc = tm.MultiplyPoint3x4(tb.center); float scale = height / (tb.size.y * theirs.transform.localScale.y);
            foreach (Transform fx in theirs.transform.parent)
            {
                if (fx == theirs.transform || !(fx.GetComponentInChildren<ParticleSystem>(true) || fx.GetComponentInChildren<Light>(true))) continue;
                var copy = Object.Instantiate(fx.gameObject, host, false); copy.name = fx.name;
                copy.transform.localPosition = center + (fx.localPosition - tc) * scale; copy.transform.localRotation = fx.localRotation; copy.transform.localScale = fx.localScale * scale;
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                    main.startColor = Tinted(main.startColor, tint);
                    var life = ps.colorOverLifetime; if (life.enabled) life.color = Tinted(life.color, Color.white);
                }
                foreach (var l in copy.GetComponentsInChildren<Light>(true)) { l.range *= scale; l.color = tint; }
            }
        }
        // The tint in place of the colours, each keeping its own alpha (fades stay as they were).
        static ParticleSystem.MinMaxGradient Tinted(ParticleSystem.MinMaxGradient g, Color tint)
        {
            Color C(Color c) => new Color(tint.r, tint.g, tint.b, c.a);
            Gradient G(Gradient x) { if (x == null) return null; var n = new Gradient(); n.SetKeys(x.colorKeys.Select(k => new GradientColorKey(new Color(tint.r, tint.g, tint.b), k.time)).ToArray(), x.alphaKeys); n.mode = x.mode; return n; }
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color: return new ParticleSystem.MinMaxGradient(C(g.color));
                case ParticleSystemGradientMode.TwoColors: return new ParticleSystem.MinMaxGradient(C(g.colorMin), C(g.colorMax));
                case ParticleSystemGradientMode.Gradient: return new ParticleSystem.MinMaxGradient(G(g.gradient));
                case ParticleSystemGradientMode.TwoGradients: return new ParticleSystem.MinMaxGradient(G(g.gradientMin), G(g.gradientMax));
                default: return g;
            }
        }

        static Material FindMaterial(GameObject prefab, string name) =>
            prefab ? prefab.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).FirstOrDefault(m => m && m.name == name) : null;
        // The game material, or a copy of it with another diffuse (normal, emission, metal and gloss kept); the new
        // texture samples like the one it replaces. Public for the Unity preview.
        public static Material Material(GameObject prefab, string name, Texture2D diffuse)
        {
            var game = FindMaterial(prefab, name);
            if (!game || !diffuse) return game;
            var copy = new Material(game) { name = name + "_" + diffuse.name };
            if (game.mainTexture) { diffuse.filterMode = game.mainTexture.filterMode; diffuse.wrapMode = game.mainTexture.wrapMode; }
            copy.mainTexture = diffuse;
            return copy;
        }

        // Puts the model in place of the base staff's mesh: same parent and local transform (the model is authored in
        // the base mesh's space), each piece with its game material; the LOD keeps the new renderers. Public for the
        // Unity preview.
        public static void Dress(GameObject staff, GameObject model, Dictionary<string, Material> materials)
        {
            var old = staff.GetComponentsInChildren<MeshFilter>(true).First(f => f.GetComponent<MeshRenderer>() && f.name.StartsWith("default"));
            var host = old.transform;
            var added = new List<Renderer>();
            foreach (var piece in model.GetComponentsInChildren<MeshFilter>(true))
            {
                var go = new GameObject(piece.name); go.transform.SetParent(host, false);
                go.transform.localPosition = piece.transform.localPosition; go.transform.localRotation = piece.transform.localRotation; go.transform.localScale = piece.transform.localScale;
                go.layer = host.gameObject.layer;
                go.AddComponent<MeshFilter>().sharedMesh = piece.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                materials.TryGetValue(piece.name, out var mat);
                r.sharedMaterials = Enumerable.Repeat(mat, piece.sharedMesh.subMeshCount).ToArray();
                added.Add(r);
            }
            // The base mesh is hidden, not removed: its collider and the effects under it stay.
            var oldRenderer = old.GetComponent<MeshRenderer>(); oldRenderer.enabled = false;
            var lod = staff.GetComponent<LODGroup>();
            if (lod)
            {
                var lods = lod.GetLODs();
                for (int i = 0; i < lods.Length; i++) if (lods[i].renderers.Contains(oldRenderer)) lods[i].renderers = lods[i].renderers.Where(x => x != oldRenderer).Concat(added).ToArray();
                lod.SetLODs(lods);
            }
        }

        // Removes the base staff's effects (the flames, embers, glow and light of its head and of its equipped state)
        // and copies the effects that sit around the game model's mesh (its siblings: particles, lights) around our
        // piece, moved and scaled from the game mesh's bounds to ours. Public for the Unity preview.
        public static void SwapEffects(GameObject staff, string piece, GameObject source)
        {
            foreach (var t in staff.GetComponentsInChildren<Transform>(true).Where(t => t.name == "effects" || t.parent && t.parent.name == "equiped").ToArray()) Object.DestroyImmediate(t.gameObject);
            var ours = staff.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.name == piece);
            var theirs = source ? source.GetComponentsInChildren<MeshFilter>(true).FirstOrDefault(m => m.GetComponent<MeshRenderer>()) : null;
            if (!ours || !theirs) return;
            // Game model bounds in its parent's space, ours in the staff host's space.
            var tb = theirs.sharedMesh.bounds; var tm = Matrix4x4.TRS(theirs.transform.localPosition, theirs.transform.localRotation, theirs.transform.localScale);
            Vector3 tc = tm.MultiplyPoint3x4(tb.center); float ts = tb.size.y * theirs.transform.localScale.y;
            var ob = ours.sharedMesh.bounds; var host = ours.transform.parent;
            Vector3 oc = ours.transform.localPosition + ours.transform.localRotation * Vector3.Scale(ours.transform.localScale, ob.center);
            float scale = ob.size.y * ours.transform.localScale.y / ts;
            foreach (Transform fx in theirs.transform.parent)
            {
                if (fx == theirs.transform || !(fx.GetComponentInChildren<ParticleSystem>(true) || fx.GetComponentInChildren<Light>(true))) continue;
                var copy = Object.Instantiate(fx.gameObject, host, false); copy.name = fx.name;
                copy.transform.localPosition = oc + (fx.localPosition - tc) * scale; copy.transform.localRotation = fx.localRotation; copy.transform.localScale = fx.localScale * scale;
                foreach (var ps in copy.GetComponentsInChildren<ParticleSystem>(true)) { var main = ps.main; main.scalingMode = ParticleSystemScalingMode.Hierarchy; }
                foreach (var l in copy.GetComponentsInChildren<Light>(true)) l.range *= scale;
            }
        }
    }
}
