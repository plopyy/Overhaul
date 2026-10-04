using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Native terrain maths run on inactive copies. No live terrain or ZDO changes
    // until the material debit and every affected tile have committed together.
    internal sealed class PlayerTerrainGame : IDisposable
    {
        private sealed class Tile
        {internal Heightmap Map;internal TerrainComp Live,Copy;internal ObjectRecord Record;internal byte[] Data;}
        private readonly Dictionary<Heightmap,Tile> tiles=new Dictionary<Heightmap,Tile>();
        private readonly HashSet<Heightmap> pendingPaint=new HashSet<Heightmap>();
        private static readonly HashSet<Vector3> reserved=new HashSet<Vector3>();
        private static PlayerTerrainGame calculating;
        private bool submitted;
        internal static void Clear()=>reserved.Clear();
        private TerrainComp Copy(Heightmap map)
        {
            if(tiles.TryGetValue(map,out var known))return known.Copy;
            if(tiles.Count>=16 || !reserved.Add(map.transform.position))throw new InvalidOperationException("Terrain tile is already being edited");
            var tile=new Tile{Map=map};tiles.Add(map,tile);
            tile.Live=TerrainComp.FindTerrainCompiler(map.transform.position);
            if(tile.Live)
            {
                if(!tile.Live.m_initialized || !tile.Live.m_nview || !tile.Live.m_nview.IsValid())throw new InvalidOperationException("Terrain compiler is unavailable");
                tile.Record=GamePersistence.ReserveAction(tile.Live.m_nview.GetZDO());tile.Live.m_nview.GetZDO().SetOwner(ZNet.GetUID());tile.Live.CheckLoad();
            }
            else tile.Record=GamePersistence.AllocateActionObject(map.m_terrainCompilerPrefab,map.transform.position,Quaternion.identity);
            var go=new GameObject("Overhaul terrain calculation"){hideFlags=HideFlags.HideAndDontSave};go.SetActive(false);go.transform.position=map.transform.position;
            tile.Copy=go.AddComponent<TerrainComp>();var copy=tile.Copy;copy.m_hmap=map;copy.Initialize();
            if(tile.Live)
            {
                var live=tile.Live;copy.m_modifiedHeight=(bool[])live.m_modifiedHeight.Clone();copy.m_levelDelta=(float[])live.m_levelDelta.Clone();copy.m_smoothDelta=(float[])live.m_smoothDelta.Clone();
                copy.m_modifiedPaint=(bool[])live.m_modifiedPaint.Clone();copy.m_paintMask=(Color[])live.m_paintMask.Clone();copy.m_operations=live.m_operations;copy.m_lastOpPoint=live.m_lastOpPoint;copy.m_lastOpRadius=live.m_lastOpRadius;
            }
            return copy;
        }
        internal void Calculate(TerrainOp operation,Vector3 point,Quaternion rotation,long playerId)
        {
            if(calculating!=null)throw new InvalidOperationException("Nested terrain edit");
            float radius=operation.GetRadius();if(float.IsNaN(radius) || float.IsInfinity(radius) || radius<=0 || radius>32)throw new InvalidOperationException("Invalid terrain radius");
            var maps=new List<Heightmap>();Heightmap.FindHeightmap(point,radius,maps);if(maps.Count==0)throw new InvalidOperationException("Terrain is not loaded");
            // Check the affected perimeter too: a tool cannot paint through a ward border.
            for(int i=0;i<16;i++)
            {var check=point+new Vector3(Mathf.Cos(i*Mathf.PI/8),0,Mathf.Sin(i*Mathf.PI/8))*radius;if(!Storage.ChestAccess.WardAccessAt(check,playerId) || Location.IsInsideNoBuildLocation(check))throw new InvalidOperationException("Terrain edit intersects protected ground");}
            try
            {
                calculating=this;foreach(var map in maps)Copy(map);
                foreach(var map in maps)Copy(map).InternalDoOperation(point,rotation*Vector3.forward,operation.m_settings);
                foreach(var tile in tiles.Values)
                {
                    tile.Data=Serialize(tile.Copy);tile.Record.Properties.RemoveAll(p=>p.Key==ZDOVars.s_TCData && p.Type=="bytes");
                    tile.Record.Properties.Add(new PropertyRecord{Key=ZDOVars.s_TCData,Type="bytes",Value=tile.Data});
                }
            }
            finally{calculating=null;}
        }
        internal static byte[] Serialize(TerrainComp copy)
        {
            var p=new ZPackage();p.Write(1);p.Write(copy.m_operations);p.Write(copy.m_lastOpPoint);p.Write(copy.m_lastOpRadius);
            p.Write(copy.m_modifiedHeight.Length);for(int i=0;i<copy.m_modifiedHeight.Length;i++){p.Write(copy.m_modifiedHeight[i]);if(copy.m_modifiedHeight[i]){p.Write(copy.m_levelDelta[i]);p.Write(copy.m_smoothDelta[i]);}}
            p.Write(copy.m_modifiedPaint.Length);for(int i=0;i<copy.m_modifiedPaint.Length;i++){p.Write(copy.m_modifiedPaint[i]);if(copy.m_modifiedPaint[i]){var c=copy.m_paintMask[i];p.Write(c.r);p.Write(c.g);p.Write(c.b);p.Write(c.a);}}
            return Utils.Compress(p.GetArray());
        }
        internal PlayerActionPlan Finish(PlayerCraftResourcesGame resources,PlayerBatch player,TerrainOp operation,Vector3 point,IEnumerable<ObjectRecord> outputs)
        {
            var plan=resources.Finish(player,()=>operation.m_onPlacedEffect?.Create(point,Quaternion.identity),outputs,tiles.Values.ToDictionary(t=>t.Record.Id,t=>t.Record),Publish);
            submitted=true;return plan;
        }
        private void Publish()
        {
            foreach(var tile in tiles.Values)
            {
                if(tile.Live)
                {var data=tile.Live.m_nview.GetZDO();data.Set(ZDOVars.s_TCData,tile.Data);tile.Live.CheckLoad();}
                else
                {
                    GamePersistence.PublishActionObject(tile.Record);
                    var data=ZDOMan.instance.GetZDO(new ZDOID(tile.Record.User,tile.Record.NetworkId));
                    var instance=ZNetScene.instance.CreateObject(data);
                    if(!instance || !instance.GetComponent<TerrainComp>())throw new System.IO.IOException("Committed terrain compiler could not be instantiated");
                }
            }
            Release();
        }
        private void Release()
        {
            foreach(var tile in tiles.Values)
            {if(tile.Live && tile.Record!=null)GamePersistence.ReleaseAction(new[]{new ZDOID(tile.Record.User,tile.Record.NetworkId)});reserved.Remove(tile.Map.transform.position);}
        }
        public void Dispose()
        {
            if(!submitted)Release();
            foreach(var tile in tiles.Values)if(tile.Copy)UnityEngine.Object.DestroyImmediate(tile.Copy.gameObject);
        }
        [HarmonyPatch(typeof(Heightmap),nameof(Heightmap.GetAndCreateTerrainCompiler))]
        private static class CalculationCompiler
        {private static bool Prefix(Heightmap __instance,ref TerrainComp __result){if(calculating==null)return true;__result=calculating.Copy(__instance);return false;}}
        [HarmonyPatch(typeof(TerrainComp),"Save")]
        private static class CalculationSave {private static bool Prefix()=>calculating==null;}
        [HarmonyPatch(typeof(Heightmap),nameof(Heightmap.Poke))]
        private static class CalculationPoke
        {private static bool Prefix(Heightmap __instance){if(calculating==null)return true;calculating.pendingPaint.Add(__instance);return false;}}
        [HarmonyPatch]
        private static class CalculationMask
        {
            private static System.Reflection.MethodBase TargetMethod()=>typeof(TerrainComp).GetMethods(AccessTools.all).Single(m=>m.Name.StartsWith("<PaintCleared>g__getMask|",StringComparison.Ordinal) && m.IsStatic && m.ReturnType==typeof(Color));
            private static bool Prefix(Heightmap hmap,int index,ref Color __result)
            {if(calculating==null || !calculating.pendingPaint.Contains(hmap))return true;var copy=calculating.Copy(hmap);__result=copy.m_paintMask[index];return false;}
        }
        [HarmonyPatch(typeof(TerrainComp),"RPC_ApplyOperation")]
        private static class LegacyOperation {private static bool Prefix()=>PlayerPersistenceConfig.Enabled?.Value!=true;}
        [HarmonyPatch(typeof(TerrainComp),"Update")]
        private static class ReservedUpdate {private static bool Prefix(TerrainComp __instance)=>!__instance.m_nview || !__instance.m_nview.IsValid() || !GamePersistence.ActionReserved(__instance.m_nview.GetZDO().m_uid);}
    }
}
