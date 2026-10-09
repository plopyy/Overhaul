using HarmonyLib;
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Overhaul.Dungeons;
using Overhaul.Leveling;

namespace Overhaul.Commands
{
    internal static class AdminCommands
    {
        private const string Prefix = "o_";
        private const string ResetDungeonsCommand = Prefix + "resetalldungeons";
        private const string ResetDungeonCommand = Prefix + "resetdungeon";
        private const string TargetRequest = "Overhaul_DungeonResetTarget";
        private const string ExitRequest = "Overhaul_DungeonAdminExit";
        private sealed class Evacuation
        {
            internal ZDOMan World;
            internal ZNetPeer Peer;
            internal ZDOID Character, Target;
            internal int Epoch;
            internal float Deadline;
            internal ZoneSystem.ZoneLocation Location;
            internal bool Sent;
            internal Vector3 Exit;
        }
        private static readonly List<Evacuation> evacuations = new List<Evacuation>();
        private const string Request = "Overhaul_DungeonReset";
        private const string Response = "Overhaul_DungeonResetResult";
        private const string ResetLevelRequest = "Overhaul_AdminResetLevel";
        private const string ExperienceRequest = "Overhaul_AdminExperience";
        private const string MudRequest = "Overhaul_AdminMudTest";
        private const string MudResponse = "Overhaul_AdminMudTestApproved";

