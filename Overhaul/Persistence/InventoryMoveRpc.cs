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
        private readonly InventoryMoveGame.Access access;
        internal readonly InventoryMoveController Controller;
        private bool disposed;
        internal bool Finished => disposed && (server == null || !server.Busy);
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
            server = new InventoryMoveService(session, writer, layout, access.Reserve, bytes => Send(Response, bytes), Fail,
                (request,state) => PlayerActionGame.Prepare(rpc,session,request,state),
                (request,result,currentLayout) => PlayerEquipmentGame.PrepareMove(PlayerSessionGame.Actor(rpc),request,result,currentLayout));
            rpc.Register<ZPackage>(Request, Receive);
            rpc.Register<string,string>(CancelEquipmentRequest,(sender,token,operation) =>
            { if (ReferenceEquals(sender,rpc) && token == nonce) server.CancelEquipment(operation); });
            rpc.Register<string>(CloseRequest, (sender, token) => { if (ReferenceEquals(sender, rpc) && token == nonce) access.Close(); });
        }
        internal InventoryMoveRpc(ZRpc rpc, string nonce, long revision)
        {
            this.rpc = rpc; this.nonce = nonce;
            Controller = new InventoryMoveController(nonce, revision, bytes => Send(Request, bytes), Apply, Fail);
            rpc.Register<ZPackage>(Response, Receive);
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
        private void Apply(InventoryMoveReply reply)
        {
            var player = Player.m_localPlayer;
            if (!player) throw new InvalidDataException("Player disappeared during inventory action");
            var eating = !reply.Notification && reply.Accepted && Controller.Pending?.Gameplay?.Kind == PlayerActionKind.Consume;
            if (!reply.Notification) PlayerEquipmentGame.Confirm(player,Controller.Pending);
            InventoryMovePresentation.Stage(player.GetInventory(), containerInventory, reply, player)();
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
                if (server != null) server.Receive(bytes); else Controller.Receive(bytes);
            }
            catch (Exception error) { Fail(error); }
        }
        private void Send(string name, byte[] bytes) { var package = new ZPackage(); package.Write(bytes); rpc.Invoke(name, package); }
        internal void Tick()
        {
            if (!disposed && !rpc.IsConnected()) Dispose();
            server?.Tick();
            if (disposed) return;
            Controller?.Tick();
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
            if (server?.StorageFailed == true) ZNet.m_loadError = true;
        }
        private static void Ignore(ZRpc sender, ZPackage package) { }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; Controller?.Dispose(); server?.Dispose(); access?.Dispose();
            rpc.Register<ZPackage>(server != null ? Request : Response, Ignore);
            if (server != null) rpc.Register<string>(CloseRequest, (_, __) => { });
            if (server != null) rpc.Register<string,string>(CancelEquipmentRequest, (_, __, ___) => { });
        }
    }
}
