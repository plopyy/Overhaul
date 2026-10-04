using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameLifeView
    {
        internal static bool IsKey(string key)=>key==GameDeathProgress.Dead||key=="time_since_death"||key==GameRespawnGame.After;
        private static PlayerChange[] initial;
        internal static void Initial(IEnumerable<PlayerChange> rows)=>initial=rows?.Where(r=>r.Table=="state"&&IsKey((string)r.Values[0])).ToArray();
        [HarmonyPatch(typeof(Player),nameof(Player.Load))]
        private static class Restore
        {
            private static void Postfix(Player __instance)
            {if(!PlayerSessionGame.Managed||initial==null)return;var rows=initial;initial=null;Presentation(rows,__instance)();}
        }
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var rows=source.ToArray();if(rows.Length==0)return ()=>{};
            if(!player||!Game.instance||rows.Any(r=>r.Table=="spawn")&&ZNet.m_world==null)throw new InvalidDataException("Player life view is unavailable");
            long worldId=ZNet.m_world?.m_uid??0;
            var profile=Game.instance.GetPlayerProfile();var updates=new List<Action>();
            foreach(var row in rows)
            {
                var v=row.Values;
                if(row.Table=="state"&&!row.Delete&&IsKey((string)v[0]))
                {
                    if((string)v[0]==GameRespawnGame.After){long ticks=Convert.ToInt64(v[1]);if(ticks<0||ticks>DateTime.MaxValue.Ticks)throw new InvalidDataException("Invalid respawn deadline");continue;}
                    if((string)v[0]==GameDeathProgress.Dead)
                    {
                        int value=Convert.ToInt32(v[1]);if(value!=0&&value!=1)throw new InvalidDataException("Invalid confirmed death flag");
                        updates.Add(()=>{player.m_isDead=value==1;if(player.m_nview&&player.m_nview.IsValid())player.m_nview.GetZDO().Set(ZDOVars.s_dead,value==1);if(player.m_visual)player.m_visual.SetActive(value==0);});
                    }
                    else
                    {
                        float elapsed=Convert.ToSingle(v[2]);if(float.IsNaN(elapsed)||float.IsInfinity(elapsed)||elapsed<0)throw new InvalidDataException("Invalid death protection time");
                        updates.Add(()=>player.m_timeSinceDeath=elapsed);
                    }
                    continue;
                }
                if(row.Table!="spawn")throw new InvalidDataException("Unknown player life change");
                string kind=(string)v[0];if(kind!="bed"&&kind!="logout"&&kind!="death"&&kind!="home")throw new InvalidDataException("Unknown spawn point");
                var point=row.Delete?Vector3.zero:new Vector3(Convert.ToSingle(v[1]),Convert.ToSingle(v[2]),Convert.ToSingle(v[3]));
                if(new[]{point.x,point.y,point.z}.Any(n=>float.IsNaN(n)||float.IsInfinity(n)))throw new InvalidDataException("Invalid spawn position");
                updates.Add(()=>
                {
                    if(!profile.m_worldData.TryGetValue(worldId,out var world)){world=new PlayerProfile.WorldPlayerData();profile.m_worldData.Add(worldId,world);}
                    if(kind=="bed"){world.m_haveCustomSpawnPoint=!row.Delete;world.m_spawnPoint=point;}
                    if(kind=="logout"){world.m_haveLogoutPoint=!row.Delete;world.m_logoutPoint=point;}
                    if(kind=="death"){world.m_haveDeathPoint=!row.Delete;world.m_deathPoint=point;}
                    if(kind=="home")world.m_homePoint=point;
                });
            }
            return ()=>{foreach(var apply in updates)apply();};
        }
    }
}
