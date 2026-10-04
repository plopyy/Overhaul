using System;
using System.Linq;

namespace Overhaul.Persistence
{
    // Last confirmed server rows, used to rebuild the transient spawn profile.
    // Never captures a native client inventory or writes a local character file.
    internal static class GameCharacterView
    {
        internal static PlayerSnapshot State { get; private set; }
        internal static void Initial(PlayerSnapshot snapshot)=>State=snapshot==null?null:new PlayerSnapshot(snapshot.Revision,snapshot.Rows.Where(r=>PlayerDatabase.IsActionTable(r.Table)));
        internal static void Confirm(PlayerBatch batch,bool inventorySnapshot=false,bool advance=false)
        {
            if(State==null)return;
            var before=inventorySnapshot?new PlayerSnapshot(State.Revision,State.Rows.Where(r=>r.Table!="inventory"&&r.Table!="item_data")):State;
            var updated=PlayerProgressService.Overlay(before,batch.Changes);
            State=new PlayerSnapshot(advance?checked(batch.ExpectedRevision+1):Math.Max(State.Revision,batch.ExpectedRevision),updated.Rows);
        }
        internal static void PrepareSpawn()
        {
            if(State==null||!Game.instance)throw new InvalidOperationException("Confirmed character spawn data is unavailable");
            var rows=State.Rows.ToArray();
            byte[] native=PlayerNativeFormat.Encode(rows,PlayerSessionGame.Appearance());
            Game.instance.GetPlayerProfile().m_playerData=native;
            PlayerPotionGame.Initial(rows);GameStatusGame.Initial(rows);GameLifeView.Initial(rows);PlayerResourceGame.Initial(rows);
        }
    }
}
