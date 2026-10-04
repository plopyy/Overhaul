using System;
using System.Reflection;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Persistence
{
    // A separate definition prevents the held guard from replacing the normal
    // protective spell. Numeric fields are persisted by GameStatusCodec.
    public sealed class SE_StaffGuard:SE_Shield
    {
        public bool m_guardActive,m_guardBroken;
        public float m_guardCast,m_guardStun;
        public int m_guardStaff;
        public override bool IsDone()=>!m_guardActive&&m_guardCast<=0&&m_guardStun<=0&&!m_guardBroken&&m_damage<=0;
        public override void OnDamaged(HitData hit,Character attacker){if(m_guardActive)base.OnDamaged(hit,attacker);}
    }
    internal static class GameStaffGuardRules
    {
        internal const string Name="Overhaul_StaffGuard";
        internal static readonly int Id=Name.GetStableHashCode();
        private static SE_StaffGuard definition;
        internal static void Initialize()=>PrefabManager.OnVanillaPrefabsAvailable+=Register;
        internal static void Shutdown()=>PrefabManager.OnVanillaPrefabsAvailable-=Register;
        private static void Register()
        {
            if(definition)return;
            var source=PrefabManager.Cache.GetPrefab<SE_Shield>("Staff_shield");
            if(!source){global::Overhaul.Utility.Log.LogError("Staff guard definition is unavailable");return;}
            var guard=CreateDefinition(source);
            if(ItemManager.Instance.AddStatusEffect(new CustomStatusEffect(guard,false)))definition=guard;
            else UnityEngine.Object.Destroy(guard);
        }
        internal static SE_StaffGuard CreateDefinition(SE_Shield source)
        {
            var guard=ScriptableObject.CreateInstance<SE_StaffGuard>();
            for(var type=typeof(SE_Shield);type!=null&&typeof(StatusEffect).IsAssignableFrom(type);type=type.BaseType)
                foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                    if(!field.IsInitOnly)field.SetValue(guard,field.GetValue(source));
            guard.name=Name;guard.m_nameHash=0;guard.m_character=null;guard.m_startEffectInstances=null;
            guard.m_startEffects=new EffectList();guard.m_ttl=0;return guard;
        }
        internal static SE_StaffGuard Begin(SE_StaffGuard previous,SE_StaffGuard template,ItemDrop.ItemData staff)
        {
            if(!staff.IsWeapon()||!staff.m_dropPrefab||!staff.m_dropPrefab.name.StartsWith("Staff",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("A staff is required");
            if(previous!=null&&(previous.m_guardActive||previous.m_guardCast>0||previous.m_guardStun>0||previous.m_guardBroken))throw new InvalidOperationException("Staff guard cannot start");
            var guard=(SE_StaffGuard)(previous??template).Clone();guard.m_startEffectInstances=null;
            if(previous==null){guard.m_damage=0;guard.m_totalAbsorbDamage=200+Mathf.Max(0,staff.m_quality-1)*50;}
            guard.m_guardStaff=staff.m_dropPrefab.name.GetStableHashCode();guard.m_guardCast=.5f;guard.m_guardActive=false;guard.m_time=0;guard.m_ttl=0;return guard;
        }
        internal static void Advance(SE_StaffGuard guard,bool held,double seconds,float regeneration)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0||float.IsNaN(regeneration)||float.IsInfinity(regeneration)||regeneration<0)throw new ArgumentOutOfRangeException(nameof(seconds));
            guard.m_guardStun=Mathf.Max(0,guard.m_guardStun-(float)seconds);
            if(!held)
            {guard.m_guardActive=false;guard.m_guardCast=0;guard.m_guardBroken=false;guard.m_damage=Mathf.Max(0,guard.m_damage-regeneration*(float)seconds);return;}
            if(guard.m_guardBroken||guard.m_guardStun>0){guard.m_guardActive=false;guard.m_guardCast=0;return;}
            if(guard.m_guardCast>0){guard.m_guardCast=Mathf.Max(0,guard.m_guardCast-(float)seconds);if(guard.m_guardCast<=0)guard.m_guardActive=true;}
        }
        internal static void Break(SE_StaffGuard guard)
        {guard.m_guardActive=false;guard.m_guardBroken=true;guard.m_guardCast=0;guard.m_guardStun=1.5f;guard.m_damage=0;}
    }
}
