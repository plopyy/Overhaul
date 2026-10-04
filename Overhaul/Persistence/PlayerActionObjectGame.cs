using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    // Prepare a detached world result; publish native properties only after durable commit.
    internal sealed class PlayerActionObjectGame : IDisposable
    {
        private readonly ZDOID uid;
        private readonly ObjectRecord record;
        private readonly List<PropertyRecord> changed = new List<PropertyRecord>();
        private bool submitted;
        internal PlayerActionObjectGame(ZDO data)
        {
            uid = data.m_uid; record = GamePersistence.ReserveAction(data); data.SetOwner(ZNet.GetUID());
        }
        internal void Set(int key,int value) => Set(key,"int",value);
        internal void Set(int key,float value) => Set(key,"float",value);
        internal void Set(int key,long value) => Set(key,"long",value);
        internal void Set(int key,string value) => Set(key,"string",value);
        private void Set(int key,string type,object value)
        {
            var property = new PropertyRecord { Key = key,Type = type,Name = NameCatalog.Key(key),Value = value };
            record.Properties.RemoveAll(p => p.Key == key && p.Type == type); record.Properties.Add(property);
            changed.RemoveAll(p => p.Key == key && p.Type == type); changed.Add(property);
        }
        internal PlayerActionPlan Finish(PlayerBatch player, Action effects = null)
            => FinishWithObjects(player,Array.Empty<ObjectRecord>(),effects);
        internal PlayerActionPlan FinishWithObjects(PlayerBatch player,IEnumerable<ObjectRecord> additions,Action effects = null)
            => FinishOutput(player,additions,0,effects,null);
        internal PlayerActionPlan FinishWithDelayedObjects(PlayerBatch player,IEnumerable<ObjectRecord> additions,float delay,Action effects,Action outputEffects)
            => FinishOutput(player,additions,delay,effects,outputEffects);
        private PlayerActionPlan FinishOutput(PlayerBatch player,IEnumerable<ObjectRecord> additions,float delay,Action effects,Action outputEffects)
        {
            if (float.IsNaN(delay) || float.IsInfinity(delay) || delay < 0) throw new ArgumentOutOfRangeException(nameof(delay));
            var spawned = additions.ToArray(); var objects = new Dictionary<long,ObjectRecord> { [record.Id] = record };
            foreach (var addition in spawned) objects.Add(addition.Id,addition);
            var change = new PlayerWorldAction(player,objects);
            var properties = changed.ToArray();
            var plan = new PlayerActionPlan(change,() =>
            {
                var data = ZDOMan.instance.GetZDO(uid);
                if (data == null) throw new IOException("Reserved interaction target disappeared before publication");
                foreach (var property in properties)
                    switch (property.Type)
                    {
                        case "int": data.Set(property.Key,(int)property.Value); break;
                        case "float": data.Set(property.Key,(float)property.Value); break;
                        case "long": data.Set(property.Key,(long)property.Value); break;
                        case "string": data.Set(property.Key,(string)property.Value); break;
                    }
                if (delay > 0) GamePersistence.DeferActionObjects(spawned,delay,outputEffects);
                else foreach (var addition in spawned) GamePersistence.PublishActionObject(addition);
                GamePersistence.ReleaseAction(new[] { uid });
                // A missing visual must not turn an already committed inventory change into a storage failure.
                try { effects?.Invoke(); if (delay == 0) outputEffects?.Invoke(); } catch (Exception error) { ZLog.LogError("[Overhaul interaction effects] " + error); }
            });
            submitted = true; return plan;
        }
        public void Dispose() { if (!submitted) GamePersistence.ReleaseAction(new[] { uid }); }
    }
}
