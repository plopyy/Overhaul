using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using SoftReferenceableAssets;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class OverhaulBossChecks
{
	private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

	private static Type layout;

	private static Type encounter;

	private static int checks;

	private static readonly Dictionary<string, string> paths = new Dictionary<string, string>();

	private static readonly Dictionary<string, List<string>> dependencies = new Dictionary<string, List<string>>();

	private static readonly Dictionary<string, AssetBundle> bundles = new Dictionary<string, AssetBundle>();

	private const string Reference = "D:/Valheim/valheim_Data/StreamingAssets/SoftRef/";

	private static object Call(Type t, string method, params object[] args)
	{
		return t.GetMethod(method, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Invoke(null, args);
	}

	private static void Check(bool ok, string message)
	{
		if (!ok)
		{
			throw new Exception(message);
		}
		checks++;
	}

	private static void Load(string id)
	{
		if (bundles.ContainsKey(id))
		{
			return;
		}
		bundles[id] = null;
		if (dependencies.ContainsKey(id))
		{
			foreach (string item in dependencies[id])
			{
				Load(item);
			}
		}
		bundles[id] = AssetBundle.LoadFromFile("D:/Valheim/valheim_Data/StreamingAssets/SoftRef/Bundles/" + id);
		if ((bool)bundles[id])
		{
			return;
		}
		throw new Exception(id);
	}

	private static GameObject Get(string path)
	{
		Load(paths[path]);
		return bundles[paths[path]].LoadAsset<GameObject>(path);
	}

	public static async Task Run(Assembly plugin)
	{
		layout = plugin.GetType("Overhaul.Dungeons.BossDungeonLayout", throwOnError: true);
		encounter = plugin.GetType("Overhaul.Dungeons.BossEncounter", throwOnError: true);
		string text = null;
		string value = null;
		bool flag = false;
		foreach (string item in File.ReadAllLines("D:/Valheim/valheim_Data/StreamingAssets/SoftRef/manifest_extended").Concat(File.ReadAllLines("D:/Valheim/valheim_Data/StreamingAssets/SoftRef/manifest")))
		{
			if (item == "bundle dependencies:")
			{
				flag = false;
			}
			else if (item.StartsWith("asset locations:"))
			{
				flag = true;
			}
			else if (!flag)
			{
				if (item.StartsWith("- bundle: "))
				{
					text = item.Substring(10).Trim();
					dependencies[text] = new List<string>();
				}
				else if (text != null && item.StartsWith("  - "))
				{
					dependencies[text].Add(item.Substring(4).Trim());
				}
			}
			else if (item.StartsWith("  bundle: "))
			{
				value = item.Substring(10).Trim();
			}
			else if (item.StartsWith("  path in bundle: "))
			{
				paths[item.Substring(18).Trim()] = value;
			}
		}
		object oldLoader = typeof(Runtime).GetField("s_assetLoader", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
		DungeonTestAssets assets = new DungeonTestAssets();
		typeof(Runtime).GetField("s_assetLoader", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, assets);
		DungeonDB db = DungeonDB.instance;
		DungeonDB.RoomData[] oldRooms = db.m_rooms.ToArray();
		KeyValuePair<int, DungeonDB.RoomData>[] oldMap = db.m_roomByHash.ToArray();
		db.m_rooms.Clear();
		db.m_roomByHash.Clear();
		GameObject templates = new GameObject("BossTestTemplates");
		GameObject generatorObject = new GameObject("BossTestGenerator");
		ZNetView view = generatorObject.AddComponent<ZNetView>();
		view.m_persistent = true;
		DungeonGenerator generator = generatorObject.AddComponent<DungeonGenerator>();
		generator.transform.position = new Vector3(0f, 12000f, 0f);
		generator.m_zoneCenter = generator.transform.position;
		generator.m_useCustomInteriorTransform = true;
		generator.m_themes = Room.Theme.ForestCrypt;
		generator.m_minRooms = 30;
		generator.m_maxRooms = 60;
		generator.SetupColliders();
		try
		{
			string[] array = new string[3] { "Crypt2", "Crypt3", "Crypt4" };
			foreach (string name in array)
			{
				GameObject gameObject = Get(paths.Keys.Single((string p) => p.EndsWith("/" + name + ".prefab", StringComparison.OrdinalIgnoreCase)));
				Location component = gameObject.GetComponent<Location>();
				Check((bool)component.transform.Find("Interior") && (bool)component.GetComponentInChildren<DungeonGenerator>(includeInactive: true), "Native " + name + " has common interior parent");
				Type type = plugin.GetType("Overhaul.Dungeons.BossInteriorPlacement", throwOnError: true);
				Transform transform = component.transform.Find("Interior");
				Vector3 localPosition = transform.localPosition;
				Vector3 vector = new Vector3(-128f, 37f, 256f);
				object[] array2 = new object[4]
				{
					new ZoneSystem.ZoneLocation
					{
						m_prefab = assets.Add(gameObject)
					},
					vector,
					ZoneSystem.SpawnMode.Ghost,
					null
				};
				Call(type, "Prefix", array2);
				Check(Math.Abs(component.GetComponentInChildren<DungeonGenerator>(includeInactive: true).transform.position.y + vector.y - (float)Call(layout, "Height", vector)) < 0.01f, "Generator moved to its exact lane");
				Check(component.GetComponentsInChildren<Teleport>(includeInactive: true).Any((Teleport t) => (bool)t.m_targetPoint && t.transform.position.y < 3000f && t.m_targetPoint.transform.position.y > 11000f), "Native exterior teleport targets moved interior");
				Call(type, "Finalizer", array2[3], null);
				Check(transform.localPosition == localPosition, "Shared native location transform restored");
			}
			foreach (string item2 in paths.Keys.Where((string p) => (p.StartsWith("Assets/world/Rooms/rooms/forestcrypt_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/rooms/sunkencrypt_new_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/cave/cave_new_", StringComparison.OrdinalIgnoreCase)) && p.EndsWith(".prefab")))
			{
				GameObject gameObject2 = Get(item2);
				Room room = (gameObject2 ? gameObject2.GetComponent<Room>() : null);
				if ((bool)room && room.m_enabled && (room.m_theme & Room.Theme.ForestCrypt) != Room.Theme.None)
				{
					Copy(room);
				}
			}
			Check(db.m_rooms.Count > 10, "Native forest room catalogue loaded");
			generator.SetupAvailableRooms();
			UnityEngine.Random.InitState(190926);
			generator.m_zoneSize = new Vector3(64f, 64f, 64f);
			generator.Clear();
			DungeonGenerator.m_placedRooms.Clear();
			DungeonGenerator.m_openConnections.Clear();
			generator.GenerateDungeon(ZoneSystem.SpawnMode.Ghost);
			Check(DungeonGenerator.m_placedRooms.Count > 5, "Unmodified native generator produced scan reference dungeon");
			List<object> list = new List<object>();
			foreach (Room placed in DungeonGenerator.m_placedRooms)
			{
				GameObject gameObject3 = Get(paths.Keys.Single((string p) => p.EndsWith("/" + placed.name.Replace("(Clone)", "") + ".prefab", StringComparison.OrdinalIgnoreCase)));
				MeshFilter[] componentsInChildren = gameObject3.GetComponentsInChildren<MeshFilter>(includeInactive: true);
				foreach (MeshFilter meshFilter in componentsInChildren)
				{
					MeshRenderer component2 = meshFilter.GetComponent<MeshRenderer>();
					if ((bool)component2 && (bool)meshFilter.sharedMesh)
					{
						list.Add(new
						{
							room = gameObject3.name,
							path = AnimationUtility.CalculateTransformPath(meshFilter.transform, gameObject3.transform),
							mesh = meshFilter.sharedMesh.name,
							scale = meshFilter.transform.lossyScale.ToString("F4"),
							position = gameObject3.transform.InverseTransformPoint(meshFilter.transform.position).ToString("F4"),
							rotation = meshFilter.transform.rotation.eulerAngles.ToString("F2"),
							size = meshFilter.sharedMesh.bounds.size.ToString("F4"),
							center = meshFilter.sharedMesh.bounds.center.ToString("F4"),
							materials = component2.sharedMaterials.Select((Material m) => (!m) ? "null" : (m.name + " | " + m.shader.name)).ToArray()
						});
					}
				}
			}
			File.WriteAllText("../../../Tools/BossDungeonWork/native-generated-visuals.json", JsonConvert.SerializeObject(list, Formatting.Indented));
			File.WriteAllLines("../../../Tools/BossDungeonWork/native-generated-plan.txt", DungeonGenerator.m_placedRooms.Select((Room r) => r.name + " " + r.transform.position.ToString() + " " + r.transform.eulerAngles.ToString()));
			generator.Clear();
			DungeonGenerator.m_placedRooms.Clear();
			DungeonGenerator.m_openConnections.Clear();
			generator.m_zoneSize = new Vector3(128f, 64f, 128f);
			DungeonDB.RoomData[] metadataRooms = db.m_rooms.ToArray();
			db.m_rooms.Clear();
			HashSet<Material> nativeMaterialSet = new HashSet<Material>();
			foreach (string item3 in paths.Keys.Where((string p) => (p.StartsWith("Assets/world/Rooms/rooms/forestcrypt_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/rooms/sunkencrypt_new_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/cave/cave_new_", StringComparison.OrdinalIgnoreCase)) && p.EndsWith(".prefab")))
			{
				GameObject gameObject4 = Get(item3);
				db.m_rooms.Add(new DungeonDB.RoomData
				{
					m_prefab = assets.Add(gameObject4),
					m_theme = Room.Theme.ForestCrypt
				});
				Renderer[] componentsInChildren2 = gameObject4.GetComponentsInChildren<Renderer>(includeInactive: true);
				for (int i = 0; i < componentsInChildren2.Length; i++)
				{
					Material[] sharedMaterials = componentsInChildren2[i].sharedMaterials;
					foreach (Material material in sharedMaterials)
					{
						if ((bool)material)
						{
							nativeMaterialSet.Add(material);
						}
					}
				}
			}
			GameObject visualFixture = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulBossRoom/Overhaul_ForestCrypt_BossRoom.prefab"));
			Renderer[] array3 = (from r in visualFixture.GetComponentsInChildren<Renderer>(includeInactive: true)
				where r.sharedMaterials.Any((Material m) => (bool)m && m.name.StartsWith("OverhaulNative:"))
				select r).ToArray();
			Type binder = plugin.GetType("Overhaul.Dungeons.BossNativeMaterials", throwOnError: true);
			Call(binder, "Bind", new object[1] { new GameObject[1] { visualFixture } });
			Check(array3.Length > 800, "Native crypt material binding covers floor walls and effects");
			Check(array3.All((Renderer r) => r.sharedMaterials.All((Material m) => !m || nativeMaterialSet.Contains(m))), "Exact native material objects used including shaders and all texture properties");
			Check((from f in visualFixture.transform.Find("Floor").GetComponentsInChildren<MeshFilter>()
				where f.name.StartsWith("CryptSlab_")
				select f).All((MeshFilter f) => f.transform.localScale == Vector3.one && f.sharedMesh.name == "stonefloor2"), "Paving uses original 2x2 crypt mesh at unit scale");
			Check(visualFixture.GetComponentsInChildren<Transform>(includeInactive: true).Count((Transform t) => t.name.StartsWith("DirtPatch_")) == 112, "Native dirt decal density matches generated reference ratio");
			Light componentInChildren = visualFixture.transform.Find("IndestructibleDecor/Torch_0").GetComponentInChildren<Light>(includeInactive: true);
			Check((bool)componentInChildren && Math.Abs(componentInChildren.range - 6f) < 0.01f && Math.Abs(componentInChildren.color.g - 0.621f) < 0.01f, "Native torch light range and color retained");
			Check(visualFixture.transform.Find("IndestructibleDecor/CryptDust").GetComponent<ParticleSystem>(), "Native dungeon dust emitter present");
			visualFixture.SetActive(value: false);
			GameObject mushroom = Get(paths.Keys.Single((string p) => p.EndsWith("/Pickable_Mushroom_yellow.prefab", StringComparison.OrdinalIgnoreCase)));
			Call(layout, "InstallMushrooms", visualFixture, mushroom);
			Check(visualFixture.GetComponentsInChildren<Pickable>(includeInactive: true).All(delegate(Pickable p)
			{
				Vector3 vector5 = visualFixture.transform.InverseTransformPoint(p.transform.position);
				return Mathf.Max(Mathf.Abs(vector5.x), Mathf.Abs(vector5.z)) >= 11.9f;
			}), "Mushrooms stay outside pillar rows in peripheral dirt patches");
			Check(visualFixture.GetComponentsInChildren<Pickable>(includeInactive: true).Length == 21 && visualFixture.GetComponentsInChildren<Pickable>(includeInactive: true).All((Pickable p) => (bool)p.GetComponent<ZNetView>() && p.m_itemPrefab == mushroom.GetComponent<Pickable>().m_itemPrefab), "Arena mushrooms use real native harvestable prefab with network state and item reward");
			MushroomGenerationChecks(generator, visualFixture, assets);
			Pickable[] componentsInChildren3 = visualFixture.GetComponentsInChildren<Pickable>(includeInactive: true);
			for (int i = 0; i < componentsInChildren3.Length; i++)
			{
				UnityEngine.Object.DestroyImmediate(componentsInChildren3[i].gameObject);
			}
			await NavigationChecks(visualFixture);
			UnityEngine.Object.DestroyImmediate(visualFixture);
			Call(binder, "Release");
			db.m_rooms.Clear();
			db.m_rooms.AddRange(metadataRooms);
			SpawnElevationChecks(generator);
			NativeSpawnChecks(generator);
			string[][] array4 = new string[2][]
			{
				new string[2] { "arena", "Overhaul_ForestCrypt_BossRoom" },
				new string[2] { "passage", "Overhaul_ForestCrypt_BossPassage" }
			};
			foreach (string[] array5 in array4)
			{
				DungeonDB.RoomData roomData = Copy(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/OverhaulBossRoom/" + array5[1] + ".prefab").GetComponent<Room>());
				roomData.m_enabled = false;
				FieldInfo field = layout.GetField(array5[0], BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
				object uninitializedObject = FormatterServices.GetUninitializedObject(field.FieldType);
				field.FieldType.GetField("<RoomData>k__BackingField", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(uninitializedObject, roomData);
				field.SetValue(null, uninitializedObject);
			}
			generator.SetupAvailableRooms();
			File.WriteAllLines("../../../Tools/BossDungeonWork/native-rooms.txt", db.m_rooms.Select(delegate(DungeonDB.RoomData r)
			{
				string[] obj2 = new string[11]
				{
					r.m_prefab.Name,
					" ",
					null,
					null,
					null,
					null,
					null,
					null,
					null,
					null,
					null
				};
				Vector3Int size = r.RoomInPrefab.m_size;
				obj2[2] = size.ToString();
				obj2[3] = " theme=";
				obj2[4] = r.m_theme.ToString();
				obj2[5] = " enabled=";
				obj2[6] = r.m_enabled.ToString();
				obj2[7] = " entrance=";
				obj2[8] = r.RoomInPrefab.m_entrance.ToString();
				obj2[9] = " ports=";
				obj2[10] = string.Join(";", from c in r.RoomInPrefab.GetConnections()
					select c.m_type + ":" + c.transform.localPosition.ToString() + "/" + c.transform.localEulerAngles.ToString());
				return string.Concat(obj2);
			}));
			HashSet<string> hashSet = new HashSet<string>();
			int val = int.MaxValue;
			int val2 = 0;
			for (int num2 = 0; num2 < 200; num2++)
			{
				generator.m_generatedSeed = num2 * 7919 + 17;
				int count = ZDOMan.instance.m_objectsByID.Count;
				try
				{
					Call(layout, "Plan", generator);
				}
				catch
				{
					File.WriteAllText("../../../Tools/BossDungeonWork/failed-plan.txt", "seed=" + num2 + " rooms=" + DungeonGenerator.m_placedRooms.Count + " open=" + DungeonGenerator.m_openConnections.Count + "\n" + string.Join("\n", DungeonGenerator.m_openConnections.Select((RoomConnection c) => c.m_type + " depth=" + c.m_placeOrder + " pos=" + c.transform.position.ToString())));
					throw;
				}
				Room[] array6 = DungeonGenerator.m_placedRooms.ToArray();
				Room boss = array6.Single((Room r) => r.name == "Overhaul_ForestCrypt_BossRoom");
				Check(array6.All((Room r) => r.name != "Overhaul_ForestCrypt_BossPassage"), "New plans use native corridors instead of legacy cube passage");
				Check(boss.m_placeOrder >= 4, "Boss beyond entrance branch");
				val = Math.Min(val, boss.m_placeOrder);
				val2 = Math.Max(val2, array6.Length);
				Dictionary<Room, int> dictionary = (Dictionary<Room, int>)Call(layout, "DistancesFromEntrance");
				Check(dictionary.ContainsKey(boss) && dictionary[boss] >= 4, "No cyclic shortcut beside entrance");
				RoomConnection port = boss.GetConnections().Single();
				Check(array6.Any((Room r) => r != boss && r.GetConnections().Any((RoomConnection c) => Vector3.Distance(c.transform.position, port.transform.position) < 0.1f)), "Arena port connected");
				DungeonGenerator.m_placedRooms.Remove(boss);
				Check(!generator.TestCollision(boss, boss.transform.position, boss.transform.rotation), "Arena collision/bounds valid");
				DungeonGenerator.m_placedRooms.Add(boss);
				Check(ZDOMan.instance.m_objectsByID.Count == count, "Planning creates zero network objects");
				generator.Save();
				byte[] byteArray = view.GetZDO().GetByteArray(ZDOVars.s_roomData);
				hashSet.Add(Convert.ToBase64String(byteArray));
				if (num2 < 5)
				{
					generator.Clear();
					DungeonGenerator.m_placedRooms.Clear();
					generator.Load();
					Check(generator.m_loadedRooms.Count((DungeonGenerator.RoomPlacementData r) => r.m_roomData.m_prefab.Name == "Overhaul_ForestCrypt_BossRoom") == 1, "Saved room contract resolves arena exactly once");
					generator.Spawn();
					Check(generator.GetComponentsInChildren<Room>().Length == array6.Length, "Native client rebuilds complete plan");
					Check(ZDOMan.instance.m_objectsByID.Count == count, "Reload spawns no duplicate boss/chest");
				}
			}
			Check(hashSet.Count == 200, "All test seeds produce distinct room plans");
			for (int num3 = -8; num3 <= 8; num3++)
			{
				for (int num4 = -8; num4 <= 8; num4++)
				{
					Vector3 vector2 = new Vector3(num3 * 64, 0f, num4 * 64);
					Bounds bounds = (Bounds)Call(layout, "BoundsFor", vector2);
					Vector3[] array7 = new Vector3[4]
					{
						new Vector3(64f, 0f, 0f),
						new Vector3(0f, 0f, 64f),
						new Vector3(256f, 0f, 256f),
						new Vector3(-64f, 0f, 64f)
					};
					foreach (Vector3 vector3 in array7)
					{
						Check(!bounds.Intersects((Bounds)Call(layout, "BoundsFor", vector2 + vector3)), "Nearby isolated lanes never overlap");
					}
				}
			}
			NetworkChecks();
			LootChecks();
			Type type2 = plugin.GetType("Overhaul.Dungeons.BossLoadedGuard", throwOnError: true);
			Vector3 vector4 = new Vector3(50000f, 12000f, 50000f);
			Check(!(bool)Call(type2, "HasFloor", vector4, -1), "Boss waits while room collision is absent");
			GameObject gameObject5 = GameObject.CreatePrimitive(PrimitiveType.Cube);
			gameObject5.transform.position = vector4 - Vector3.up;
			gameObject5.transform.localScale = new Vector3(4f, 1f, 4f);
			Check((bool)Call(type2, "HasFloor", vector4, -1), "Boss releases when actual room floor is loaded");
			UnityEngine.Object.DestroyImmediate(gameObject5);
			File.WriteAllText("../../../Tools/BossDungeonWork/check-results.txt", "PASS " + checks + " checks; 200 native-metadata forest plans; min boss depth " + val + "; max total rooms " + val2 + "; distinct seeds, zero planning ZDOs, native save/load, isolation and chest authorization.\n");
		}
		finally
		{
			layout.GetField("Planning", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, false);
			UnityEngine.Object.DestroyImmediate(generatorObject);
			UnityEngine.Object.DestroyImmediate(templates);
			db.m_rooms.Clear();
			db.m_rooms.AddRange(oldRooms);
			db.m_roomByHash.Clear();
			KeyValuePair<int, DungeonDB.RoomData>[] array8 = oldMap;
			for (int i = 0; i < array8.Length; i++)
			{
				KeyValuePair<int, DungeonDB.RoomData> keyValuePair = array8[i];
				db.m_roomByHash.Add(keyValuePair.Key, keyValuePair.Value);
			}
			typeof(Runtime).GetField("s_assetLoader", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SetValue(null, oldLoader);
			foreach (AssetBundle item4 in bundles.Values.Reverse())
			{
				if ((bool)item4)
				{
					item4.Unload(unloadAllLoadedObjects: true);
				}
			}
		}
		DungeonDB.RoomData Copy(Room source)
		{
			GameObject gameObject6 = new GameObject(source.name);
			gameObject6.transform.SetParent(templates.transform, worldPositionStays: false);
			gameObject6.transform.position = new Vector3(10000f, 0f, 0f);
			Room room2 = gameObject6.AddComponent<Room>();
			EditorUtility.CopySerialized(source, room2);
			room2.m_musicPrefab = null;
			room2.m_roomConnections = null;
			RoomConnection[] componentsInChildren4 = source.GetComponentsInChildren<RoomConnection>(includeInactive: true);
			foreach (RoomConnection roomConnection in componentsInChildren4)
			{
				GameObject gameObject7 = new GameObject(roomConnection.name);
				gameObject7.transform.SetParent(gameObject6.transform, worldPositionStays: false);
				gameObject7.transform.localPosition = roomConnection.transform.localPosition;
				gameObject7.transform.localRotation = roomConnection.transform.localRotation;
				RoomConnection dest = gameObject7.AddComponent<RoomConnection>();
				EditorUtility.CopySerialized(roomConnection, dest);
			}
			DungeonDB.RoomData roomData2 = new DungeonDB.RoomData
			{
				m_prefab = assets.Add(gameObject6),
				m_enabled = source.m_enabled,
				m_theme = source.m_theme
			};
			db.m_rooms.Add(roomData2);
			db.m_roomByHash[roomData2.Hash] = roomData2;
			return roomData2;
		}
	}

	private static void SpawnElevationChecks(DungeonGenerator generator)
	{
		GameObject gameObject = new GameObject("NativeRandomSpawnCheck");
		RandomSpawn randomSpawn = gameObject.AddComponent<RandomSpawn>();
		randomSpawn.Prepare();
		randomSpawn.m_chanceToSpawn = 100f;
		string name = generator.name;
		Vector3 position = generator.transform.position;
		generator.name = "DG_ForestCrypt";
		generator.transform.position = new Vector3(0f, 16480f, 0f);
		randomSpawn.Randomize(new Vector3(0f, 16480f, 0f), null, generator);
		Check(gameObject.activeSelf, "Native guaranteed spawn survives relocated dungeon height instead of exceeding 10000m cap");
		randomSpawn.m_chanceToSpawn = 0f;
		UnityEngine.Random.InitState(123);
		randomSpawn.Randomize(new Vector3(0f, 16480f, 0f), null, generator);
		Check(!gameObject.activeSelf, "Native spawn chance remains respected after altitude correction");
		gameObject.SetActive(value: true);
		randomSpawn.m_chanceToSpawn = 100f;
		generator.name = "unrelated";
		randomSpawn.Randomize(new Vector3(0f, 12000f, 0f), null, generator);
		Check(!gameObject.activeSelf, "Unrelated generators retain native altitude restrictions");
		generator.name = name;
		generator.transform.position = position;
		UnityEngine.Object.DestroyImmediate(gameObject);
	}

	private static void MushroomGenerationChecks(DungeonGenerator generator, GameObject arena, DungeonTestAssets assets)
	{
		GameObject gameObject = new GameObject("InactiveHarvestTemplate");
		gameObject.SetActive(value: false);
		arena.transform.SetParent(gameObject.transform, worldPositionStays: false);
		arena.SetActive(value: true);
		DungeonDB.RoomData roomData = new DungeonDB.RoomData
		{
			m_prefab = assets.Add(arena),
			m_theme = Room.Theme.ForestCrypt
		};
		HashSet<ZDO> before = new HashSet<ZDO>(ZDOMan.instance.m_objectsByID.Values);
		bool ghostInit = ZNetView.m_ghostInit;
		try
		{
			ZNetView.StartGhostInit();
			Room room = generator.PlaceRoom(roomData, new Vector3(800f, 12004.5f, 800f), Quaternion.identity, null, ZoneSystem.SpawnMode.Ghost);
			ZNetView.FinishGhostInit();
			ZDO[] added = ZDOMan.instance.m_objectsByID.Values.Where((ZDO z) => !before.Contains(z)).ToArray();
			Check(added.Length == 21 && added.All((ZDO z) => z.GetPrefab() == "Pickable_Mushroom_yellow".GetStableHashCode() && z.Persistent), "Native room generation creates exactly 21 persistent mushroom ZDOs");
			Check(room.GetComponentsInChildren<ZNetView>().Length == 0, "Static arena contains no duplicate active mushroom views");
			int count = ZDOMan.instance.m_objectsByID.Count;
			Room room2 = generator.PlaceRoom(roomData, new Vector3(800f, 12004.5f, 800f), Quaternion.identity, null, ZoneSystem.SpawnMode.Client);
			Check(ZDOMan.instance.m_objectsByID.Count == count, "Native client reload creates no duplicate mushroom ZDOs");
			Pickable pickable = UnityEngine.Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None).First((Pickable p) => (bool)p.m_nview && added.Contains(p.m_nview.GetZDO()));
			pickable.m_nview.GetZDO().SetOwner(ZNet.GetUID());
			pickable.RPC_SetPicked(ZNet.GetUID(), picked: true);
			Check(pickable.m_picked && pickable.m_nview.GetZDO().GetBool(ZDOVars.s_picked), "Harvested state persists through native Pickable RPC");
			ZPackage zPackage = new ZPackage();
			pickable.m_nview.GetZDO().Serialize(zPackage);
			ZDO zDO = new ZDO();
			zDO.Deserialize(new ZPackage(zPackage.GetArray()));
			Check(zDO.GetBool(ZDOVars.s_picked), "Mushroom harvest survives native world object serialization");
			foreach (Pickable item2 in from p in UnityEngine.Object.FindObjectsByType<Pickable>(FindObjectsSortMode.None)
				where (bool)p.m_nview && added.Contains(p.m_nview.GetZDO())
				select p)
			{
				UnityEngine.Object.DestroyImmediate(item2.gameObject);
			}
			DungeonGenerator.m_placedRooms.Remove(room);
			RoomConnection[] connections = room.GetConnections();
			foreach (RoomConnection item in connections)
			{
				DungeonGenerator.m_openConnections.Remove(item);
			}
			UnityEngine.Object.DestroyImmediate(room.gameObject);
			UnityEngine.Object.DestroyImmediate(room2.gameObject);
		}
		finally
		{
			ZNetView.m_ghostInit = ghostInit;
			arena.SetActive(value: false);
			arena.transform.SetParent(null, worldPositionStays: false);
			UnityEngine.Object.DestroyImmediate(gameObject);
		}
	}

	private static void NativeSpawnChecks(DungeonGenerator generator)
	{
		GameObject gameObject = new GameObject("InactiveNativeSpawnFixtures");
		gameObject.SetActive(value: false);
		Vector3 position = generator.transform.position;
		string name = generator.name;
		UnityEngine.Random.State state = UnityEngine.Random.state;
		int num = 0;
		HashSet<string> hashSet = new HashSet<string>();
		try
		{
			generator.name = "DG_ForestCrypt";
			foreach (string item in paths.Keys.Where((string p) => (p.StartsWith("Assets/world/Rooms/rooms/forestcrypt_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/rooms/sunkencrypt_new_", StringComparison.OrdinalIgnoreCase) || p.StartsWith("Assets/world/Rooms/cave/cave_new_", StringComparison.OrdinalIgnoreCase)) && p.EndsWith(".prefab")))
			{
				generator.name=item.Contains("sunkencrypt_")?"DG_SunkenCrypt":item.Contains("/cave/")?"DG_Cave":"DG_ForestCrypt";
                GameObject room = UnityEngine.Object.Instantiate(Get(item), gameObject.transform, worldPositionStays: false);
				Transform[] transforms = room.GetComponentsInChildren<Transform>(includeInactive: true);
				bool[] active = transforms.Select((Transform t) => t.gameObject.activeSelf).ToArray();
				RandomSpawn[] spawns = Utils.GetEnabledComponentsInChildren<RandomSpawn>(room);
				RandomSpawn[] array = spawns;
				for (int num2 = 0; num2 < array.Length; num2++)
				{
					array[num2].Prepare();
				}
				RandomObject[] alternatives = Utils.GetEnabledComponentsInChildren<RandomObject>(room);
				ZNetView[] views = Utils.GetEnabledComponentsInChildren<ZNetView>(room);
				for (int num3 = 1; num3 <= 12; num3++)
				{
					bool[] first = Sample(5000f, num3);
					float[] array2 = new float[4] { 12000f, 16480f, 18400f, 44640f };
					for (int num2 = 0; num2 < array2.Length; num2++)
					{
						float height = array2[num2];
						bool[] array3 = Sample(height, num3);
						Check(first.SequenceEqual(array3), "Native random spawn selection unchanged at " + height + " for " + room.name);
						num++;
						for (int num4 = 0; num4 < views.Length; num4++)
						{
							if (array3[num4])
							{
								hashSet.Add(views[num4].name);
							}
						}
					}
				}
				UnityEngine.Object.DestroyImmediate(room);
				bool[] Sample(float num5, int seed)
				{
					for (int i = 0; i < transforms.Length; i++)
					{
						transforms[i].gameObject.SetActive(active[i]);
					}
					generator.transform.position = new Vector3(0f, num5, 0f);
					UnityEngine.Random.InitState(seed);
					RandomSpawn[] array4 = spawns;
					foreach (RandomSpawn obj in array4)
					{
						obj.Randomize(obj.transform.position - room.transform.position + Vector3.up * num5, null, generator);
					}
					RandomObject[] array5 = alternatives;
					foreach (RandomObject obj2 in array5)
					{
						obj2.Randomize(obj2.transform.position - room.transform.position + Vector3.up * num5, null, generator);
					}
					return views.Select((ZNetView v) => v.gameObject.activeSelf).ToArray();
				}
			}
			Check(hashSet.Any((string n) => n.Contains("Mushroom")) && hashSet.Any((string n) => n.Contains("SurtlingCore")) && hashSet.Any((string n) => n.Contains("Chest")) && hashSet.Any((string n) => n.Contains("Spawner") || n.Contains("Skeleton")), "Native mushrooms, cores, chests and enemy sources survive randomization at relocated heights");
			File.WriteAllText("../../../Tools/BossDungeonWork/native-spawn-checks.txt", num + " native room/seed/height comparisons matched 5000m reference.\n" + string.Join("\n", hashSet.OrderBy((string n) => n)));
		}
		finally
		{
			generator.transform.position = position;
			generator.name = name;
			UnityEngine.Random.state = state;
			UnityEngine.Object.DestroyImmediate(gameObject);
		}
	}

	private static async Task NavigationChecks(GameObject arena)
	{
		arena.transform.position = new Vector3(400f, 12004.5f, 400f);
		arena.SetActive(value: true);
		Physics.SyncTransforms();
		Pathfinding old = Pathfinding.instance;
		GameObject go = new GameObject("NativeBossNavigationCheck");
		Pathfinding path = go.AddComponent<Pathfinding>();
		path.enabled = false;
		path.m_layers = arena.GetComponentsInChildren<Collider>(includeInactive: true).Aggregate(0, (int mask, Collider c) => mask | (1 << c.gameObject.layer));
		path.m_waterLayers = 0;
		try
		{
			Pathfinding.AgentType[] array = new Pathfinding.AgentType[2]
			{
				Pathfinding.AgentType.Humanoid,
				Pathfinding.AgentType.TrollSize
			};
			foreach(float height in new[]{12000f,18400f,44640f})
            for (int num = 0; num < array.Length; num++)
			{
				arena.transform.position=new Vector3(400,height+4.5f,400);Physics.SyncTransforms();
                Pathfinding.AgentType agent = array[num];
				Vector3Int tile = path.GetTile(new Vector3(400f, 0f, 400f), agent);
				Vector3Int upper = path.GetTile(new Vector3(400f, height, 400f), agent);
				Vector3Int tile2 = path.GetTile(new Vector3(400f, height+128, 400f), agent);
				Check(tile != upper && upper != tile2 && path.GetTilePos(tile).y == 2500f && path.GetTilePos(upper).y == height, "Surface and vertical dungeon lanes have distinct native navmesh tiles");
				for (int x = -1; x <= 1; x++)
				{
					for (int z = -1; z <= 1; z++)
					{
						Vector3Int tile3 = upper + new Vector3Int(x, z, 0);
						Pathfinding.NavMeshTile navTile = path.GetNavTile(tile3);
						path.BuildTile(navTile);
						DateTime timeout = DateTime.UtcNow.AddSeconds(20.0);
						while (!path.m_buildOperation.isDone && DateTime.UtcNow < timeout)
						{
							await Task.Yield();
						}
						if (!path.m_buildOperation.isDone)
						{
							throw new Exception("Navigation build timeout");
						}
						path.UpdateAsyncBuild();
					}
				}
				List<Vector3> list = new List<Vector3>();
				Check(path.GetPath(new Vector3(400f, height+.2f, 413f), new Vector3(400f, height+.2f, 385f), list, agent, requireFullPath: true) && list.Count >= 2, "Real native navmesh provides complete boss-to-entrance path for " + agent);
				List<Vector3> list2 = new List<Vector3>();
				path.FindGround(new Vector3(400f, height, 400f), testWater: false, list2, path.GetSettings(agent));
				Check(list2.Any((Vector3 h) => Mathf.Abs(h.y - height) < 0.2f) && list2.All((Vector3 h) => h.y > height-48 && h.y < height+48), "Native navmesh stitching finds dungeon floor only in correct vertical lane");
			}
		}
		finally
		{
			UnityEngine.Object.DestroyImmediate(go);
			Pathfinding.m_instance = old;
			arena.SetActive(value: false);
		}
	}

	private static void NetworkChecks()
	{
		int hash = (int)encounter.GetField("EncounterKey", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
		int hash2 = (int)encounter.GetField("BossKey", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
		int hash3 = (int)encounter.GetField("DefeatedKey", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null);
		string text = Guid.NewGuid().ToString("N");
		ZDO zDO = ZDOMan.instance.CreateNewZDO(new Vector3(0f, 12000f, 0f), 123);
		zDO.SetOwner(742L);
		zDO.Set(hash, text);
		zDO.Set(hash2, value: true);
		ZDO zDO2 = ZDOMan.instance.CreateNewZDO(new Vector3(0f, 12000f, 5f), "Overhaul_ForestBossChest".GetStableHashCode());
		zDO2.SetPrefab("Overhaul_ForestBossChest".GetStableHashCode());
		zDO2.Set(hash, text);
		Call(encounter, "Unlock", 999L, text);
		Check(!zDO2.GetBool(hash3), "Foreign peer cannot unlock boss chest");
		Call(encounter, "Unlock", 742L, Guid.NewGuid().ToString("N"));
		Check(!zDO2.GetBool(hash3), "Different generation cannot unlock boss chest");
		Call(encounter, "Unlock", 742L, text);
		Check(zDO2.GetBool(hash3), "Simulation owner death unlocks chest on server");
		Call(encounter, "Unlock", 742L, text);
		Check(zDO2.GetBool(hash3), "Death notification idempotent");
		ZPackage zPackage = new ZPackage();
		zDO2.Serialize(zPackage);
		ZDO zDO3 = new ZDO();
		zDO3.Deserialize(new ZPackage(zPackage.GetArray()));
		Check(zDO3.GetBool(hash3) && zDO3.GetString(hash) == text, "Victory survives native ZDO serialization");
		GameObject gameObject = new GameObject("BossLevelTest");
		gameObject.SetActive(value: false);
		ZNetView zNetView = gameObject.AddComponent<ZNetView>();
		zNetView.m_zdo = zDO;
		zDO.SetOwner(ZNet.GetUID());
		Character character = gameObject.AddComponent<Character>();
		character.m_nview = zNetView;
		character.m_health = 100f;
		zDO.Set(ZDOVars.s_overrideHoverName, "Boss test");
		character.SetLevel(4);
		character.SetHealth(character.GetMaxHealth());
		Check(character.GetLevel() == 4 && zDO.GetInt(ZDOVars.s_level) == 4 && character.GetMaxHealth() == character.GetMaxHealthBase() * 4f, "Native third-star level and health multiplier");
		Check(!character.GetHoverName().Contains("★"), "Guardian name has no text stars; native HUD icons used");
		GameObject gameObject2 = new GameObject("GuardianHudTest");
		gameObject2.SetActive(value: false);
		EnemyHud enemyHud = gameObject2.AddComponent<EnemyHud>();
		GameObject gameObject3 = new GameObject("Gui", typeof(RectTransform));
		gameObject3.transform.SetParent(gameObject2.transform, worldPositionStays: false);
		GameObject gameObject4 = new GameObject("level_2", typeof(RectTransform), typeof(Image));
		gameObject4.transform.SetParent(gameObject3.transform, worldPositionStays: false);
		Image component = gameObject4.GetComponent<Image>();
		component.color = Color.yellow;
		component.rectTransform.sizeDelta = new Vector2(16f, 16f);
		enemyHud.m_huds.Add(character, new EnemyHud.HudData
		{
			m_character = character,
			m_gui = gameObject3,
			m_level2 = component.rectTransform
		});
		Type type = encounter.Assembly.GetType("Overhaul.Dungeons.BossStarsPatch", throwOnError: true);
		Call(type, "Postfix", enemyHud);
		Call(type, "Postfix", enemyHud);
		Transform transform = gameObject3.transform.Find("OverhaulThreeStars");
		Check((bool)transform && transform.childCount == 3 && transform.GetComponentsInChildren<Image>(includeInactive: true).All((Image i) => i.color == Color.yellow), "Three native-style icons with original color and no duplicates");
		zDO.Set(hash2, value: false);
		Call(type, "Postfix", enemyHud);
		Check(!transform.gameObject.activeSelf, "Third-star HUD hidden for ordinary monsters");
		zDO.Set(hash2, value: true);
		UnityEngine.Object.DestroyImmediate(gameObject2);
		LevelEffects levelEffects = gameObject.AddComponent<LevelEffects>();
		levelEffects.m_levelSetups.Add(new LevelEffects.LevelSetup
		{
			m_scale = 1.1f
		});
		levelEffects.m_levelSetups.Add(new LevelEffects.LevelSetup
		{
			m_scale = 1.2f
		});
		levelEffects.SetupLevelVisualization(4);
		Check(Mathf.Abs(gameObject.transform.localScale.x - 1.38f) < 0.001f, "Third star grows 15 percent beyond second star");
		levelEffects.SetupLevelVisualization(4);
		Check(levelEffects.m_levelSetups.Count == 3 && Mathf.Abs(gameObject.transform.localScale.x - 1.38f) < 0.001f, "Visual setup is idempotent");
		gameObject.name = "Troll";
		levelEffects.m_levelSetups.RemoveAt(2);
		levelEffects.SetupLevelVisualization(4);
		Check(Mathf.Abs(gameObject.transform.localScale.x - 1.2f) < 0.001f, "Troll retains native second-star size");
		CharacterDrop characterDrop = gameObject.AddComponent<CharacterDrop>();
		characterDrop.m_character = character;
		GameObject gameObject5 = new GameObject("GuardianDropTest");
		characterDrop.m_drops.Add(new CharacterDrop.Drop
		{
			m_prefab = gameObject5,
			m_chance = 1f,
			m_amountMin = 1,
			m_amountMax = 2,
			m_dontScale = true,
			m_levelMultiplier = true
		});
		Check(characterDrop.GenerateDropList().Single().Value == 6, "Guardian drop multiplier is six in native generator");
		characterDrop.m_drops[0].m_levelMultiplier = false;
		Check(characterDrop.GenerateDropList().Single().Value == 1, "Unscaled drops unchanged");
		characterDrop.m_drops[0].m_levelMultiplier = true;
		zDO.Set(hash2, value: false);
		Check(characterDrop.GenerateDropList().Single().Value == 8, "Ordinary third-star native loot unchanged");
		UnityEngine.Object.DestroyImmediate(gameObject5);
		zDO.Set(hash2, value: false);
		character.SetLevel(2);
		Check(character.GetLevel() == 2 && !character.GetHoverName().EndsWith("★★★"), "Ordinary monster levels and names unaffected");
		UnityEngine.Object.DestroyImmediate(gameObject);
	}

	private static void LootChecks()
	{
		GameObject gameObject = new GameObject("BossChestTest");
		gameObject.SetActive(value: false);
		ZNetView zNetView = gameObject.AddComponent<ZNetView>();
		zNetView.m_zdo = ZDOMan.instance.CreateNewZDO(Vector3.zero, "Overhaul_ForestBossChest".GetStableHashCode());
		zNetView.m_zdo.SetPrefab("Overhaul_ForestBossChest".GetStableHashCode());
		zNetView.m_zdo.SetOwner(ZNet.GetUID());
		Container container = gameObject.AddComponent<Container>();
		container.m_nview = zNetView;
		container.m_inventory = new Inventory("test", null, 4, 2);
		ObjectDB instance = ObjectDB.instance;
		ObjectDB objectDB = (ObjectDB.m_instance = (instance ? instance : gameObject.AddComponent<ObjectDB>()));
		Dictionary<int, GameObject> dictionary = new Dictionary<int, GameObject>();
		var entries=(Array)Call(encounter.Assembly.GetType("Overhaul.Dungeons.BossLootData"),"For","Forest");
        string[] names=entries.Cast<object>().Select(e=>(string)e.GetType().GetField("Prefab",All).GetValue(e)).ToArray();
		int[] array=entries.Cast<object>().Select(e=>(int)e.GetType().GetField("Minimum",All).GetValue(e)).ToArray();
		int[] array2=entries.Cast<object>().Select(e=>(int)e.GetType().GetField("Maximum",All).GetValue(e)).ToArray();
		try
		{
			string[] array3 = names;
			foreach (string name in array3)
			{
				GameObject value = Get(paths.Keys.Single((string p) => p.EndsWith("/" + name + ".prefab", StringComparison.OrdinalIgnoreCase)));
				int stableHashCode = name.GetStableHashCode();
				objectDB.m_itemByHash.TryGetValue(stableHashCode, out var value2);
				dictionary[stableHashCode] = value2;
				objectDB.m_itemByHash[stableHashCode] = value;
			}
			HashSet<int>[] array4 = names.Select((string n) => new HashSet<int>()).ToArray();
			for (int num = 0; num < 1000; num++)
			{
				UnityEngine.Random.InitState(num);
				Call(encounter, "FillChest", container);
				List<ItemDrop.ItemData> allItems = container.GetInventory().GetAllItems();
				Check(allItems.Count == names.Length, "All configured guaranteed reward types");
				int i2;
				for (i2 = 0; i2 < names.Length; i2++)
				{
					ItemDrop.ItemData itemData = allItems.Single((ItemDrop.ItemData v) => v.m_dropPrefab.name == names[i2]);
					Check(itemData.m_stack >= array[i2] && itemData.m_stack <= array2[i2], "Reward amount inside inclusive bounds");
					array4[i2].Add(itemData.m_stack);
				}
				Inventory inventory = new Inventory("restored", null, 4, container.GetInventory().GetHeight());
				inventory.Load(new ZPackage(zNetView.m_zdo.GetByteArray(ZDOVars.s_items)));
				Check(inventory.GetAllItems().Sum((ItemDrop.ItemData v) => v.m_stack) == allItems.Sum((ItemDrop.ItemData v) => v.m_stack) && inventory.NrOfItems() == names.Length, "Loot survives native inventory save/load");
			}
			for (int num2 = 0; num2 < names.Length; num2++)
			{
				Check(array4[num2].Contains(array[num2]) && array4[num2].Contains(array2[num2]), "Both reward endpoints reachable");
			}
			Check(!container.CheckAccess(123L), "Native CheckAccess rejects opening while boss alive");
			zNetView.m_zdo.Set((int)encounter.GetField("DefeatedKey", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null), value: true);
			Check(container.CheckAccess(123L), "Native CheckAccess allows opening after victory");
		}
		finally
		{
			foreach (KeyValuePair<int, GameObject> item in dictionary)
			{
				if ((bool)item.Value)
				{
					objectDB.m_itemByHash[item.Key] = item.Value;
				}
				else
				{
					objectDB.m_itemByHash.Remove(item.Key);
				}
			}
			ObjectDB.m_instance = instance;
			UnityEngine.Object.DestroyImmediate(gameObject);
		}
	}
}
