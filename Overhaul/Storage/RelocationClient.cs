using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class RelocationClient
    {
        internal static Piece Target;
        static Player player;
        static GameObject ghost, tableObject, oldGhost;
        static PieceTable table;
        static RelocationGuide guide;
        static Vector3 origin;
        static int revision, request, sequence, startFrame, rotation, originalRotation, originalSnap;
        static float pendingSince;
        static bool pending, inNative;
        static bool acquiring, prepared;
        static string leaseToken;
        static float leaseRequestAt;
        static int closedFrame=-1;
        static bool suppressHeldControls;
        static string previousGhostName;
        static readonly Dictionary<Material,float> previousRipple=new Dictionary<Material,float>();
        static readonly HashSet<Material> ghostMaterials=new HashSet<Material>();
        internal static bool Active=>Target;
        internal static bool HasHammer(Player p) => p && p.m_rightItem?.m_dropPrefab
            && Utils.GetPrefabName(p.m_rightItem.m_dropPrefab) == "Hammer";
        internal static bool IdleInput()=>Player.m_localPlayer && !Console.IsVisible() && !InventoryGui.IsVisible() &&
            !Menu.IsVisible() && !TextInput.IsVisible() && !Hud.IsPieceSelectionVisible() && !(Chat.instance&&Chat.instance.HasFocus()) &&
            !(Minimap.instance&&Minimap.instance.m_mode==Minimap.MapMode.Large);
        internal static Piece Hover(Player p)
        {
            if(!HasHammer(p)||!GameCamera.instance)return null;
            var camera=GameCamera.instance.transform;
            if(!Physics.Raycast(camera.position,camera.forward,out var hit,50,p.m_removeRayMask,QueryTriggerInteraction.Ignore)||
                Vector3.Distance(p.m_eye.position,hit.point)>PieceRelocation.Reach)return null;
            var piece=hit.collider.GetComponentInParent<Piece>();
            return PieceRelocation.Eligible(piece)?piece:null;
        }
        static void Message(string key){if(player)player.Message(MessageHud.MessageType.Center,key);}
        internal static void Begin(Player p,Piece piece)
        {
            if(!HasHammer(p))return;
            if(Active||BuildStorage.Busy||StationStorage.Busy||!piece||p.IsDead()||p.IsAttached()||p.IsTeleporting()||p.InAttack()||p.IsDrawingBow()||!PieceRelocation.Access(piece,p.GetPlayerID())){p.Message(MessageHud.MessageType.Center,"$overhaul_move_denied");return;}
            player=p;Target=piece;origin=piece.transform.position;revision=piece.m_nview.GetZDO().GetInt(PieceRelocation.RevisionKey,0);
            leaseToken=Guid.NewGuid().ToString("N");acquiring=true;prepared=false;pending=false;startFrame=Time.frameCount;
            Lease(false);
        }
        static void Lease(bool release)
        {
            if(!Target||!Target.m_nview||!Target.m_nview.IsValid()||!player||string.IsNullOrEmpty(leaseToken))return;
            leaseRequestAt=Time.realtimeSinceStartup;
            if(Target.m_nview.IsOwner())
            {
                bool ok=MoveReservation.Change(Target,ZNet.GetUID(),player.GetZDOID(),leaseToken,release);
                if(!release)Reserved(Target,leaseToken,ok);
            }
            else
            {
                var packet=new ZPackage();packet.Write(player.GetZDOID());packet.Write(leaseToken);packet.Write(release);
                Target.m_nview.InvokeRPC(MoveReservation.Rpc,packet);
            }
        }
        internal static void Reserved(Piece piece,string token,bool ok)
        {
            if(Target!=piece||leaseToken!=token)return;
            if(!ok){Message("$overhaul_move_denied");End();return;}
            if(!acquiring)return;
            acquiring=false;Prepare();
        }
        static void Prepare()
        {
            var p=player;var piece=Target;
            var prefab=ZNetScene.instance.GetPrefab(piece.m_nview.GetZDO().GetPrefab());
            if(!prefab||!prefab.GetComponent<Piece>()){End();return;}
            prepared=true;
            oldGhost=p.m_placementGhost;if(oldGhost)oldGhost.SetActive(false);
            previousGhostName=p.m_placementGhostLast;previousRipple.Clear();
            foreach(var entry in p.m_ghostRippleDistance)previousRipple[entry.Key]=entry.Value;
            originalRotation=p.m_placeRotation;originalSnap=p.m_manualSnapPoint;
            rotation=Mathf.RoundToInt(piece.transform.eulerAngles.y/p.m_placeRotationDegrees);
            tableObject=new GameObject("Overhaul relocation table");tableObject.SetActive(false);table=tableObject.AddComponent<PieceTable>();
            table.m_selectedCategory=Piece.PieceCategory.Misc;table.m_selectedPiece=new Vector2Int[9];
            table.m_pieces=new List<GameObject>{prefab};table.m_availablePiecesByCategory.Clear();
            for(int i=0;i<9;i++)table.m_availablePiecesByCategory.Add(new List<Piece>{prefab.GetComponent<Piece>()});
            try
            {
                WithNative(()=>{player.m_placementGhost=null;player.SetupPlacementGhost();ghost=player.m_placementGhost;});
                if(!ghost)throw new InvalidOperationException("Placement ghost unavailable");
                var originals=new HashSet<Material>();
                foreach(var renderer in prefab.GetComponentsInChildren<Renderer>(true))foreach(var material in renderer.sharedMaterials)originals.Add(material);
                foreach(var renderer in ghost.GetComponentsInChildren<Renderer>(true))foreach(var material in renderer.sharedMaterials)
                    if(material&&!originals.Contains(material))ghostMaterials.Add(material);
                // Native setup disables network initialization. Disable remaining behaviours
                // that could run production or interactions in a preview without a valid ZDO.
                foreach(var behaviour in ghost.GetComponentsInChildren<MonoBehaviour>(true))
                    if(!(behaviour is Piece)&&!(behaviour is StationExtension))behaviour.enabled=false;
                guide=new RelocationGuide();startFrame=Time.frameCount;pending=false;
            }
            catch(Exception error){Debug.LogWarning("[Overhaul] Relocation preview: "+error.Message);End();}
        }
        static void WithNative(Action action)
        {
            var previousTable=player.m_buildPieces;var previousGhost=player.m_placementGhost;
            bool init=ZNetView.m_forceDisableInit,terrain=TerrainOp.m_forceDisableTerrainOps;
            inNative=true;
            var hidden=new List<Collider>();
            try
            {
                // Ignore the source only during synchronous placement queries. Keep its
                // world physics, production and network state intact between queries.
                if(Target)foreach(var collider in Target.GetComponentsInChildren<Collider>(true))
                    if(collider.enabled){hidden.Add(collider);collider.enabled=false;}
                player.m_buildPieces=table;player.m_placementGhost=ghost;player.m_placeRotation=rotation;action();
            }
            finally
            {
                foreach(var collider in hidden)if(collider)collider.enabled=true;
                player.m_buildPieces=previousTable;player.m_placementGhost=previousGhost;
                ZNetView.m_forceDisableInit=init;TerrainOp.m_forceDisableTerrainOps=terrain;inNative=false;
            }
        }
        internal static void End()
        {
            if(Active){closedFrame=Time.frameCount;suppressHeldControls=true;}
            Lease(true);
            if(ghost)
            {
                RelocationGuide.Release(ghost);
            }
            foreach(var material in ghostMaterials)RelocationGuide.Release(material);ghostMaterials.Clear();
            if(player&&prepared)
            {
                player.m_placeRotation=originalRotation;player.m_manualSnapPoint=originalSnap;player.m_placementGhostLast=previousGhostName;
                player.m_ghostRippleDistance.Clear();foreach(var entry in previousRipple)player.m_ghostRippleDistance[entry.Key]=entry.Value;
                if(player.m_placementMarkerInstance)player.m_placementMarkerInstance.SetActive(false);
            }
            previousRipple.Clear();
            RelocationGuide.Release(tableObject);
            guide?.Dispose();guide=null;ghost=null;table=null;tableObject=null;Target=null;pending=false;acquiring=false;prepared=false;leaseToken=null;
            if(oldGhost)oldGhost.SetActive(false);oldGhost=null;player=null;
        }
        internal static void Reply(Piece piece,int id,bool ok)
        {
            if(!Active||Target!=piece||!pending||id!=request)return;
            pending=false;
            Message(ok?"$overhaul_move_done":"$overhaul_move_denied");End();
        }
        static void Tick(bool takeInput)
        {
            if(!HasHammer(player)||player!=Player.m_localPlayer||!Target||!Target.m_nview||!Target.m_nview.IsValid()||
                player.IsDead()||player.IsTeleporting()||player.IsAttached()||!PieceRelocation.Near(Target,player.transform.position)) {End();return;}
            if(acquiring)
            {
                if(Time.realtimeSinceStartup-leaseRequestAt>10){Message("$overhaul_move_timeout");End();}
                else if(!IdleInput()||ZInput.GetKeyDown(KeyCode.Escape)||ZInput.GetButtonDown("Block")||(Time.frameCount!=startFrame&&RelocationShortcut.Down))
                {End();}
                return;
            }
            if(Time.realtimeSinceStartup-leaseRequestAt>3){Lease(false);if(!Active)return;}
            if(pending)
            {
                // A lost acknowledgement is resolved from the persistent revision.
                if(Target.m_nview.GetZDO().GetInt(PieceRelocation.RevisionKey,0)!=revision){End();return;}
                if(Time.realtimeSinceStartup-pendingSince>10){Message("$overhaul_move_timeout");End();}return;
            }
            if(Target.m_nview.GetZDO().GetInt(PieceRelocation.RevisionKey,0)!=revision){Message("$overhaul_move_denied");End();return;}
            if(!IdleInput()){End();return;}
            if(Time.frameCount!=startFrame&&(RelocationShortcut.Down||ZInput.GetKeyDown(KeyCode.Escape)||ZInput.GetButtonDown("Block")))
            {End();return;}
            if(!takeInput)return;
            float scroll=ZInput.GetMouseScrollWheel();if(Mathf.Abs(scroll)>.001f)rotation+=scroll>0?1:-1;
            WithNative(()=>player.UpdatePlacementGhost(false));
            bool valid=ghost.activeSelf&&player.m_placementStatus==Player.PlacementStatus.Valid;
            if(valid)valid=Vector3.Distance(player.transform.position,ghost.transform.position)<=PieceRelocation.Reach&&
                PieceRelocation.Destination(Target,ghost.transform.position,ghost.transform.rotation,player.GetPlayerID());
            WithNative(()=>player.SetPlacementGhostValid(valid));
            guide.Update(origin,ghost.transform.position,ghost.activeSelf);
            if(Time.frameCount==startFrame||!ZInput.GetButtonDown("Attack"))return;
            if(!valid){Message("$overhaul_move_invalid");return;}
            request=++sequence;pending=true;pendingSince=Time.realtimeSinceStartup;
            if(Target.m_nview.IsOwner())
            {
                bool ok=PieceRelocation.Commit(Target,ZNet.GetUID(),player.GetZDOID(),origin,revision,ghost.transform.position,ghost.transform.rotation,leaseToken);
                Reply(Target,request,ok);
            }
            else
            {
                var packet=new ZPackage();packet.Write(player.GetZDOID());packet.Write(request);packet.Write(origin);packet.Write(revision);
                packet.Write(ghost.transform.position);packet.Write(ghost.transform.rotation);packet.Write(leaseToken);Target.m_nview.InvokeRPC(PieceRelocation.RequestRpc,packet);
            }
        }
        [HarmonyPatch(typeof(Player),"UpdatePlacement")]
        private static class Placement
        {
            private static bool Prefix(Player __instance,bool takeInput)
            {
                if(__instance!=Player.m_localPlayer)return true;
                if(!Active&&takeInput&&IdleInput()&&RelocationShortcut.Down)
                {var target=Hover(__instance);if(target){Begin(__instance,target);}}
                if(!Active){if(player)End();return true;}
                Tick(takeInput);return false;
            }
        }
        [HarmonyPatch(typeof(Player),"UpdatePlacementGhost")]
        private static class GhostUpdate {private static bool Prefix(Player __instance)=>__instance!=Player.m_localPlayer||!Active||inNative;}
        [HarmonyPatch(typeof(Player),"SetupPlacementGhost")]
        private static class GhostSetup {private static bool Prefix(Player __instance)=>__instance!=Player.m_localPlayer||!Active||inNative;}
        [HarmonyPatch(typeof(Player),nameof(Player.SetControls))]
        private static class Controls
        {
            private static void Prefix(Player __instance,ref bool attack,ref bool attackHold,ref bool secondaryAttack,ref bool secondaryAttackHold,ref bool block,ref bool blockHold)
            {
                if(__instance!=Player.m_localPlayer)return;
                if(Active||closedFrame==Time.frameCount)suppressHeldControls=true;
                else if(!(attack||attackHold||secondaryAttack||secondaryAttackHold||block||blockHold))suppressHeldControls=false;
                if(suppressHeldControls)attack=attackHold=secondaryAttack=secondaryAttackHold=block=blockHold=false;
            }
        }
        [HarmonyPatch(typeof(Menu),"Update")]
        private static class MenuInput {private static bool Prefix()=>!Active&&closedFrame!=Time.frameCount;}
        [HarmonyPatch(typeof(StationExtension),nameof(StationExtension.OtherExtensionInRange))]
        private static class ExtensionSpace
        {
            private static bool Prefix(StationExtension __instance,float radius,ref bool __result)
            {
                if(!inNative||!Target)return true;
                __result=false;
                foreach(var extension in StationExtension.m_allExtensions)
                    if(extension&&extension!=__instance&&!extension.transform.IsChildOf(Target.transform)&&
                        Vector3.Distance(extension.transform.position,__instance.transform.position)<radius){__result=true;break;}
                return false;
            }
        }
        [HarmonyPatch(typeof(Player),"Interact")]
        private static class Interact {private static bool Prefix(Player __instance)=>__instance!=Player.m_localPlayer||!Active;}
        [HarmonyPatch(typeof(Player),"OnDestroy")]
        private static class Destroy {private static void Prefix(Player __instance){if(__instance==player)End();}}
        [HarmonyPatch(typeof(Hud),"UpdateBuild")]
        private static class BuildMenu
        {
            private static bool Prefix(Hud __instance)
            {if(!Active)return true;__instance.m_buildUi.Close();__instance.m_buildHud.SetActive(false);return false;}
            [HarmonyPriority(Priority.Last)]
            private static void Postfix()
            {if(Active&&MessageHud.instance&&MessageHud.instance.m_messageCenterText)MessageHud.instance.m_messageCenterText.gameObject.SetActive(true);}
        }
        [HarmonyPatch(typeof(Hud),"UpdateCrosshair")]
        private static class Hint
        {
            private static void Postfix(Hud __instance,Player player)
            {
                if(!__instance.m_hoverName||!IdleInput()||!HasHammer(player))return;
                if(Active)__instance.m_hoverName.text=(pending||acquiring?Localization.instance.Localize("$overhaul_move_wait"):RelocationShortcut.ControlsText());
                else if(Hover(player))__instance.m_hoverName.text+=Localization.instance.Localize("\n[<color=yellow>"+RelocationShortcut.Label+"</color>] $overhaul_move");
            }
        }
        [HarmonyPatch(typeof(GameCamera),"UpdateCamera")]
        private static class RotationScroll
        {
            private static void Prefix(GameCamera __instance,out float __state)
            {__state=__instance.m_zoomSens;if(Active)__instance.m_zoomSens=0;}
            private static void Finalizer(GameCamera __instance,float __state)=>__instance.m_zoomSens=__state;
        }
    }
}
