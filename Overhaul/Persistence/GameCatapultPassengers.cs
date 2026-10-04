using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal static class GameCatapultPassengers
    {
        private sealed class Passenger { internal Catapult Machine; internal Player Player; internal bool Kinematic; }
        private static readonly Dictionary<ZDOID,Passenger> passengers=new Dictionary<ZDOID,Passenger>();
        private static Catapult collecting;
        internal static void Forget(ZDOID actor)
        {
            if(!passengers.TryGetValue(actor,out var passenger))return;
            passengers.Remove(actor);
            if(passenger.Machine)passenger.Machine.m_launchCharacters.Remove(passenger.Player);
            if(passenger.Player){passenger.Player.ReleaseTempParent();if(passenger.Player.m_body)passenger.Player.m_body.isKinematic=passenger.Kinematic;}
        }
        internal static bool Hold(Player player)
        {
            if(!passengers.TryGetValue(player.GetZDOID(),out var passenger))return false;
            if(!passenger.Machine||!passenger.Machine.isActiveAndEnabled||player.IsDead()||player.IsTeleporting())
            {Forget(player.GetZDOID());return false;}
            if(player.m_body)player.m_body.position=player.transform.position;
            GameMovementRuntime.Record(player);return true;
        }
        [HarmonyPatch(typeof(Catapult),"CollectLaunchCharacters")]
        private static class Collect
        {
            private static bool Prefix(Catapult __instance,out Catapult __state)
            {
                __state=collecting;
                if(!GameCreatureAuthority.Enabled&&!PlayerSessionGame.Managed)return true;
                foreach(var passenger in __instance.m_launchCharacters.ToArray())
                    if(passenger is Player player)Forget(player.GetZDOID());else if(passenger)passenger.ReleaseTempParent();
                __instance.m_launchCharacters.Clear();
                if(!GameCreatureAuthority.Enabled)return false;
                collecting=__instance;return true;
            }
            private static void Finalizer(Catapult __state){collecting=__state;}
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var owner=AccessTools.Method(typeof(ZNetView),nameof(ZNetView.IsOwner));
                foreach(var instruction in instructions)
                {
                    if(instruction.Calls(owner)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(GameCatapultPassengers),nameof(CanCollect));}
                    yield return instruction;
                }
            }
        }
        private static bool CanCollect(ZNetView view)
        {
            if(!collecting)return view.IsOwner();
            var character=view.GetComponent<Character>();
            if(!character||character.IsDead()||collecting.m_launchCharacters.Contains(character))return false;
            if(!(character is Player player))return view.IsOwner();
            if(!GameMovementRuntime.Managed(player)||!player.m_body||player.InIntro()||player.IsTeleporting()||player.IsAttached()||passengers.ContainsKey(player.GetZDOID()))return false;
            passengers.Add(player.GetZDOID(),new Passenger{Machine=collecting,Player=player,Kinematic=player.m_body.isKinematic});
            player.m_body.isKinematic=true;return true;
        }
        [HarmonyPatch(typeof(Catapult),"LaunchCharacters")]
        private static class Launch
        {
            private static bool Prefix(Catapult __instance,out Player[] __state)
            {
                __state=null;
                if(!GameCreatureAuthority.Enabled&&!PlayerSessionGame.Managed)return true;
                if(!GameCreatureAuthority.Enabled){__instance.m_launchCharacters.Clear();return false;}
                __instance.m_launchCharacters.RemoveAll(character=>!character||character.IsDead());
                __state=__instance.m_launchCharacters.OfType<Player>().ToArray();
                foreach(var player in __state)
                {
                    if(!passengers.TryGetValue(player.GetZDOID(),out var passenger))continue;
                    passengers.Remove(player.GetZDOID());
                    if(player.m_body)player.m_body.isKinematic=passenger.Kinematic;
                }
                return true;
            }
            private static void Postfix(Player[] __state){if(__state!=null)foreach(var player in __state)if(player)GameMovementRuntime.Record(player);}
        }
    }
}
