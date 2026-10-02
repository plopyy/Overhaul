using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;

namespace Overhaul
{
    internal static class DvergerCirclet
    {
        internal const string PrefabName="HelmetDverger";
        internal const string SpiritName="Overhaul_SpiritCirclet";
        internal static readonly int PrefabHash=PrefabName.GetStableHashCode();
        internal static readonly int SpiritHash=SpiritName.GetStableHashCode();
        private static Sprite spiritIcon;
        private static Texture2D spiritTexture;
        private static readonly Dictionary<Material,Material> spiritMaterials=new Dictionary<Material,Material>();
        private static Texture2D LoadArt(string file)
        {
            using(var stream=typeof(DvergerCirclet).Assembly.GetManifestResourceStream("Overhaul.Assets."+file))
            using(var memory=new System.IO.MemoryStream())
            {
                if(stream==null)throw new System.IO.FileNotFoundException("Missing circlet art",file);
                stream.CopyTo(memory);
                var texture=new Texture2D(2,2,TextureFormat.RGBA32,false);
                if(!ImageConversion.LoadImage(texture,memory.ToArray()))
                {
                    Object.Destroy(texture);
                    throw new System.IO.InvalidDataException("Invalid circlet art: "+file);
                }
                texture.name=SpiritName+"_"+file;
                texture.filterMode=FilterMode.Point;
                texture.wrapMode=TextureWrapMode.Clamp;
                return texture;
            }
        }
        private static void ConfigureSpiritArt(GameObject prefab,ItemDrop item)
        {
            if(!spiritIcon)
            {
                var texture=LoadArt("spirit_icon.png");
                spiritIcon=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),new Vector2(.5f,.5f),100);
                spiritIcon.name=SpiritName+"_icon";
            }
            if(!spiritTexture)spiritTexture=LoadArt("spirit_texture.png");
            // Replace the array and materials, never edit the source circlet's shared assets.
            item.m_itemData.m_shared.m_icons=new[]{spiritIcon};
            foreach(var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                var materials=renderer.sharedMaterials;
                bool changed=false;
                for(int i=0;i<materials.Length;i++)
                {
                    var original=materials[i];
                    if(!original || (original.name!="dvergerhat" && original.name!="dvergerhat_stone"))continue;
                    if(!spiritMaterials.TryGetValue(original,out var material) || !material)
                    {
                        material=new Material(original){name=SpiritName+"_"+original.name};
                        if(original.name=="dvergerhat")material.SetTexture("_MainTex",spiritTexture);
                        else
                        {
                            var color=material.GetColor("_Color");
                            material.SetColor("_Color",new Color(1,1,1,color.a));
                            material.SetColor("_EmissionColor",Color.white);
                        }
                        spiritMaterials[original]=material;
                    }
                    materials[i]=material;
                    changed=true;
                }
                if(changed)renderer.sharedMaterials=materials;
            }
        }
        internal static bool IsSpirit(ItemDrop.ItemData item) => (item?.m_dropPrefab && item.m_dropPrefab.name==SpiritName) || item?.m_shared?.m_name=="$overhaul_spirit_circlet";
        internal static bool IsCirclet(ItemDrop.ItemData item) => IsSpirit(item) || (item?.m_dropPrefab && item.m_dropPrefab.name==PrefabName);
        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() => PrefabManager.OnVanillaPrefabsAvailable -= Register;
        internal static ItemConfig RecipeConfig() => new ItemConfig {
            Name="$overhaul_spirit_circlet", Description="$overhaul_spirit_circlet_description", Amount=1,
            CraftingStation="forge", MinStationLevel=1,
            Requirements=new[] {new RequirementConfig(PrefabName,1),new RequirementConfig("Demister",1)}
        };
        private static void Register()
        {
            if (PrefabManager.Instance.GetPrefab(SpiritName)) return;
            var item=new CustomItem(SpiritName,PrefabName,RecipeConfig());
            Configure(item.ItemPrefab);
            ItemManager.Instance.AddItem(item);
        }
        internal static void Configure(GameObject prefab)
        {
            if(!prefab || (prefab.name!=PrefabName && prefab.name!=SpiritName))return;
            var item=prefab.GetComponent<ItemDrop>();
            if(!item)return;
            item.m_itemData.m_shared.m_itemType=ItemDrop.ItemData.ItemType.Utility;
            if(prefab.name==SpiritName)ConfigureSpiritArt(prefab,item);
            var attach=prefab.transform.Find("attach");
            if(!attach)return;
            foreach(var light in attach.GetComponentsInChildren<Light>(true))
            {
                light.type=LightType.Point;
                if(prefab.name==SpiritName)light.color=new Color(.84f,.93f,1f,1f);
                light.cookie=null;
            }
            if(!attach.GetComponent<DvergerCircletVisual>())attach.gameObject.AddComponent<DvergerCircletVisual>();
        }
        // Utility normally attaches armor to named bones; this item uses the original helmet attachment.
        [HarmonyPatch(typeof(VisEquipment),"AttachArmor")]
        private static class Attach
        {
            private static bool Prefix(VisEquipment __instance,int itemHash,ref List<GameObject> __result)
            {
                if(itemHash!=PrefabHash && itemHash!=SpiritHash)return true;
                var instance=__instance.AttachItem(itemHash,0,__instance.m_helmet,true,false,0);
                __result=new List<GameObject>();
                if(instance)__result.Add(instance);
                return false;
            }
        }
    }
    internal sealed class DvergerCircletVisual : MonoBehaviour
    {
        private Renderer[] meshes;
        private VisEquipment equipment;
        private void Awake(){meshes=GetComponentsInChildren<Renderer>(true);}
        internal void SetHelmetPresent(bool present)
        {
            if(meshes==null)meshes=GetComponentsInChildren<Renderer>(true);
            foreach(var mesh in meshes)if(mesh)mesh.forceRenderingOff=present;
        }
        private void LateUpdate()
        {
            if(!equipment)equipment=GetComponentInParent<VisEquipment>(true);
            if(equipment)SetHelmetPresent(equipment.m_currentHelmetItemHash!=0);
        }
    }
}

