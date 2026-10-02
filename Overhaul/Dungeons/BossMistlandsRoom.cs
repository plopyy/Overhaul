using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object=UnityEngine.Object;

namespace Overhaul.Dungeons
{
    // Assemble the actual native mine objects, retaining their materials, effects,
    // doors, loot and network components. No replacement artwork is embedded.
    internal static class MistlandsBossRoom
    {
        private static readonly List<DungeonDB.RoomData> retained=new List<DungeonDB.RoomData>();
        internal static void Release(){foreach(var room in retained)room.m_prefab.Release();retained.Clear();}
        internal static GameObject Create(Transform parent)
        {
            var sources=new List<GameObject>();
            foreach(var room in DungeonDB.instance.m_rooms.Where(r=>(r.m_theme&Room.Theme.DvergerTown)!=0))
            {room.m_prefab.Load();retained.Add(room);sources.Add(room.m_prefab.Asset);}
            return Build(parent,sources.ToArray());
        }

        internal static GameObject Build(Transform parent,GameObject[] sources)
        {
            if(parent.gameObject.activeInHierarchy)throw new InvalidOperationException("Mine room templates require an inactive parent");
            var root=new GameObject("Overhaul_Mistlands_BossRoom");root.transform.SetParent(parent,false);
            var room=root.AddComponent<Room>();room.m_theme=Room.Theme.DvergerTown;room.m_size=new Vector3Int(72,32,64);room.m_enabled=true;
            GameObject Source(string name)=>sources.FirstOrDefault(g=>g.name==name)??sources.SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).First(t=>t.name==name).gameObject;
            GameObject Copy(GameObject source,string name,Vector3 pos,Quaternion rot,Vector3? scale=null)
            {
                var copy=Object.Instantiate(source,root.transform,false);
                if(source.GetComponent<ZNetView>())
                {
                    var marker=new GameObject(name);marker.transform.SetParent(root.transform,false);marker.transform.localPosition=pos;marker.transform.localRotation=rot;
                    copy.transform.SetParent(marker.transform,false);copy.name=Utils.GetPrefabName(source);copy.transform.localPosition=Vector3.zero;copy.transform.localRotation=Quaternion.identity;
                }
                else {copy.name=name;copy.transform.localPosition=pos;copy.transform.localRotation=rot;}
                copy.transform.localScale=scale??Vector3.one;
                foreach(var r in copy.GetComponentsInChildren<Room>(true))Object.DestroyImmediate(r);
                foreach(var c in copy.GetComponentsInChildren<RoomConnection>(true))Object.DestroyImmediate(c.gameObject);
                foreach(var random in copy.GetComponentsInChildren<RandomSpawn>(true))
                    if(random.m_OffObject&&!random.m_OffObject.transform.IsChildOf(copy.transform))random.m_OffObject=null;
                return copy;
            }
            var open=Source("dvergr_roombig_open01");room.m_musicPrefab=open.GetComponent<Room>().m_musicPrefab;
            var floor=Source("dvergrtown_floor_large");var block=Source("dvergrtown_2x2x2");
            // Preserve complete native mine bays: floor, water, stone, organic pillars,
            // ceiling membranes, attached eggs and their native particle/light systems.
            // Keep four full corner pillars; open the central and side crossing lanes.
            for(int x=-16;x<=16;x+=16)for(int z=-16;z<=16;z+=16)
            {
                var bay=Copy(open,"NativeBay_"+x+"_"+z,new Vector3(x,5,z),Quaternion.identity);
                var enemies=bay.transform.Find("enemies");if(enemies)Object.DestroyImmediate(enemies.gameObject);
                var mist=bay.transform.Find("MistArea_small");if(mist)Object.DestroyImmediate(mist.gameObject);
                if(x==0||z==0)
                {
                    foreach(string group in new[]{"pillar","creep"}){var t=bay.transform.Find(group);if(t)Object.DestroyImmediate(t.gameObject);}
                }
                // Paired corner growth from adjacent bays otherwise narrows every lane.
                // Keep those native supports only against the outer walls, with both halves.
                var corners=bay.transform.Find("creep (4)");
                if(corners)foreach(Transform group in corners.Cast<Transform>().ToArray())
                {
                    var anchor=group.GetComponentsInChildren<Transform>(true).FirstOrDefault(t=>t.name.StartsWith("CreepProp_pillar"));
                    if(anchor){var p=root.transform.InverseTransformPoint(anchor.position);if(Mathf.Abs(p.x)<20&&Mathf.Abs(p.z)<20)Object.DestroyImmediate(group.gameObject);}
                }
                foreach(Transform part in bay.transform.Find("floor").Cast<Transform>().ToArray())
                {
                    var p=root.transform.InverseTransformPoint(part.position);
                    if(part.name.StartsWith("dvergrtown_2x2x2")&&p.y>0&&Mathf.Abs(p.x)<20&&Mathf.Abs(p.z)<20)Object.DestroyImmediate(part.gameObject);
                }
                // All nest attachments move with the raised rear vault, never alone.
                if(z==16)
                {
                    bay.transform.Find("roof").localPosition+=Vector3.up*3;
                    bay.transform.Find("hangingeggs").localPosition+=Vector3.up*3;
                    foreach(var t in bay.GetComponentsInChildren<Transform>(true))
                        if(t.name.StartsWith("CreepProp_pillar")&&bay.transform.InverseTransformPoint(t.position).y>0)t.localPosition+=t.parent.InverseTransformVector(Vector3.up*3);
                    foreach(Transform t in bay.transform)if(t.name.StartsWith("CreepProp_drops"))t.localPosition+=Vector3.up*3;
                    if(x!=0)
                    {
                        // Native capitals remain attached to their roof; extend only the shaft.
                        var pillar=bay.transform.Find("pillar");
                        foreach(Transform t in pillar)if(pillar.InverseTransformPoint(t.position).y>5)t.localPosition+=Vector3.up*3;
                        foreach(float y in new[]{8f,10f})Copy(block,"RearPillarShaft_"+x+"_"+y,new Vector3(x,y,z),Quaternion.identity);
                    }
                }
                // Native random variants are kept; the structural infestation itself is fixed.
                foreach(var random in bay.GetComponentsInChildren<RandomSpawn>(true))
                    if(!random.GetComponent<ZNetView>()){random.m_chanceToSpawn=100;}
            }
            // Original large-room rock/ruin endcaps, with original small-room gateways.
            for(int edge=0;edge<4;edge++)for(int offset=-16;offset<=16;offset+=16)
            {
                var rot=Quaternion.Euler(0,edge*90,0);var origin=rot*new Vector3(offset,5,-24);
                if(offset!=0||edge==2)
                    Copy(Source("dvergr_roombig_endcap02"),"NativeBoundary_"+edge+"_"+offset,origin,rot);
                else
                {
                    Copy(Source("dvergrtown_gate"),"NativeGateway_"+edge,rot*new Vector3(0,0,-24),rot);
                    foreach(int side in new[]{-8,8})Copy(Source("dvergr_room_endcap01"),"GateSide_"+edge+"_"+side,rot*new Vector3(side,2,-24),rot);
                }
                // Backing is behind the native endcap silhouette, sealing the upper reserve.
                for(int dx=-7;dx<=7;dx+=2)for(int y=1;y<=13;y+=2)
                {
                    if(offset==0&&edge!=2&&Math.Abs(dx)<3&&y<5)continue;
                    Copy(block,"BoundaryBacking_"+edge+"_"+offset+"_"+dx+"_"+y,rot*new Vector3(offset+dx,y,-25),rot);
                }
            }
            // A native roof layer closes any seams above the irregular visible rock vault.
            for(int x=-20;x<=20;x+=8)for(int z=-20;z<=20;z+=8)
                Copy(floor,"VaultBacking_"+x+"_"+z,new Vector3(x,14.5f,z),Quaternion.Euler(0,0,180),new Vector3(1,.5f,1));
            Copy(Source("dvergr_corridor01"),"NativeEntranceCorridor",new Vector3(0,2,-28),Quaternion.identity);
            Copy(Source("dvergr_room_endcap01"),"EntranceSideClosure",new Vector3(4,2,-28),Quaternion.Euler(0,90,0));
            Copy(Source("dvergr_room_TREASURE"),"SecretRoomLeft",new Vector3(-28,2,0),Quaternion.identity);
            Copy(Source("dvergr_room_TREASURE2"),"SecretRoomRight",new Vector3(28,2,0),Quaternion.Euler(0,180,0));
            // Small chest-only dais at the rear: native blocks, four metres square.
            foreach(int x in new[]{-1,1})foreach(int z in new[]{20,22})Copy(block,"ChestDais_"+x+"_"+z,new Vector3(x,1,z),Quaternion.identity);
            foreach(int x in new[]{-1,1})for(int step=0;step<2;step++)
                Copy(Source("dvergrtown_stair"),"DaisStair_"+x+"_"+step,new Vector3(x,step,16+step*2),Quaternion.Euler(0,180,0));
            foreach(int x in new[]{-1,1})Copy(Source("dvergrtown_2x2x1"),"StairBacking_"+x,new Vector3(x,.5f,18),Quaternion.Euler(90,0,0));
            // Royal jelly piles are the native Pickable, including networked collected state.
            foreach(int side in new[]{-1,1})Copy(Source("Pickable_RoyalJelly"),"SecretJelly_"+side,new Vector3(side*28,.05f,-1),Quaternion.identity);
            void Marker(string name,Vector3 pos){var go=new GameObject(name);go.transform.SetParent(root.transform,false);go.transform.localPosition=pos;}
            Marker("BossSpawn",new Vector3(0,.1f,7));Marker("RewardChestSpawn",new Vector3(0,2.05f,21));
            var entrance=new GameObject("EntranceConnection");entrance.transform.SetParent(root.transform,false);entrance.transform.localPosition=new Vector3(0,0,-32);
            var connection=entrance.AddComponent<RoomConnection>();connection.m_type="dvergr";connection.m_allowDoor=false;
            foreach(var pickable in root.GetComponentsInChildren<Pickable>(true))
                if(pickable.name.StartsWith("Pickable_RoyalJelly"))
                {
                    var p=root.transform.InverseTransformPoint(pickable.transform.position);
                    // Keep collectible piles out of the guardian's broad walking lanes.
                    if(Mathf.Abs(p.x)<20){if(Mathf.Abs(p.x)<8)p.z+=3;p.x=p.x<0?-21:21;}
                    p.y=.05f;pickable.transform.position=root.transform.TransformPoint(p);
                }
            return root;
        }
    }
}
