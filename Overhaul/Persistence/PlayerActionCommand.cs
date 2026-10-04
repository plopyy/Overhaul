using System;
using System.IO;
using System.Linq;

namespace Overhaul.Persistence
{
    internal enum PlayerActionKind { Pickup, Drop, Consume, Equip, Unequip, Interact, UseOn, Craft, Build, Repair, Buy, Sell, Attack, Death, Trash }
    // Contains intent only. Item definitions, resource costs and resulting rows are server-owned.
    internal sealed class PlayerActionCommand
    {
        internal PlayerActionKind Kind;
        internal long TargetUser;
        internal uint TargetId;
        internal string Definition = "";
        internal int Variant;
        internal bool Alternate, Held;
        internal float[] Position = new float[3], Rotation = new float[] { 0, 0, 0, 1 };
        internal void Write(BinaryWriter writer)
        {
            Validate(); writer.Write((byte)Kind); writer.Write(TargetUser); writer.Write(TargetId); writer.Write(Definition);
            writer.Write(Variant); writer.Write(Alternate); writer.Write(Held);
            foreach (float value in Position) writer.Write(value); foreach (float value in Rotation) writer.Write(value);
        }
        internal static PlayerActionCommand Read(BinaryReader reader)
        {
            var value = new PlayerActionCommand { Kind = (PlayerActionKind)reader.ReadByte(), TargetUser = reader.ReadInt64(), TargetId = reader.ReadUInt32(),
                Definition = reader.ReadString(), Variant = reader.ReadInt32(), Alternate = reader.ReadBoolean(), Held = reader.ReadBoolean(),
                Position = Enumerable.Range(0,3).Select(_ => reader.ReadSingle()).ToArray(), Rotation = Enumerable.Range(0,4).Select(_ => reader.ReadSingle()).ToArray() };
            value.Validate(); return value;
        }
        private void Validate()
        {
            if (!Enum.IsDefined(typeof(PlayerActionKind), Kind) || Definition == null || Definition.Length > 128 || Variant < 0 || Variant > 65535 ||
                Position == null || Position.Length != 3 || Rotation == null || Rotation.Length != 4 ||
                Position.Concat(Rotation).Any(n => float.IsNaN(n) || float.IsInfinity(n)) || Position.Any(n => Math.Abs(n) > 1000000))
                throw new InvalidDataException("Invalid player action intent");
        }
    }
}
