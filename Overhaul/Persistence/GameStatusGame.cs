using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace Overhaul.Persistence
{
    internal static class GameStatusGame
    {
        private static PlayerChange[] view=Array.Empty<PlayerChange>();
        private static readonly HashSet<int> confirmed=new HashSet<int>();
        private static bool initial;
        [ThreadStatic] private static int presenting,simulating;
        internal static bool Presenting=>presenting>0;
        internal static void Simulate(Action action){simulating++;try{action();}finally{simulating--;}}
        internal static void Initial(IEnumerable<PlayerChange> rows)
        {view=rows?.Where(r=>r.Table=="status"||r.Table=="status_data").ToArray()??Array.Empty<PlayerChange>();confirmed.Clear();initial=rows!=null;}
        internal static Action Presentation(IEnumerable<PlayerChange> source,Player player)
        {
            var delta=source.ToArray();if(delta.Length==0)return ()=>{};
            if(!player)throw new System.IO.InvalidDataException("Status view is unavailable");
            var next=PlayerProgressService.Overlay(new PlayerSnapshot(0,view),delta).Rows.ToArray();
            var work=new List<Action>();
            foreach(int id in delta.Select(r=>Convert.ToInt32(r.Values[0])).Distinct())
            {
                var rows=next.Where(r=>Convert.ToInt32(r.Values[0])==id).ToArray();
                if(rows.Length==0){work.Add(()=>{confirmed.Remove(id);player.GetSEMan().RemoveStatusEffect(id,true);});continue;}
                var restored=GameStatusCodec.Restore(rows,player);
                work.Add(()=>
                {
                    var manager=player.GetSEMan();var live=manager.GetStatusEffect(id);confirmed.Add(id);
                    if(!live)live=manager.AddStatusEffect(id,false,0,0,restored.m_hitVariant);
                    if(!live)throw new System.IO.InvalidDataException("Confirmed native status could not be displayed");
                    GameStatusCodec.Apply(restored,live);
                });
            }
            return ()=>{presenting++;try{foreach(var apply in work)apply();view=next;}finally{presenting--;}};
        }
        [HarmonyPatch(typeof(Player),nameof(Player.Load))]
        private static class Restore
        {
            private static void Postfix(Player __instance)
            {
                if(!PlayerSessionGame.Managed || !initial)return;initial=false;
                var rows=view;view=Array.Empty<PlayerChange>();Presentation(rows,__instance)();
            }
        }
        [HarmonyPatch]
        private static class NativeTimers
        {
            private static IEnumerable<MethodBase> TargetMethods()=>typeof(StatusEffect).Assembly.GetTypes()
                .Where(t=>typeof(StatusEffect).IsAssignableFrom(t)).Select(t=>t.GetMethod(nameof(StatusEffect.UpdateStatusEffect),BindingFlags.Instance|BindingFlags.Public|BindingFlags.DeclaredOnly))
                .Where(m=>m!=null&&!m.IsAbstract);
            private static bool Prefix(StatusEffect __instance)=>simulating>0 || !PlayerSessionGame.Managed || __instance.m_character!=Player.m_localPlayer || !confirmed.Contains(__instance.NameHash());
        }
        [HarmonyPatch]
        private static class SetupResources
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach(var type in new[]{typeof(Character),typeof(Player)})
                    foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                        if(new[]{"Heal","AddEitr","UseHealth","UseEitr","UseStamina","AddStamina","AddAdrenaline","SetHealth"}.Contains(method.Name))yield return method;
            }
            [HarmonyPriority(Priority.First+300)]
            private static bool Prefix(Character __instance)=>presenting==0 || __instance!=Player.m_localPlayer;
        }
    }
}
