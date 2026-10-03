using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Jotunn.Entities;
using Jotunn.Managers;
using UnityEngine;

namespace Overhaul.Storage
{
    internal static class FeedingTrough
    {
        internal const string Name = "Overhaul_FeedingTrough";
        internal const string RequestRpc = "Overhaul_TroughMeal", ReplyRpc = "Overhaul_TroughFed";
        internal const float SearchRange = 4f;
        internal static readonly HashSet<TroughContainer> Loaded = new HashSet<TroughContainer>();
        private sealed class Search
        {
            internal TroughContainer Target;
            internal float NextSearch, NextRequest, Started;
            internal long Ticket, Owner;
        }
        private static readonly ConditionalWeakTable<MonsterAI, Search> Searches = new ConditionalWeakTable<MonsterAI, Search>();
        internal static void Initialize() => PrefabManager.OnVanillaPrefabsAvailable += Register;
        internal static void Shutdown() { PrefabManager.OnVanillaPrefabsAvailable -= Register; Loaded.Clear(); }

        private static void Register()
        {
            if (PrefabManager.Instance.GetPrefab(Name)) return;
            var custom = new CustomPiece(Name, "piece_chest_wood", "_HammerPieceTable");
            Configure(custom.PiecePrefab, PrefabManager.Instance.GetPrefab("Wood").GetComponent<ItemDrop>(),
                PrefabManager.Instance.GetPrefab("blackforge_ext1"), PrefabManager.Instance.GetPrefab("Acorn"), PrefabManager.Instance.GetPrefab("Carrot"),
                PrefabManager.Instance.GetPrefab("Raspberry"), PrefabManager.Instance.GetPrefab("Turnip"));
            PieceManager.Instance.AddPiece(custom);
        }

        internal static void Configure(GameObject prefab, ItemDrop wood, GameObject cooler, GameObject acorn, GameObject carrot, GameObject raspberry, GameObject turnip)
        {
            var piece = prefab.GetComponent<Piece>();
            piece.m_name = "$overhaul_feeding_trough";
            piece.m_description = "$overhaul_feeding_trough_description";
            piece.m_craftingStation = null;
            piece.m_resources = new[] { new Piece.Requirement { m_resItem = wood, m_amount = 10, m_recover = true } };
            var container = prefab.GetComponent<Container>();
            container.m_name = piece.m_name;
            container.m_width = 4; container.m_height = 1;
            container.m_open = null; container.m_closed = null;
            container.m_defaultItems = new DropTable();
            if (!prefab.GetComponent<TroughContainer>()) prefab.AddComponent<TroughContainer>();
            BuildVisual(prefab, cooler, acorn, carrot, raspberry, turnip);
        }

