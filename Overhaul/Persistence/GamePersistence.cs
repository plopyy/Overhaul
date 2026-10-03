using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GamePersistence
    {
        private static ProgressiveWriter writer;
        private static ZNet session;
        private static bool loading;
        private static long nextId;
        private static float nextCapture;
        private static string lastError;
        private static readonly Dictionary<ZDOID,long> ids=new Dictionary<ZDOID,long>();
        private static readonly HashSet<ZDOID> dirty=new HashSet<ZDOID>();
        internal static bool Active=>writer!=null&&!loading;
        internal static bool SaveHeader(World world)
        {
            if(!TryWorld(world.m_worldName,world.m_fileSource,out var stored))return false;
            if(stored.m_uid!=world.m_uid)throw new InvalidDataException("World metadata identity mismatch");
            if(Active){Save(false);return true;}
            string path=PathFor(world.m_worldName,world.m_fileSource);
            using(var guard=new FileStream(path+".owner",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            using(var db=new SqliteDatabase(path))
            {
                var header=new WorldRecord{Name=world.m_name,SeedName=world.m_seedName,Seed=world.m_seed,Uid=world.m_uid,Generation=world.m_worldGenVersion};header.InitialKeys.AddRange(world.m_startingGlobalKeys);
                db.Transaction(()=>WorldSql.WriteHeader(db,header));
            }
            return true;
        }
        internal static string PathFor(string name,FileHelpers.FileSource source)
        {
            if(string.IsNullOrEmpty(name)||System.IO.Path.GetFileName(name)!=name)throw new InvalidDataException("Invalid world name");
            return System.IO.Path.Combine(World.GetSaveDirectory(source,name),name+".db");
        }
        internal static bool TryWorld(string name,FileHelpers.FileSource source,out World result)
        {
            result=null;if(source!=FileHelpers.FileSource.Local)return false;
            string path=PathFor(name,source);if(!File.Exists(path))return false;
            using(var db=new SqliteDatabase(path,true))
            {
                if(!WorldSchema.Owned(db))throw new InvalidDataException("Unrecognized database at "+path);
                if(!MigrationState.Complete(db))return false;
                var state=WorldSql.Read(db);if(!MigrationState.Complete(db,state.Uid))throw new InvalidDataException("Incomplete migration");
                result=new World{m_worldName=name,m_fileSource=source};GameSnapshot.ApplyHeader(result,state);return true;
            }
        }
        internal static bool Load(ZNet net)
        {
            var world=ZNet.m_world;
            if(world==null||world.m_menu||world.m_fileSource!=FileHelpers.FileSource.Local)return false;
            Close();loading=true;session=net;nextId=0;FileStream startupOwnership=null;
            try
            {
                string path=PathFor(world.m_worldName,world.m_fileSource),directory=System.IO.Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                startupOwnership=new FileStream(path+".owner",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                if(File.Exists(path))
                {
                    bool complete;
                    using(var db=new SqliteDatabase(path)){if(!WorldSchema.Owned(db))throw new InvalidDataException("Unrecognized world database");complete=MigrationState.Complete(db,world.m_uid);db.Checkpoint();}
                    if(!complete)File.Move(path,path+".incomplete-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff"));
                }
                if(!File.Exists(path))
                {
                    if(Directory.GetFiles(directory,"_main.*.ok").Length>0)
                        NativeMigration.Run(directory,path,BrotliCompressor.DecompressBytes,(percent,message)=>ZLog.Log("[Overhaul SQLite] Migration "+percent+"%: "+message),expectedWorld:world.m_uid);
                    else if(world.m_needsDB||File.Exists(world.GetDBPath()))throw new InvalidDataException("Existing world has no complete format-41 chunked save. Native save retained.");
                    else
                    {
                        string temporary=path+".new-"+Guid.NewGuid().ToString("N");
                        using(var db=new SqliteDatabase(temporary)){WorldSchema.Create(db);db.Transaction(()=>{WorldSql.Write(db,GameSnapshot.CaptureWorld(net));MigrationState.Finish(db,world.m_uid);});db.Checkpoint();}
                        File.Move(temporary,path);
                    }
                }
                writer=new ProgressiveWriter(path,startupOwnership);startupOwnership=null;
                var links=new List<ZDOID>();var manager=net.m_zdoMan;manager.ResetBeforeLoad();
                using(var db=new SqliteDatabase(path,true))
                {
                    if(!MigrationState.Complete(db,world.m_uid))throw new InvalidDataException("World migration is incomplete");
                    GameSnapshot.RestoreWorld(net,WorldSql.Read(db));
                    foreach(var item in ObjectSql.Read(db))
                    {
                        var z=GameSnapshot.Restore(manager,item);ids.Add(z.m_uid,item.Id);nextId=Math.Max(nextId,item.Id);
                        if(item.ConnectionType!=0&&!item.TargetUser.HasValue)links.Add(z.m_uid);
                    }
                }
                manager.ConnectPortals();manager.ConnectSpawners();manager.ConnectSyncTransforms();Game.instance.ConnectPortals();
                manager.m_deadZDOs.Clear();manager.DirtyChunks.Clear();manager.DirtyPortalObjects=false;
                net.WorldSetup();net.OnWorldSaveLoaded();
                // Convert every imported connection together before incremental snapshots can mix formats.
                links.AddRange(manager.GetPortalList().Select(z=>z.m_uid));
                if(links.Count>0){writer.Enqueue(links.Distinct().Select(uid=>GameSnapshot.Capture(manager.GetZDO(uid),ids[uid])).ToArray(),null);writer.Flush();}
                loading=false;nextCapture=Time.realtimeSinceStartup+5;lastError=null;
                ZLog.Log("[Overhaul SQLite] World loaded: "+ids.Count+" objects. Progressive saves every 5 seconds: "+path);
                return true;
            }
            catch(Exception ex)
            {
                ZNet.m_loadError=true;loading=false;writer?.Dispose();writer=null;session=null;ids.Clear();dirty.Clear();
                ZLog.LogError("[Overhaul SQLite] World loading failed; saving disabled. Native files retained. "+ex);
                throw; // Do not start an empty world or fall back to an old native generation.
            }
            finally{startupOwnership?.Dispose();}
        }
        internal static void Mark(ZDOID uid){if(Active)dirty.Add(uid);}
        private static bool CanSave()=>Active&&session&&session.IsServer()&&!ZNet.m_loadError&&!SaveSystem.HasSessionFlag(SaveSystemSessionFlags.DontSaveWorld)&&!ZoneSystem.instance.SkipSaving()&&!DungeonDB.instance.SkipSaving();
        internal static void Capture()
        {
            if(!CanSave())return;
            var changes=new List<ObjectRecord>();var removed=new List<long>();
            foreach(var uid in dirty)
            {
                var z=session.m_zdoMan.GetZDO(uid);
                if(z==null||!z.Persistent)
                {if(ids.TryGetValue(uid,out long old)){removed.Add(old);ids.Remove(uid);}continue;}
                if(!ids.TryGetValue(uid,out long id)){id=checked(++nextId);ids.Add(uid,id);}
                changes.Add(GameSnapshot.Capture(z,id));
            }
            var metadata=GameSnapshot.CaptureWorld(session);
            writer.Enqueue(changes,removed,metadata);dirty.Clear();
        }
        internal static void Tick()
        {
            if(!Active)return;
            try
            {
                if(Time.realtimeSinceStartup>=nextCapture){Capture();writer.RequestFlush();nextCapture=Time.realtimeSinceStartup+5;}
                string error=writer.LastError;if(error!=lastError){lastError=error;if(error!=null)ZLog.LogError("[Overhaul SQLite] SAVE FAILED; changes remain pending: "+error);}
            }
            catch(Exception ex){ZNet.m_loadError=true;ZLog.LogError("[Overhaul SQLite] Snapshot failed; saving disabled: "+ex);}
        }
        internal static void Save(bool sync)
        {
            Capture();writer.RequestFlush(true);if(sync)writer.Flush();
        }
        internal static void Close()
        {
            if(writer==null)return;
            Capture();writer.Dispose();writer=null;session=null;loading=false;ids.Clear();dirty.Clear();
        }
    }

    [HarmonyPatch(typeof(World),nameof(World.GetCreateWorld))]
    internal static class SqliteCreateWorldPatch
    {
        static bool Prefix(string name,FileHelpers.FileSource source,ref World __result)=>!GamePersistence.TryWorld(name,source,out __result);
    }
    [HarmonyPatch(typeof(World),nameof(World.GetDevWorld))]
    internal static class SqliteDevWorldPatch
    {
        static bool Prefix(ref World __result)=>!GamePersistence.TryWorld(Game.instance.m_devWorldName,FileHelpers.FileSource.Local,out __result);
    }
    [HarmonyPatch(typeof(World),nameof(World.LoadWorld))]
    internal static class SqliteWorldHeaderPatch
    {
        static bool Prefix(SaveWithBackups saveFile,ref World __result)=>!GamePersistence.TryWorld(saveFile.Name,saveFile.PrimaryFile.m_source,out __result);
    }
    [HarmonyPatch(typeof(World),nameof(World.HaveWorld))]
    internal static class SqliteHaveWorldPatch
    {
        static bool Prefix(string name,ref bool __result){if(!File.Exists(GamePersistence.PathFor(name,FileHelpers.FileSource.Local)))return true;__result=true;return false;}
    }
    [HarmonyPatch(typeof(SaveSystem),nameof(SaveSystem.GetWorldList))]
    internal static class SqliteWorldListPatch
    {
        static void Postfix(List<World> __result)
        {
            string root=SaveSystem.GetWorldsSaveRootPath(FileHelpers.FileSource.Local);if(!Directory.Exists(root))return;
            foreach(string directory in Directory.GetDirectories(root))
            {
                string name=System.IO.Path.GetFileName(directory);
                if(GamePersistence.TryWorld(name,FileHelpers.FileSource.Local,out var world)){__result.RemoveAll(w=>w.m_worldName==name&&w.m_fileSource==FileHelpers.FileSource.Local);__result.Add(world);}
            }
        }
    }
    [HarmonyPatch(typeof(World),nameof(World.SaveWorldFWLData),new[]{typeof(DateTime)})]
    internal static class SqliteWorldMetadataPatch
    {
        static bool Prefix(World __instance)
        {
            return !GamePersistence.SaveHeader(__instance);
        }
    }
    [HarmonyPatch]
    internal static class SqliteWorldMetadataWriterPatch
    {
        static MethodBase TargetMethod()=>AccessTools.Method(typeof(World),nameof(World.SaveWorldFWLData),new[]{typeof(DateTime),typeof(FileWriter).MakeByRefType()});
        static bool Prefix(World __instance,ref FileWriter metaWriter){if(!GamePersistence.SaveHeader(__instance))return true;metaWriter=null;return false;}
    }
    [HarmonyPatch(typeof(ZNet),"LoadWorld")]
    internal static class SqliteLoadPatch
    {
        static bool Prefix(ZNet __instance,ref bool __result){if(!GamePersistence.Load(__instance))return true;__result=true;return false;}
    }
    [HarmonyPatch(typeof(ZNet),"LoadOldWorld")]
    internal static class SqliteLoadOldPatch
    {
        static bool Prefix(ZNet __instance)=>!GamePersistence.Load(__instance);
    }
    [HarmonyPatch(typeof(ZNet),"SaveWorld")]
    internal static class SqliteSavePatch
    {
        [HarmonyPriority(Priority.Last)]
        static bool Prefix(bool sync){if(!GamePersistence.Active)return true;GamePersistence.Save(sync);return false;}
    }
    [HarmonyPatch(typeof(ZNet),"StopAll")]
    internal static class SqliteStopPatch {static void Prefix()=>GamePersistence.Close();}
    [HarmonyPatch(typeof(ZDOMan),nameof(ZDOMan.SetDirtySector))]
    internal static class SqliteDirtyPatch {static void Postfix(ZDO zdo)=>GamePersistence.Mark(zdo.m_uid);}
    [HarmonyPatch(typeof(ZDOMan),"HandleDestroyedZDO")]
    internal static class SqliteDeletedPatch {static void Prefix(ZDOID uid)=>GamePersistence.Mark(uid);}
    [HarmonyPatch]
    internal static class SqliteObjectChangedPatch
    {
        static IEnumerable<MethodBase> TargetMethods()=>AccessTools.GetDeclaredMethods(typeof(ZDO)).Where(m=>m.Name=="Deserialize"||m.Name=="SetPosition"||m.Name=="set_Persistent"||m.Name=="set_Distant"||m.Name=="set_Type");
        static void Postfix(ZDO __instance)=>GamePersistence.Mark(__instance.m_uid);
    }
    [HarmonyPatch]
    internal static class SqliteExtraChangedPatch
    {
        static IEnumerable<MethodBase> TargetMethods()=>AccessTools.GetDeclaredMethods(typeof(ZDOExtraData)).Where(m=>(m.Name=="Set"||m.Name=="Update"||m.Name=="Add"||m.Name.StartsWith("Remove",StringComparison.Ordinal)||m.Name=="SetConnection")&&m.GetParameters().Length>0&&m.GetParameters()[0].ParameterType==typeof(ZDOID));
        static void Postfix(ZDOID __0)=>GamePersistence.Mark(__0);
    }
}
