using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace Overhaul.Persistence
{
    // Marker names/checks are user annotations. Exploration remains server-derived.
    internal static class GameMapPins
    {
        private static readonly Dictionary<Minimap.PinData,string> ids=new Dictionary<Minimap.PinData,string>();
        private static readonly List<PlayerChange> pending=new List<PlayerChange>();
        private static readonly Dictionary<string,PlayerChange> saved=new Dictionary<string,PlayerChange>();
        private static readonly Dictionary<string,string> authors=new Dictionary<string,string>();
        private static int loading;
        internal static void Initial(PlayerSnapshot state)
        {ids.Clear();pending.Clear();saved.Clear();authors.Clear();if(state!=null)Confirm(state.Rows.ToArray());}
        internal static void Confirm(PlayerChange[] rows)
        {
            foreach(var row in rows)
            {
                var v=row.Values;
                if(row.Table=="pins"){string id=(string)v[0];if(row.Delete)saved.Remove(id);else saved[id]=row;}
                else if(row.Table=="knowledge"&&(string)v[0]=="map_pin_author"){string id=(string)v[1];if(row.Delete)authors.Remove(id);else authors[id]=(string)v[2];}
            }
        }
        internal static void ApplyShared(PlayerChange[] rows)
        {
            var map=Minimap.instance;if(!map)return;loading++;
            try
            {
                foreach(var row in rows.Where(r=>r.Table=="pins"))
                {
                    var v=row.Values;var pin=ids.FirstOrDefault(p=>p.Value==(string)v[0]).Key;
                    if(row.Delete){if(pin!=null){map.RemovePin(pin);ids.Remove(pin);}continue;}
                    if(pin!=null)continue;
                    authors.TryGetValue((string)v[0],out string author);
                    pin=map.AddPin(new Vector3(Convert.ToSingle(v[3]),Convert.ToSingle(v[4]),Convert.ToSingle(v[5])),(Minimap.PinType)Convert.ToInt32(v[1]),(string)v[2],true,Convert.ToBoolean(v[6]),Convert.ToInt64(v[7]),string.IsNullOrEmpty(author)?default(PlatformUserID):new PlatformUserID(author));ids[pin]=(string)v[0];
                }
            }
            finally{loading--;}
        }
        private static void Add(PlayerChange row){pending.RemoveAll(old=>old.SameKey(row));pending.Add(row);}
        internal static void Changed(Minimap.PinData pin)
        {
            if(loading!=0||!PlayerSessionGame.Managed||pin==null||!pin.m_save)return;
            if(!ids.TryGetValue(pin,out var id)){id=Guid.NewGuid().ToString("N");ids.Add(pin,id);}
            Add(new PlayerChange("pins",false,id,(int)pin.m_type,pin.m_name??"",pin.m_pos.x,pin.m_pos.y,pin.m_pos.z,pin.m_checked,pin.m_ownerID));
            Add(new PlayerChange("knowledge",false,"map_pin_author",id,pin.m_author.ToString()));
        }
        internal static void Tick()
        {
            if(!PlayerSessionGame.Managed||InventoryMoveGame.Client==null||pending.Count==0)return;
            var rows=pending.Take(64).ToArray();InventoryMoveGame.Client.MapEdit(rows);pending.RemoveRange(0,rows.Length);
        }
        internal static void Flush()
        {while(PlayerSessionGame.Managed&&InventoryMoveGame.Client!=null&&pending.Count!=0)Tick();}
        [HarmonyPatch(typeof(Minimap),"SetMapData")]
        private static class Load
        {
            private static void Prefix(){loading++;}
            private static void Finalizer(Minimap __instance)
            {
                try
                {
                    if(!PlayerSessionGame.Managed)return;
                    ids.Clear();foreach(var pin in __instance.m_pins.Where(p=>p.m_save).ToArray())__instance.RemovePin(pin);
                    foreach(var pair in saved.OrderBy(p=>p.Key,StringComparer.Ordinal))
                    {
                        var v=pair.Value.Values;authors.TryGetValue(pair.Key,out string author);
                        var pin=__instance.AddPin(new Vector3(Convert.ToSingle(v[3]),Convert.ToSingle(v[4]),Convert.ToSingle(v[5])),(Minimap.PinType)Convert.ToInt32(v[1]),(string)v[2],true,Convert.ToBoolean(v[6]),Convert.ToInt64(v[7]),string.IsNullOrEmpty(author)?default(PlatformUserID):new PlatformUserID(author));ids[pin]=pair.Key;
                    }
                }
                finally{loading--;}
            }
        }
        [HarmonyPatch(typeof(Minimap),nameof(Minimap.OnTogglePublicPosition))]
        private static class PublicPosition
        {private static void Postfix(Minimap __instance){if(PlayerSessionGame.Managed&&loading==0)Add(new PlayerChange("state",false,"map_public",__instance.m_publicPosition.isOn,null,null,null));}}
        [HarmonyPatch(typeof(Minimap),nameof(Minimap.AddPin))]
        private static class Created{private static void Postfix(Minimap.PinData __result)=>Changed(__result);}
        [HarmonyPatch(typeof(Minimap),nameof(Minimap.RemovePin),new[]{typeof(Minimap.PinData)})]
        private static class Removed
        {
            private static void Prefix(Minimap.PinData pin)
            {if(loading!=0||!PlayerSessionGame.Managed||pin==null||!ids.TryGetValue(pin,out var id))return;ids.Remove(pin);Add(new PlayerChange("pins",true,id));Add(new PlayerChange("knowledge",true,"map_pin_author",id));}
        }
        // Hook the actual assignment, including keyboard/gamepad paths. Avoid
        // rescanning every saved marker every frame to detect a single checkbox.
        [HarmonyPatch]
        private static class Edited
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach(var method in typeof(Minimap).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                    if(method.Name=="OnPinTextEntered"||method.Name=="OnMapLeftClick"||method.Name=="UpdateMap")yield return method;
            }
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> codes)
            {
                foreach(var code in codes)
                {
                    if(code.opcode==OpCodes.Stfld&&code.operand is FieldInfo field&&field.DeclaringType==typeof(Minimap.PinData)&&(field.Name=="m_checked"||field.Name=="m_name"||field.Name=="m_ownerID"))
                    {
                        string method=field.Name=="m_checked"?nameof(Check):field.Name=="m_name"?nameof(Name):nameof(Owner);
                        yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(GameMapPins),method)){labels=code.labels,blocks=code.blocks};
                    }
                    else yield return code;
                }
            }
        }
        private static void Check(Minimap.PinData pin,bool value){pin.m_checked=value;Changed(pin);}
        private static void Name(Minimap.PinData pin,string value){pin.m_name=value;Changed(pin);}
        private static void Owner(Minimap.PinData pin,long value){pin.m_ownerID=value;Changed(pin);}
    }
}