        internal static void Initialize()
        {
            // Opening the terminal is independent of permission to run admin commands.
            // The session flag survives Console.Awake and native settings reloads.
            Console.SetConsoleEnabledForThisSession();
            new Terminal.ConsoleCommand(Prefix+"exp",
                "Ajoute ou retire de l'EXP Overhaul : o_exp <montant signe> [nom exact du joueur connecte] (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args=>
                {
                    if(args.Length<2 || !long.TryParse(args.Args[1],System.Globalization.NumberStyles.AllowLeadingSign,
                        System.Globalization.CultureInfo.InvariantCulture,out long amount) || amount==0)
                    {args.Context.AddString("Usage : o_exp <montant entier non nul, ex. +1250 ou -8750> [nom exact du joueur connecte]");return;}
                    if(!ZNet.instance){args.Context.AddString("Overhaul : aucun monde connecte.");return;}
                    string target=string.Join(" ",args.Args.Skip(2)).Trim().Trim('"');
                    if(ZNet.instance.IsServer()){args.Context.AddString(AdjustExperience(null,target,amount));return;}
                    var server=ZNet.instance.GetServerPeer();
                    if(server==null || !server.IsReady()){args.Context.AddString("Overhaul : serveur non connecte.");return;}
                    server.m_rpc.Invoke(ExperienceRequest,target,amount);
                    args.Context.AddString("Overhaul : correction d'EXP envoyee au serveur pour verification admin.");
                }));
            new Terminal.ConsoleCommand(Prefix + "testmud",
                "Traverser les tas de boue des cryptes pour les tests : o_testmud [on|off] (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Length > 2 || (args.Length == 2 && args.Args[1] != "on" && args.Args[1] != "off"))
                    { args.Context.AddString("Usage : o_testmud [on|off]"); return; }
                    if (!ZNet.instance || !Player.m_localPlayer) { args.Context.AddString("Commande a lancer depuis un personnage connecte."); return; }
                    bool enabled = args.Length == 1 ? !MudCollisionTest.Enabled : args.Args[1] == "on";
                    if (ZNet.instance.IsServer()) { ApplyMud(enabled); return; }
                    var server = ZNet.instance.GetServerPeer();
                    if (server == null || !server.IsReady()) return;
                    server.m_rpc.Invoke(MudRequest,enabled);
                }));
            new Terminal.ConsoleCommand(ResetDungeonCommand,
                "Demande le reset du donjon actuel ou du lieu eligible le plus proche (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Length != 1) { args.Context.AddString("Usage : " + ResetDungeonCommand); return; }
                    if (!ZNet.instance) { args.Context.AddString("Overhaul : aucun monde connecte."); return; }
                    if (ZNet.instance.IsServer()) { args.Context.AddString(ResetTarget(null)); return; }
                    var server = ZNet.instance.GetServerPeer();
                    if (server == null || !server.IsReady()) { args.Context.AddString("Overhaul : serveur non connecte."); return; }
                    server.m_rpc.Invoke(TargetRequest);
                    args.Context.AddString("Overhaul : demande ciblee envoyee au serveur pour verification admin.");
                }));
            new Terminal.ConsoleCommand(Prefix + "resetlevel",
                "Remet la progression Overhaul a zero. Usage : o_resetlevel [nom exact du joueur connecte] (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (!ZNet.instance) { args.Context.AddString("Overhaul : aucun monde connecte."); return; }
                    string target=string.Join(" ",args.Args.Skip(1)).Trim().Trim('"');
                    if (ZNet.instance.IsServer()) { args.Context.AddString(ResetLevel(null,target)); return; }
                    var server=ZNet.instance.GetServerPeer();
                    if(server==null || !server.IsReady()){args.Context.AddString("Overhaul : serveur non connecte.");return;}
                    server.m_rpc.Invoke(ResetLevelRequest,target);
                    args.Context.AddString("Overhaul : demande de remise a zero envoyee au serveur.");
                }));
            new Terminal.ConsoleCommand(Prefix + "mythicset",
                "Test Epic Loot : fait apparaitre une piece d'equipement mythique aleatoire appartenant a un ensemble (admin serveur + devcommands, Epic Loot requis).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (!EpicLootVisuals.Loaded) { args.Context.AddString("Overhaul : Epic Loot n'est pas installe, commande sans effet."); return; }
                    if (!AdminCommandAccess.LocalEnabled) { args.Context.AddString("Overhaul : administrateur et devcommands actifs requis."); return; }
                    string id = RandomSetPiece(out bool mythicSet);
                    if (id == null) { args.Context.AddString("Overhaul : aucun ensemble Epic Loot configure."); return; }
                    // Epic Loot's own command and generation; a legendary set piece is rolled as Mythic when the
                    // configuration has no mythic set (Epic Loot's default configuration has none).
                    args.Context.AddString("Overhaul : piece d'ensemble " + id + (mythicSet ? "" : " (ensemble legendaire, generee en Mythique)"));
                    Console.instance.TryRunCommand("magicitemmythic " + id, false, true);
                }));
            new Terminal.ConsoleCommand(Prefix + "aurabooster",
                "Test : passe au mode suivant de renfort des auras de loot en plein jour (Opacity, Brightness, None).",
                (Terminal.ConsoleEvent)(args =>
                {
                    var entry = Utility.OverhaulConfig.LootAuraDaylight;
                    if (entry == null) return;
                    string[] modes = { "Opacity", "Brightness", "None" };
                    entry.Value = modes[(System.Array.IndexOf(modes, entry.Value) + 1) % modes.Length];
                    args.Context.AddString("Overhaul : renfort des auras de loot en plein jour = " + entry.Value);
                }));
            new Terminal.ConsoleCommand(Prefix + "checktooltips",
                "Verifie l'infobulle de tous les objets et liste les textes non traduits (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (!AdminCommandAccess.LocalEnabled) { args.Context.AddString("Overhaul : administrateur et devcommands actifs requis."); return; }
                    args.Context.AddString(TooltipAudit.Run());
                }));
            new Terminal.ConsoleCommand(ResetDungeonsCommand,
                "Demande le reset des lieux generes, visites ou non, actives dans Dungeon.ResetLocations (admin serveur + devcommands).",
                (Terminal.ConsoleEvent)(args =>
                {
                    if (args.Length != 1) { args.Context.AddString("Usage : " + ResetDungeonsCommand); return; }
                    if (!ZNet.instance) { args.Context.AddString("Overhaul : aucun monde connecte."); return; }
                    if (ZNet.instance.IsServer()) { args.Context.AddString(DungeonRuntime.RequestManualReset()); return; }
                    ZNetPeer server = ZNet.instance.GetServerPeer();
                    if (server == null || !server.IsReady()) { args.Context.AddString("Overhaul : serveur non connecte."); return; }
                    server.m_rpc.Invoke(Request);
                    args.Context.AddString("Overhaul : demande envoyee au serveur pour verification admin.");
                }));
        }

        // A random piece of an Epic Loot mythic set, or of a legendary set when none is configured.
        private static string RandomSetPiece(out bool mythicSet)
        {
            mythicSet = false;
            System.Type helper = System.Type.GetType("EpicLoot.LegendarySystem.UniqueLegendaryHelper, EpicLoot");
            var mythic = helper?.GetField("MythicSets")?.GetValue(null) as System.Collections.IDictionary;
            var legendary = helper?.GetField("LegendarySets")?.GetValue(null) as System.Collections.IDictionary;
            var sets = mythic != null && mythic.Count > 0 ? mythic : legendary;
            if (sets == null || sets.Count == 0) return null;
            mythicSet = sets == mythic;
            object set = sets.Values.Cast<object>().ElementAt(UnityEngine.Random.Range(0, sets.Count));
            var ids = (set.GetType().GetField("LegendaryIDs")?.GetValue(set) as System.Collections.IEnumerable)?.Cast<string>().ToList();
            return ids == null || ids.Count == 0 ? null : ids[UnityEngine.Random.Range(0, ids.Count)];
        }

        internal static bool Authorized(ZRpc rpc)
            => IsAdministrator(rpc) && AdminCommandAccess.Enabled(rpc);

        internal static bool IsAdministrator(ZRpc rpc)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer() || rpc == null) return false;
            ZNetPeer peer = ZNet.instance.GetPeer(rpc);
            return peer != null && peer.IsReady() && ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName());
        }

        private static string ResetLevel(ZRpc requester,string target)
            => ProgressionTarget(requester,target,"o_resetlevel",LevelingNetwork.AdminReset);

        private static string AdjustExperience(ZRpc requester,string target,long amount)
            => ProgressionTarget(requester,target,"o_exp <montant signe>",peer=>LevelingNetwork.AdminExperience(peer,amount));

        private static string ProgressionTarget(ZRpc requester,string target,string usage,Func<ZNetPeer,string> apply)
        {
            if(!ZNet.instance || !ZNet.instance.IsServer())return "Overhaul : serveur requis.";
            if(requester!=null && !Authorized(requester))return "Overhaul : acces refuse, admin serveur et devcommands actifs requis (adminlist.txt).";
            target=(target??"").Trim();
            if(target.Length==0)
            {
                if(requester!=null)return apply(ZNet.instance.GetPeer(requester));
                if(Player.m_localPlayer)return apply(null);
                return "Usage serveur dedie : "+usage+" <nom exact du joueur connecte>";
            }
            var peers=ZNet.instance.GetPeers().Where(p=>p.IsReady() && string.Equals(p.m_playerName,target,StringComparison.OrdinalIgnoreCase)).ToList();
            bool local=Player.m_localPlayer && string.Equals(Player.m_localPlayer.GetPlayerName(),target,StringComparison.OrdinalIgnoreCase);
            if(peers.Count+(local?1:0)!=1)return "Overhaul : joueur absent ou nom ambigu ; utiliser un nom exact et unique de joueur connecte.";
            return apply(local?null:peers[0]);
        }

        private static void ReceiveResetLevel(ZRpc rpc,string target)
        {
            if(!ZNet.instance || !ZNet.instance.IsServer())return;
            rpc.Invoke(Response,ResetLevel(rpc,target));
        }

        private static void Receive(ZRpc rpc)
        {
            // Authenticate the actual connection, never a user ID supplied by the client.
            if (!ZNet.instance || !ZNet.instance.IsServer()) return;
            rpc.Invoke(Response, Authorized(rpc) ? DungeonRuntime.RequestManualReset() : "Overhaul : acces refuse, admin serveur et devcommands actifs requis (adminlist.txt).");
        }

        private static string ResetTarget(ZRpc requester)
        {
            if (!ZNet.instance || !ZNet.instance.IsServer()) return "Overhaul : serveur requis.";
            if (requester != null)
            {
                if (!Authorized(requester)) return "Overhaul : acces refuse, admin serveur et devcommands actifs requis (adminlist.txt).";
                var peer = ZNet.instance.GetPeer(requester);
                var character = peer.m_characterID.IsNone() ? null : ZDOMan.instance.GetZDO(peer.m_characterID);
                if (character == null) return "Overhaul : personnage du demandeur indisponible.";
                // Position from the authenticated character, never coordinates supplied in the RPC.
                return ResetAt(character, peer);
            }
            if (!Player.m_localPlayer) return "Overhaul : commande a lancer depuis la console d'un joueur administrateur connecte ; la console du serveur dedie n'a pas de position.";
            return ResetAt(Player.m_localPlayer.m_nview.GetZDO(), null);
        }

        private static string ResetAt(ZDO character, ZNetPeer peer)
        {
            if (!Character.InInterior(character.GetPosition())) return DungeonRuntime.RequestManualResetAt(character.GetPosition());
            if (evacuations.Any(e => e.World == ZDOMan.instance && e.Character == character.m_uid)) return "Overhaul : sortie du donjon deja en cours.";
            ZDO target;
            try { target = DungeonRuntime.FindManualResetTarget(character.GetPosition()); }
            catch (Exception e) { Utility.Log.LogError("Dungeon ciblage : " + e); return "Overhaul : impossible d'identifier le donjon ; aucun reset demande."; }
            if (target == null) return "Overhaul : donjon actuel introuvable ; aucun autre donjon ne sera cible.";
            // The authenticated character is demonstrably inside: this is a real first visit.
            if (target.GetLong(DungeonRuntime.VisitStartedKey, 0) == 0)
            {
                long now = DateTime.UtcNow.Ticks;
                target.SetOwner(ZNet.GetUID()); target.Set(DungeonRuntime.VisitStartedKey, now - now % TimeSpan.TicksPerMinute);
            }
            string refused = DungeonRuntime.ManualTargetRefusal(target);
            if (refused != null) return refused;
            var location = ZoneSystem.instance.GetLocation(target.GetInt(ZDOVars.s_location, 0));
            location.m_prefab.LoadAsync();
            evacuations.Add(new Evacuation { World = ZDOMan.instance, Peer = peer, Character = character.m_uid,
                Target = target.m_uid, Epoch = target.GetInt(DungeonRuntime.EpochKey, 0), Location = location,
                Deadline = Time.realtimeSinceStartup + 60f });
            return "Overhaul : sortie vers l'entree exterieure de " + location.m_prefab.Name + " " + target.m_uid + ", puis tentative de reset de ce donjon uniquement.";
        }

        internal static bool TryGetExterior(GameObject prefab, ZDO proxy, out Vector3 point, out Quaternion rotation)
        {
            point = Vector3.zero; rotation = Quaternion.identity;
            Teleport found = null;
            foreach (var teleport in prefab.GetComponentsInChildren<Teleport>(true))
            {
                var target = teleport.m_targetPoint;
                if (!target || !target.transform.IsChildOf(prefab.transform)) continue;
                if (prefab.transform.InverseTransformPoint(teleport.transform.position).y <= 3000 ||
                    prefab.transform.InverseTransformPoint(target.transform.position).y > 3000) continue;
                if (found && found != target) return false;
                found = target;
            }
            if (!found) return false;
            point = proxy.GetPosition() + proxy.GetRotation() * prefab.transform.InverseTransformPoint(found.GetTeleportPoint());
            rotation = proxy.GetRotation() * Quaternion.Inverse(prefab.transform.rotation) * found.transform.rotation;
            return !Character.InInterior(point);
        }

        private static void ReceiveExit(ZRpc rpc, Vector3 point, Quaternion rotation)
        {
            if (!ZNet.instance || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer()?.m_rpc != rpc || !Player.m_localPlayer) return;
            if (!Player.m_localPlayer.TeleportTo(point, rotation, false) && Console.instance)
                Console.instance.AddString("Overhaul : teleportation indisponible, le reset attend votre sortie puis sera annule apres 60 s.");
        }

        private static void CompleteEvacuation(int index, string message)
        {
            var work = evacuations[index]; evacuations.RemoveAt(index); work.Location.m_prefab.Release();
            if (work.World != ZDOMan.instance) return;
            if (work.Peer != null)
            { if (ZNet.instance && ZNet.instance.GetPeers().Contains(work.Peer)) work.Peer.m_rpc.Invoke(Response, message); }
            else if (Console.instance) Console.instance.AddString(message);
            Utility.Log.LogInfo(message);
        }

        internal static void Clear()
        {
            AdminCommandAccess.Clear();
            MudCollisionTest.Set(false);
            foreach (var work in evacuations) work.Location.m_prefab.Release();
            evacuations.Clear();
        }

        internal static void Tick()
        {
            AdminCommandAccess.Tick();
            MudCollisionTest.Tick();
            for (int i = evacuations.Count - 1; i >= 0; i--)
            {
                var work = evacuations[i];
                if(work.Peer!=null?!Authorized(work.Peer.m_rpc):!AdminCommandAccess.LocalEnabled){CompleteEvacuation(i,"Overhaul : reset annule, admin et devcommands actifs requis.");continue;}
                if (!ZNet.instance || !ZNet.instance.IsServer() || work.World != ZDOMan.instance)
                { CompleteEvacuation(i, "Overhaul : reset cible annule, monde ferme."); continue; }
                if (Time.realtimeSinceStartup >= work.Deadline || (work.Peer != null && (!ZNet.instance.GetPeers().Contains(work.Peer) || !work.Peer.IsReady())))
                { CompleteEvacuation(i, "Overhaul : reset cible annule, sortie non confirmee ou joueur deconnecte."); continue; }
                var character = work.World.GetZDO(work.Character); var target = work.World.GetZDO(work.Target);
                if (character == null || target == null || target.GetInt(DungeonRuntime.EpochKey, 0) != work.Epoch ||
                    (work.Peer != null && work.Peer.m_characterID != work.Character))
                { CompleteEvacuation(i, "Overhaul : reset cible annule, personnage ou generation change."); continue; }
                if (!work.Sent)
                {
                    if (!work.Location.m_prefab.IsLoaded) continue;
                    Quaternion rotation;
                    if (!TryGetExterior(work.Location.m_prefab.Asset, target, out work.Exit, out rotation))
                    { CompleteEvacuation(i, "Overhaul : sortie exterieure introuvable ; aucun reset effectue."); continue; }
                    work.Sent = true;
                    if (work.Peer != null) work.Peer.m_rpc.Invoke(ExitRequest, work.Exit, rotation);
                    else if (!Player.m_localPlayer || !Player.m_localPlayer.TeleportTo(work.Exit, rotation, false))
                    { CompleteEvacuation(i, "Overhaul : teleportation refusee ; aucun reset effectue."); }
                    continue;
                }
                // No client acknowledgement can authorize destruction: observe the actual character.
                if (Character.InInterior(character.GetPosition()) || (character.GetPosition() - work.Exit).sqrMagnitude > 256f) continue;
                if (work.Peer == null && Player.m_localPlayer && Player.m_localPlayer.IsTeleporting()) continue;
                CompleteEvacuation(i, DungeonRuntime.RequestManualResetTarget(target));
            }
        }

        private static void ReceiveTarget(ZRpc rpc)
        {
            if (ZNet.instance && ZNet.instance.IsServer()) rpc.Invoke(Response, ResetTarget(rpc));
        }

        private static void Reply(ZRpc rpc, string message)
        {
            if (!ZNet.instance || ZNet.instance.IsServer() || ZNet.instance.GetServerPeer()?.m_rpc != rpc) return;
            if (Console.instance) Console.instance.AddString(message);
        }

        internal static void Register(ZNetPeer peer)
        {
            AdminCommandAccess.Register(peer);
            peer.m_rpc.Register<bool>(MudRequest, (rpc, enabled) =>
            {
                if (!ZNet.instance || !ZNet.instance.IsServer()) return;
                if (!Authorized(rpc)) { rpc.Invoke(Response,"Overhaul : acces refuse, admin serveur et devcommands actifs requis."); return; }
                rpc.Invoke(MudResponse,enabled);
            });
            peer.m_rpc.Register<bool>(MudResponse, (rpc, enabled) =>
            {
                if (ZNet.instance && !ZNet.instance.IsServer() && ZNet.instance.GetServerPeer()?.m_rpc == rpc && (!enabled || AdminCommandAccess.LocalEnabled)) ApplyMud(enabled);
            });
            peer.m_rpc.Register(Request, new ZRpc.RpcMethod.Method(Receive));
            peer.m_rpc.Register(TargetRequest, new ZRpc.RpcMethod.Method(ReceiveTarget));
            peer.m_rpc.Register<Vector3, Quaternion>(ExitRequest, ReceiveExit);
            peer.m_rpc.Register<string>(Response, Reply);
            peer.m_rpc.Register<string>(ResetLevelRequest, ReceiveResetLevel);
            peer.m_rpc.Register<string,long>(ExperienceRequest,(rpc,target,amount)=>
            {
                if(ZNet.instance && ZNet.instance.IsServer())rpc.Invoke(Response,AdjustExperience(rpc,target,amount));
            });
        }
        private static void ApplyMud(bool enabled)
        {
            MudCollisionTest.Set(enabled);
            if (Console.instance) Console.instance.AddString(enabled
                ? "Overhaul : traversee des tas de boue activee pour votre personnage (test temporaire). o_testmud off pour retablir."
                : "Overhaul : collisions avec les tas de boue retablies.");
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    internal static class AdminConnectionPatch
    {
        private static void Postfix(ZNetPeer peer) { AdminCommands.Register(peer); }
    }
}