        private static void BuildVisual(GameObject prefab, GameObject cooler, GameObject acorn, GameObject carrot, GameObject raspberry, GameObject turnip)
        {
            if (prefab.transform.Find("Overhaul trough")) return;
            if (!cooler || !cooler.transform.Find("new") || !cooler.transform.Find("collider"))
                throw new InvalidOperationException("Feeding trough requires the black forge cooler model.");
            foreach (var lod in prefab.GetComponentsInChildren<LODGroup>(true)) lod.enabled = false;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true)) renderer.enabled = false;
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true)) collider.enabled = false;

            var root = new GameObject("Overhaul trough");
            root.transform.SetParent(prefab.transform, false);
            root.transform.localScale = cooler.transform.localScale * .8f;
            // Only clone the visual and collider children: no station, network object or forge behavior.
            var model = UnityEngine.Object.Instantiate(cooler.transform.Find("new"), root.transform, false);
            model.name = "Cooler model";
            model.gameObject.SetActive(true);
            UnityEngine.Object.Instantiate(cooler.transform.Find("collider"), root.transform, false);
            // The native Low mesh is the dry basin; High also contains a water submesh.
            // Keep the original GPU mesh intact: some native meshes do not permit CPU access.
            foreach (var lod in model.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.DestroyImmediate(lod);
            model.Find("High").gameObject.SetActive(false);
            model.Find("Low").gameObject.SetActive(true);
            // Meshes only: fixed decoration, with no item, physics or networking components.
            var food = new GameObject("Food decoration").transform;
            food.SetParent(root.transform, false);
            for (int i = 0; i < 6; i++)
                AddFood(food, acorn.transform.Find("acorn"), "Acorns", new Vector3(-.49f+i*.19f,.16f,(i%2==0?-.09f:.09f)), i*73f, .42f);
            for (int i = 0; i < 4; i++)
                AddFood(food, carrot.transform.Find("attach"), "Carrot", new Vector3(-.40f+i*.25f,.18f,(i%2==0?.055f:-.055f)), 75f+i*12f, .57f);
            for (int i = 0; i < 8; i++)
                AddFood(food, raspberry.transform.Find("attach"), "Raspberry", new Vector3(-.52f+i*.145f,.19f,(i%2==0?-.13f:.12f)), i*47f, .38f);
            for (int i = 0; i < 3; i++)
                AddFood(food, turnip.transform.Find("attach"), "Turnip", new Vector3(-.38f+i*.37f,.17f,(i%2==0?-.03f:.055f)), i*113f, .33f);
            CombineFood(food);
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = prefab.layer;
            var wear = prefab.GetComponent<WearNTear>();
            if (wear) wear.m_new = wear.m_worn = wear.m_broken = root;
        }

        private static void AddFood(Transform parent, Transform source, string name, Vector3 position, float yaw, float scale)
        {
            if (!source) throw new InvalidOperationException("Missing feeding trough decoration: " + name);
            var holder = new GameObject(name).transform;
            holder.SetParent(parent, false);
            var visual = UnityEngine.Object.Instantiate(source, holder, false);
            // Keep authored rotation/scale (the carrot is already lying down), then center its visible bounds.
            visual.localPosition = Vector3.zero;
            foreach (var collider in visual.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(collider);
            var renderers = visual.GetComponentsInChildren<MeshRenderer>();
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            var bottom = holder.InverseTransformPoint(new Vector3(bounds.center.x, bounds.min.y, bounds.center.z));
            visual.localPosition -= bottom;
            holder.localPosition = position;
            holder.localRotation = Quaternion.Euler(0, yaw, 0);
            holder.localScale = Vector3.one * scale;
        }
        private static void CombineFood(Transform food)
        {
            // One renderer per native material instead of one per vegetable/seed mesh.
            var groups = new Dictionary<Material, List<CombineInstance>>();
            var combined = new List<GameObject>();
            foreach (var filter in food.GetComponentsInChildren<MeshFilter>())
            {
                if (!filter.sharedMesh.isReadable) continue;
                combined.Add(filter.gameObject);
                var materials = filter.GetComponent<MeshRenderer>().sharedMaterials;
                for (int i = 0; i < materials.Length; i++)
                {
                    if (!groups.TryGetValue(materials[i], out var list))
                        groups.Add(materials[i], list = new List<CombineInstance>());
                    list.Add(new CombineInstance { mesh = filter.sharedMesh, subMeshIndex = i,
                        transform = food.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                }
            }
            var children = new List<GameObject>();
            foreach (Transform child in food) children.Add(child.gameObject);
            foreach (var pair in groups)
            {
                var mesh = new Mesh { name = "Overhaul trough " + pair.Key.name };
                mesh.CombineMeshes(pair.Value.ToArray(), true, true);
                var part = new GameObject(pair.Key.name);
                part.transform.SetParent(food, false);
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                part.AddComponent<MeshRenderer>().sharedMaterial = pair.Key;
            }
            foreach (var child in combined) UnityEngine.Object.DestroyImmediate(child);
        }
        internal static bool Hungry(MonsterAI ai) => ai && ai.m_character &&
            !ai.m_character.IsDead() && ai.m_tamable && ai.m_tamable.IsHungry();
        internal static ItemDrop.ItemData Food(Container container, MonsterAI ai)
        {
            if (!container || container.GetInventory()==null || ai.m_consumeItems==null) return null;
            foreach (var item in container.GetInventory().GetAllItems())
                if (item.m_stack>0 && ai.CanConsume(item)) return item;
            return null;
        }
        internal static bool Available(TroughContainer trough) => trough && trough.Container &&
            trough.gameObject.activeInHierarchy && ChestAccess.Data(trough.Container)!=null &&
            !trough.Container.m_inUse && ChestAccess.Data(trough.Container).GetInt(ZDOVars.s_inUse,0)==0 &&
            !ChestAccess.Leased(trough.Container) && !MoveReservation.Busy(trough.Container);

        private static bool Tick(MonsterAI ai, Humanoid body, float dt)
        {
            if (!ai.m_nview || !ai.m_nview.IsValid() || !ai.m_nview.IsOwner() || !Hungry(ai))
            { Searches.Remove(ai);return false; }
            var search=Searches.GetOrCreateValue(ai);
            if (!Available(search.Target) || (ai.transform.position-search.Target.transform.position).sqrMagnitude>SearchRange*SearchRange ||
                Time.time-search.Started>30f)
            { search.Target=null;search.Ticket=0; }
            if (!search.Target && Time.time>=search.NextSearch)
            {
                search.NextSearch=Time.time+Mathf.Max(1f,ai.m_consumeSearchInterval);
                float closest=SearchRange*SearchRange;
                foreach (var trough in Loaded)
                {
                    if (!Available(trough)) continue;
                    float distance=(ai.transform.position-trough.transform.position).sqrMagnitude;
                    if(distance>closest)continue;
                    trough.Container.Load();
                    if(Food(trough.Container,ai)==null || !ai.HavePath(trough.transform.position))continue;
                    search.Target=trough;closest=distance;
                }
                search.Started=Time.time;search.NextRequest=0;
                if(search.Target)ai.m_consumeTarget=null;
            }
            if(!search.Target)return false;
            var target=search.Target;
            if(ai.MoveTo(dt,target.transform.position,Mathf.Max(1.3f,ai.m_consumeRange),false))
            {
                ai.LookAt(target.transform.position);
                if(ai.IsLookingAt(target.transform.position,20f,false) && Time.time>=search.NextRequest)
                {
                    if(search.Ticket==0)search.Ticket=DateTime.UtcNow.Ticks;
                    search.NextRequest=Time.time+2f;
                    search.Owner=target.Container.m_nview.GetZDO().GetOwner();
                    target.Container.m_nview.InvokeRPC(RequestRpc,ai.m_character.GetZDOID(),search.Ticket);
                }
            }
            return true;
        }

        private static void Fed(MonsterAI ai,long sender,ZDOID troughId,long ticket,string foodName)
        {
            if(!ai || !ai.m_nview || !ai.m_nview.IsValid() || !ai.m_nview.IsOwner() ||
                !Searches.TryGetValue(ai,out var search) || search.Ticket!=ticket || search.Owner!=sender ||
                !search.Target || ChestAccess.Data(search.Target.Container)?.m_uid!=troughId)return;
            search.Ticket=0;search.Target=null;search.NextSearch=Time.time+10f;
            ai.m_consumeTarget=null;
            var prefab=ObjectDB.instance ? ObjectDB.instance.GetItemPrefab(foodName) : null;
            var food=prefab ? prefab.GetComponent<ItemDrop>() : null;
            if(!food || !ai.CanConsume(food.m_itemData))return;
            ai.m_onConsumedItem?.Invoke(food);
            var body=ai.m_character as Humanoid;
            if(body)body.m_consumeItemEffects.Create(ai.transform.position,Quaternion.identity);
            if(ai.m_animator)ai.m_animator.SetTrigger("consume");
        }

        [HarmonyPatch(typeof(MonsterAI), "Awake")]
        private static class AnimalRegister
        {
            private static void Postfix(MonsterAI __instance)
            {
                var ai=__instance;
                if(ai.m_nview && ai.m_nview.IsValid())
                    ai.m_nview.Register<ZDOID,long,string>(ReplyRpc,(sender,id,ticket,food)=>Fed(ai,sender,id,ticket,food));
            }
        }
        [HarmonyPatch(typeof(MonsterAI), "UpdateConsumeItem")]
        private static class Consume
        {
            private static bool Prefix(MonsterAI __instance,Humanoid humanoid,float dt,ref bool __result)
            {
                if(!Tick(__instance,humanoid,dt))return true;
                __result=true;return false;
            }
        }
    }

    internal sealed class TroughContainer : MonoBehaviour
    {
        internal Container Container;
        private const string ReceiptsKey="overhaul_trough_receipts_v1";
        private sealed class Receipt { internal ZDOID Animal;internal long Ticket,Time;internal string Food; }
        private void Start()
        {
            Container=GetComponent<Container>();
            if(!Container || !Container.m_nview || !Container.m_nview.IsValid())return;
            FeedingTrough.Loaded.Add(this);
            Container.m_nview.Register<ZDOID,long>(FeedingTrough.RequestRpc,Request);
        }
        private void OnDestroy() => FeedingTrough.Loaded.Remove(this);

        internal void Request(long sender,ZDOID animalId,long ticket)
        {
            if(!FeedingTrough.Available(this) || !Container.m_nview.IsOwner() || ticket<=0)return;
            var animalObject=ZNetScene.instance?ZNetScene.instance.FindInstance(animalId):null;
            var ai=animalObject?animalObject.GetComponent<MonsterAI>():null;
            if(!ai || !ai.m_nview || !ai.m_nview.IsValid() || ai.m_nview.GetZDO().GetOwner()!=sender ||
                !ai.m_character || ai.m_character.IsDead() ||
                (ai.transform.position-transform.position).sqrMagnitude>Mathf.Pow(Mathf.Max(1.3f,ai.m_consumeRange)+.5f,2))return;
            var data=Container.m_nview.GetZDO();long now=DateTime.UtcNow.Ticks;
            var receipts=ReadReceipts(data,now);
            foreach(var receipt in receipts)
                if(receipt.Animal==animalId && receipt.Ticket==ticket)
                { Reply(ai,sender,ticket,receipt.Food);return; }
            if(!FeedingTrough.Hungry(ai))return;
            // Avoid a second ticket consuming twice before the animal's fed state arrives.
            foreach(var receipt in receipts)if(receipt.Animal==animalId)return;
            Container.Load();var food=FeedingTrough.Food(Container,ai);
            if(food==null || !food.m_dropPrefab)return;
            string name=food.m_dropPrefab.name;
            // Keep all live receipts: defer if the bounded ledger is full.
            if(receipts.Count>=128)return;
            if(!Container.GetInventory().RemoveItem(food,1))return;
            receipts.Add(new Receipt{Animal=animalId,Ticket=ticket,Time=now,Food=name});
            var package=new ZPackage();package.Write(receipts.Count);
            foreach(var receipt in receipts){package.Write(receipt.Animal);package.Write(receipt.Ticket);package.Write(receipt.Time);package.Write(receipt.Food);}
            data.Set(ReceiptsKey,package.GetArray());Container.Save();
            Reply(ai,sender,ticket,name);
        }
        private static List<Receipt> ReadReceipts(ZDO data,long now)
        {
            var result=new List<Receipt>();var bytes=data.GetByteArray(ReceiptsKey,null);
            if(bytes==null)return result;
            var package=new ZPackage(bytes);int count=package.ReadInt();
            if(count<0 || count>128)throw new InvalidOperationException("Invalid trough receipt count");
            for(int i=0;i<count;i++)
            {
                var receipt=new Receipt{Animal=package.ReadZDOID(),Ticket=package.ReadLong(),Time=package.ReadLong(),Food=package.ReadString()};
                if(now-receipt.Time<TimeSpan.FromMinutes(2).Ticks)result.Add(receipt);
            }
            return result;
        }
        private void Reply(MonsterAI ai,long owner,long ticket,string food) =>
            ai.m_nview.InvokeRPC(owner,FeedingTrough.ReplyRpc,Container.m_nview.GetZDO().m_uid,ticket,food);
    }
}



