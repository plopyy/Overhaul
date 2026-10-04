using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Overhaul.Persistence
{
    internal sealed class InventoryMoveRpc : IDisposable
    {
        private const string Request = "Overhaul_MoveItems", Response = "Overhaul_ItemsMoved", CloseRequest = "Overhaul_CloseInventory";
        private const string CancelEquipmentRequest = "Overhaul_CancelEquipment";
        private readonly ZRpc rpc;
        private readonly string nonce;
        private readonly InventoryMoveService server;
        private readonly PlayerProgressService progress;
        private readonly PlayerServerActions serverActions;
        private const string ServerActionResponse="Overhaul_ServerItemsChanged";
        private PlayerSnapshot canonical;
        private ZDOID knownActor;
        private System.Collections.Generic.Dictionary<string,double> wearRates=new System.Collections.Generic.Dictionary<string,double>();
        private readonly System.Collections.Generic.Dictionary<string,double> wearDebits=new System.Collections.Generic.Dictionary<string,double>();
        private double wearTime=Time.timeAsDouble,nextWear;
        private bool wearPending,identityPending;
        private void CaptureWear()
        {
            double now=Time.timeAsDouble,elapsed=Math.Max(0,now-wearTime);wearTime=now;
            if(disposed || elapsed==0 || PlayerSessionGame.Actor(rpc)==null)return;
            foreach(var entry in wearRates)
            {wearDebits.TryGetValue(entry.Key,out double previous);wearDebits[entry.Key]=previous+entry.Value*elapsed;}
        }
        private void QueueWear(bool final=false)
        {
            if(serverActions==null || wearPending && !final || wearDebits.Count==0)return;
            wearPending=true;
            if(!serverActions.Enqueue(state=>
            {
                CaptureWear();var debits=new System.Collections.Generic.Dictionary<string,double>(wearDebits);wearDebits.Clear();
                return GameEquipmentWear.Debit(state,debits);
            },()=>wearPending=false))wearPending=false;
        }
        private void IdentifyItems()
        {
            if(disposed || identityPending || serverActions==null || GameEquipmentWear.Identify(canonical)==null)return;
            identityPending=true;
            if(!serverActions.Enqueue(GameEquipmentWear.Identify,()=>identityPending=false))identityPending=false;
        }
        internal PlayerSnapshot SessionState(ZRpc connection)=>!disposed && server!=null && ReferenceEquals(rpc,connection)?canonical:null;
        private double pendingStamina;
        internal PlayerSnapshot State(ZDOID actor)
        {
            if(disposed || canonical==null || PlayerSessionGame.Actor(rpc)?.m_uid!=actor)return null;
            knownActor=actor;return canonical;
        }
        internal bool ActionBusy=>server?.Busy==true || serverActions?.Busy==true;
        internal bool Wear(ZDOID actor,string token,float amount)
        {
            var state=State(actor);if(state==null)return false;
            if(token==null || float.IsNaN(amount) || float.IsInfinity(amount) || amount<=0)return true;
            if(!state.Rows.Any(r=>r.Table=="item_data" && (string)r.Values[3]==GameEquipmentWear.Identity && (string)r.Values[4]==token))return true;
            float protection=PlayerCraftProgressGame.Passive(state,"artisan")?1:Mathf.Clamp01(PlayerCraftProgressGame.Bonus(state,"durability"));
            double cost=amount*(1-protection);if(cost<=0)return true;
            wearDebits.TryGetValue(token,out double previous);wearDebits[token]=previous+cost;QueueWear();return true;
        }
        internal bool ServerAction(ZDOID actor,Func<PlayerSnapshot,PlayerActionPlan> prepare,Action confirmed=null)
            =>!disposed && serverActions!=null && PlayerSessionGame.Actor(rpc)?.m_uid==actor && serverActions.Enqueue(prepare,confirmed);
        internal double Stamina(ZDOID actor)=>State(actor)==null?0:Math.Max(0,PlayerResources.Read(canonical,"stamina")-pendingStamina);
        internal bool Spending(ZDOID actor,float amount,float delay)
        {
            if(State(actor)==null || float.IsNaN(amount) || float.IsInfinity(amount) || amount<0 || float.IsNaN(delay) || float.IsInfinity(delay) || delay<0)return false;
            double debit=Math.Min(Stamina(actor),amount);if(debit==0)return true;
            pendingStamina+=debit;
            if(progress.Enqueue(state=>new[]{PlayerResources.Row("stamina",Math.Max(0,PlayerResources.Read(state,"stamina")-debit)),PlayerResources.Row(PlayerResources.StaminaDelay,delay)},()=>pendingStamina=Math.Max(0,pendingStamina-debit)))return true;
            pendingStamina-=debit;return false;
        }
        private void Committed(PlayerBatch batch,bool advance)
        {
            CaptureWear();
            bool wasAlive=PlayerResources.Read(canonical,"health")>0;
            var updated=PlayerProgressService.Overlay(canonical,batch.Changes);
            canonical=new PlayerSnapshot(batch.ExpectedRevision+(advance?1:0),updated.Rows);
            if(wasAlive!=(PlayerResources.Read(canonical,"health")>0) || batch.Changes.Any(r=>r.Table=="inventory" || r.Table=="item_data" || r.Table=="custom_data"))
                wearRates=GameEquipmentWear.Rates(canonical);
            if(batch.Changes.Any(r=>r.Table=="inventory"))IdentifyItems();
        }
        private byte[] deferredRequest;
        private bool discoveryPending;
        private float nextDiscovery;
        private int resourceFrame=-1;
        private double resourceElapsed;
        private bool resourcePending;
        private void QueueResources()
        {
            if(resourcePending || resourceElapsed<=0 || progress==null)return;
            var actor=PlayerSessionGame.Actor(rpc);if(actor==null)return;
            double seconds=resourceElapsed;resourceElapsed=0;resourcePending=true;
            if(!progress.Enqueue(state=>
            {
                resourcePending=false;double elapsed=seconds+resourceElapsed;resourceElapsed=0;
                return PlayerResourceGame.Simulate(state,actor,elapsed);
            }))resourcePending=false;
        }
        private readonly System.Collections.Generic.Dictionary<string,int> observedStations=new System.Collections.Generic.Dictionary<string,int>();
        private void Discover()
        {
            if(disposed || discoveryPending)return;
            discoveryPending=true;
            if(!progress.Enqueue(state=>{discoveryPending=false;return PlayerDiscoveryGame.Refresh(state);}))discoveryPending=false;
        }
        private const string ProgressResponse="Overhaul_ProgressChanged";
        private readonly InventoryMoveGame.Access access;
        internal readonly InventoryMoveController Controller;
        private bool disposed;
        internal bool Finished => disposed && (server == null || !server.Busy) && (progress==null || progress.Finished) && (serverActions==null || serverActions.Finished);
        internal bool StorageFailed=>server?.StorageFailed==true || progress?.Failed==true || serverActions?.Failed==true;
        internal bool Progress(ZDOID actor,Func<PlayerSnapshot,System.Collections.Generic.IEnumerable<PlayerChange>> action)
        {return !disposed && progress!=null && PlayerSessionGame.Actor(rpc)?.m_uid==actor && progress.Enqueue(action);}
        internal Container Container { get; private set; }
        private Inventory containerInventory;
        private InventoryMoveKind? afterOpen;
        private bool closeAfterMove;
        internal bool ViewActive { get; private set; }
        internal bool ManagedView(Container value) => value == Container && Controller != null && (ViewActive || Controller.Busy);
        internal bool Views(Container value, ZRpc except) => !disposed && rpc != except && access?.Views(value) == true;
        internal void Notify(Container value, PlayerBatch batch)
        {
            var id = value.m_nview.GetZDO().m_uid;
            Send(Response, InventoryMoveProtocol.Encode(new InventoryMoveReply { Nonce = nonce, Notification = true, Accepted = true, ContainerAllowed = true,
                ContainerUser = id.UserID, ContainerId = id.ID, Container = batch,
                Player = new PlayerBatch(batch.Operation, 0, new PlayerChange[0]) }));
        }

        internal InventoryMoveRpc(ZRpc rpc, PlayerAdmission.Session session, PlayerDatabaseWriter writer, InventoryMoveLayout layout)
        {
            if (!ReferenceEquals(rpc, session.Connection)) throw new ArgumentException("Wrong admitted inventory connection");
            this.rpc = rpc; nonce = session.Nonce; access = new InventoryMoveGame.Access(rpc, session);
            // Map blocks belong to admission/persistence, not the frequently updated
            // action view. Avoid copying the explored map on every resource tick.
            canonical=new PlayerSnapshot(session.Snapshot.Revision,session.Snapshot.Rows.Where(r=>PlayerDatabase.IsActionTable(r.Table)));
            progress=new PlayerProgressService(session.Identity,writer,batch=>
            {Committed(batch,false);if(!disposed)Send(ProgressResponse,InventoryMoveProtocol.Encode(new InventoryMoveReply{Nonce=nonce,Accepted=true,Player=batch}));},Fail);
            serverActions=new PlayerServerActions(session.Identity,writer,batch=>
            {
                Committed(batch,true);Discover();
                if(!disposed)Send(ServerActionResponse,InventoryMoveProtocol.Encode(new InventoryMoveReply{Nonce=nonce,Accepted=true,Player=batch}));
            },Fail);
            server = new InventoryMoveService(session, writer, layout, access.Reserve, bytes => Send(Response, bytes), Fail,
                (request,state) => PlayerActionGame.Prepare(rpc,session,request,state),
                (request,result,currentLayout) => PlayerEquipmentGame.PrepareMove(PlayerSessionGame.Actor(rpc),request,result,currentLayout),
                batch=>{Committed(batch,true);if(batch.Changes.Any(r=>r.Table=="inventory" || r.Table=="knowledge" && (string)r.Values[0]=="stations"))Discover();});
            foreach(var row in session.Snapshot.Rows.Where(r=>r.Table=="knowledge" && (string)r.Values[0]=="stations"))observedStations[(string)row.Values[1]]=Convert.ToInt32(row.Values[2]);
            wearRates=GameEquipmentWear.Rates(canonical);IdentifyItems();
            Discover();
            rpc.Register<ZPackage>(Request, Receive);
            rpc.Register<string,int,int>("Overhaul_FishingDraw",(sender,token,x,y)=>
            {if(ReferenceEquals(sender,rpc)&&token==nonce)PlayerFishingCastGame.Begin(PlayerSessionGame.Actor(rpc),x,y);});
            rpc.Register<string,bool,bool>("Overhaul_FishingControl",(sender,token,reel,cancel)=>
            {if(ReferenceEquals(sender,rpc)&&token==nonce)PlayerFishingCastGame.Control(PlayerSessionGame.Actor(rpc),reel,cancel);});
            rpc.Register<string,string>(CancelEquipmentRequest,(sender,token,operation) =>
            { if (ReferenceEquals(sender,rpc) && token == nonce) server.CancelEquipment(operation); });
            rpc.Register<string>(CloseRequest, (sender, token) => { if (ReferenceEquals(sender, rpc) && token == nonce) access.Close(); });
        }
        internal InventoryMoveRpc(ZRpc rpc, string nonce, long revision)
        {
            this.rpc = rpc; this.nonce = nonce;
            Controller = new InventoryMoveController(nonce, revision, bytes => Send(Request, bytes), Apply, Fail, ApplyServer);
            rpc.Register<ZPackage>(Response, Receive);
            rpc.Register<ZPackage>(ProgressResponse,ReceiveProgress);
            rpc.Register<ZPackage>(ServerActionResponse,ReceiveServerAction);
        }
        private void ReceiveServerAction(ZRpc sender,ZPackage package)
        {
            if(disposed || !ReferenceEquals(sender,rpc))return;
            try
            {
                if(package.Size()>InventoryMoveProtocol.Limit+4)throw new InvalidDataException("Server inventory response exceeds limit");
                int length=package.ReadInt();if(length<0 || length!=package.Size()-package.GetPos())throw new InvalidDataException("Invalid server inventory response length");
                Controller.ReceiveServer(package.ReadByteArray(length));
            }
            catch(Exception error){Fail(error);}
        }
        private void ApplyServer(InventoryMoveReply reply)
        {
            var player=Player.m_localPlayer;if(!player)throw new InvalidDataException("Player disappeared during server inventory update");
            InventoryMovePresentation.Stage(player.GetInventory(),null,reply,player)();
            var gui=InventoryGui.instance;
            if(!gui)return;
            if(gui.m_dragInventory==player.GetInventory() && gui.m_dragItem!=null &&
                reply.Player.Changes.Where(r=>r.Table=="inventory" || r.Table=="item_data").Any(r=>Convert.ToInt32(r.Values[1])==gui.m_dragItem.m_gridPos.x && Convert.ToInt32(r.Values[2])==gui.m_dragItem.m_gridPos.y))gui.SetupDragItem(null,null,1);
            gui.UpdateCraftingPanel(false);
        }
        private void ReceiveProgress(ZRpc sender,ZPackage package)
        {
            if(disposed || !ReferenceEquals(sender,rpc))return;
            try
            {
                if(package.Size()>InventoryMoveProtocol.Limit+4)throw new InvalidDataException("Progress response exceeds limit");
                int length=package.ReadInt();if(length<0 || length!=package.Size()-package.GetPos())throw new InvalidDataException("Invalid progress response length");
                var reply=InventoryMoveProtocol.Reply(package.ReadByteArray(length));if(reply.Nonce!=nonce)return;
                if(!reply.Accepted || reply.Snapshot || reply.Notification || reply.ContainerAllowed || reply.Player.Changes.Any(r=>!PlayerProgressService.Allowed(r)))throw new InvalidDataException("Invalid server progress response");
                var progressView=PlayerCraftProgressGame.Presentation(reply.Player.Changes.Where(r=>r.Table=="skills" || r.Table=="knowledge"),Player.m_localPlayer);
                var resourceView=PlayerResourceGame.Presentation(reply.Player.Changes.Where(r=>r.Table=="state" && PlayerResources.IsKey((string)r.Values[0])),Player.m_localPlayer);
                var foodView=PlayerFoodGame.Presentation(reply.Player.Changes.Where(r=>r.Table=="food"),Player.m_localPlayer);
                var effectView=PlayerPotionGame.Presentation(reply.Player.Changes.Where(r=>r.Table=="effects"),Player.m_localPlayer);
                progressView();foodView();effectView();resourceView();
            }
            catch(Exception error){Fail(error);}
        }
        internal bool Open(Container chest, InventoryMoveKind? then = null)
        {
            if (Controller == null || Controller.Busy || Controller.Closed || !chest || !chest.m_nview || !chest.m_nview.IsValid()) return false;
            Container = chest; containerInventory = chest.GetInventory(); afterOpen = then; ViewActive = true;
            var id = chest.m_nview.GetZDO().m_uid; return Controller.Open(id.UserID, id.ID);
        }
        internal void CloseContainer()
        { if (!disposed) rpc.Invoke(CloseRequest, nonce); afterOpen = null; closeAfterMove = false; ViewActive = false; }
        internal void CancelEquipment()
        { if (!disposed && Controller?.Pending != null) rpc.Invoke(CancelEquipmentRequest,nonce,Controller.Pending.Action.Operation); }
        internal void BeginFishingDraw(int x,int y){if(!disposed && Controller!=null)rpc.Invoke("Overhaul_FishingDraw",nonce,x,y);}
        internal void FishingControl(bool reel,bool cancel){if(!disposed && Controller!=null)rpc.Invoke("Overhaul_FishingControl",nonce,reel,cancel);}
        private void Apply(InventoryMoveReply reply)
        {
            var player = Player.m_localPlayer;
            if (!player) throw new InvalidDataException("Player disappeared during inventory action");
            var eating = !reply.Notification && reply.Accepted && Controller.Pending?.Gameplay?.Kind == PlayerActionKind.Consume;
            if (!reply.Notification) PlayerEquipmentGame.Confirm(player,Controller.Pending);
            InventoryMovePresentation.Stage(player.GetInventory(), containerInventory, reply, player)();
            if(!reply.Notification)PlayerFishingGame.Confirm(Controller.Pending);
            if(!reply.Notification && reply.Accepted)PlayerBuildGame.Feedback(player,Controller.Pending);
            if (eating) PlayerFoodGame.Feedback(player,reply.Player);
            if (!reply.Notification && reply.Accepted && Controller.Pending?.Gameplay?.Kind == PlayerActionKind.Trash) PlayerDropGame.TrashFeedback();
            if (!reply.Notification && reply.Accepted && (Controller.Pending?.Gameplay?.Kind == PlayerActionKind.Buy || Controller.Pending?.Gameplay?.Kind == PlayerActionKind.Sell))
                PlayerTradeGame.Feedback(Controller.Pending.Gameplay);
            var gui = InventoryGui.instance;
            if (reply.Notification)
            {
                if (gui && gui.m_dragInventory == containerInventory && gui.m_dragItem != null &&
                    ContainerVersions.Slots(reply.Container).Contains(gui.m_dragItem.m_gridPos.y * 256 + gui.m_dragItem.m_gridPos.x))
                    gui.SetupDragItem(null, null, 1);
                return;
            }
            if (gui)
            {
                gui.SetupDragItem(null, null, 1);
                if (Controller.Pending.Open && reply.Accepted && Container && ViewActive && !afterOpen.HasValue) gui.Show(Container, 1);
                else if (!reply.ContainerAllowed && (Controller.Pending.Open || Controller.Pending.Action.UsesContainer) && gui.m_currentContainer == Container) gui.Hide();
                gui.UpdateCraftingPanel(false);
            }
            if (!reply.Accepted) { afterOpen = null; player.Message(MessageHud.MessageType.Center, "$msg_cantopen"); }
        }
        private void Receive(ZRpc sender, ZPackage package)
        {
            if (disposed || !ReferenceEquals(sender, rpc)) return;
            try
            {
                int limit = server != null ? 65536 : InventoryMoveProtocol.Limit;
                if (package.Size() > limit + 4) throw new InvalidDataException("Inventory RPC exceeds limit");
                int length = package.ReadInt();
                if (length < 0 || length != package.Size() - package.GetPos()) throw new InvalidDataException("Invalid inventory RPC length");
                byte[] bytes = package.ReadByteArray(length);
                if(server!=null){CaptureWear();QueueWear();}
                // Start already accepted simulation events before reading a new action snapshot.
                if(server!=null && !server.Busy)
                {serverActions.Tick(progress.Busy);progress.Tick(serverActions.Busy);}
                if(server!=null && (progress.Busy || serverActions.Busy))
                {if(deferredRequest!=null)throw new InvalidDataException("Multiple requests during server progress update");deferredRequest=bytes;}
                else if (server != null) server.Receive(bytes); else Controller.Receive(bytes);
            }
            catch (Exception error) { Fail(error); }
        }
        private void Send(string name, byte[] bytes) { var package = new ZPackage(); package.Write(bytes); rpc.Invoke(name, package); }
        internal void Tick()
        {
            if (!disposed && !rpc.IsConnected()) Dispose();
            if(!disposed && serverActions!=null)
            {
                CaptureWear();
                if(Time.timeAsDouble>=nextWear){nextWear=Time.timeAsDouble+.2;QueueWear();}
            }
            if(!disposed && progress!=null && resourceFrame!=Time.frameCount)
            {
                resourceFrame=Time.frameCount;
                if(PlayerSessionGame.Actor(rpc)!=null)resourceElapsed+=Time.deltaTime;
                if(resourceElapsed>=.2)QueueResources();
            }
            if(!disposed && progress!=null && Time.time>=nextDiscovery)
            {
                nextDiscovery=Time.time+2;
                var stations=PlayerDiscoveryGame.Nearby(PlayerSessionGame.Actor(rpc),observedStations);
                if(stations.Length!=0)progress.Enqueue(state=>PlayerDiscoveryGame.Refresh(state,stations));
            }
            server?.Tick();
            serverActions?.Tick(server?.Busy==true || progress?.Busy==true || deferredRequest!=null);
            progress?.Tick(server?.Busy==true || serverActions?.Busy==true || deferredRequest!=null);
            if(!disposed && deferredRequest!=null && !progress.Busy && !serverActions.Busy)
            {var request=deferredRequest;deferredRequest=null;server.Receive(request);}
            if (disposed) return;
            Controller?.Tick();
            if(Controller!=null)PlayerFishingCastGame.ClientTick();
            if (closeAfterMove && Controller != null && !Controller.Busy) CloseContainer();
            if (afterOpen.HasValue && Controller != null && !Controller.Busy && Controller.ContainerId != 0)
            {
                var kind = afterOpen.Value; afterOpen = null;
                closeAfterMove = true;
                Controller.Move(kind, kind == InventoryMoveKind.TakeAll ? 1 : 0, kind == InventoryMoveKind.TakeAll ? 0 : 1, 0, 0, 0, 0, 1);
            }
        }
        private void Fail(Exception error)
        {
            ZLog.LogError("[Overhaul inventory] " + error);
            Dispose(); rpc.GetSocket().Close();
            if (server?.StorageFailed == true || progress?.Failed == true || serverActions?.Failed==true) ZNet.m_loadError = true;
        }
        private static void Ignore(ZRpc sender, ZPackage package) { }
        public void Dispose()
        {
            if (disposed) return;
            if(serverActions!=null){CaptureWear();QueueWear(true);}
            QueueResources();disposed = true; Controller?.Dispose(); server?.Dispose(); access?.Dispose();
            progress?.Close();serverActions?.Close();deferredRequest=null;
            if(Controller!=null){PlayerFishingGame.Clear();PlayerFishingCastGame.ClearClient();}
            else {var actor=PlayerSessionGame.Actor(rpc)?.m_uid??knownActor;if(!actor.IsNone())PlayerFishingCastGame.Forget(actor);}
            rpc.Register<ZPackage>(server != null ? Request : Response, Ignore);
            if(server==null)rpc.Register<ZPackage>(ProgressResponse,Ignore);
            if(server==null)rpc.Register<ZPackage>(ServerActionResponse,Ignore);
            if (server != null) rpc.Register<string>(CloseRequest, (_, __) => { });
            if (server != null) rpc.Register<string,string>(CancelEquipmentRequest, (_, __, ___) => { });
            if (server != null) rpc.Register<string,int,int>("Overhaul_FishingDraw",(_,__,___,____)=>{ });
            if (server != null) rpc.Register<string,bool,bool>("Overhaul_FishingControl",(_,__,___,____)=>{ });
        }
    }
}
