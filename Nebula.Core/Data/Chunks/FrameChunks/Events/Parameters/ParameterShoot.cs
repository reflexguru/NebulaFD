using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.FrameChunks.Events;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Memory;

namespace Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters
{
    public class ParameterShoot : ParameterChunk
    {
        public BitDict ShootFlags = new BitDict( // Shoot Flags
            "", "", "CalculateDirection" // Launch in select directions Disabled
        );

        public ushort ObjectInfoParent;
        public short X;
        public short Y;
        public short Slope;
        public short Angle;
        public int Direction;
        public short TypeParent;
        public short ObjectInfoList;
        public short Layer;

        public ushort ObjectInstance;
        public ushort ObjectInfo;
        public short ShootSpeed;

        public ParameterShoot()
        {
            ChunkName = "ParameterShoot";
        }

        public override void ReadCCN(ByteReader reader, params object[] extraInfo)
        {
            ObjectInfoParent = reader.ReadUShort();
            ShootFlags.Value = reader.ReadUShort();
            X = reader.ReadShort();
            Y = reader.ReadShort();
            Slope = reader.ReadShort();
            Angle = reader.ReadShort();
            Direction = reader.ReadInt();
            TypeParent = reader.ReadShort();
            ObjectInfoList = reader.ReadShort();
            Layer = reader.ReadShort();

            ObjectInstance = reader.ReadUShort();
            ObjectInfo = reader.ReadUShort();
            reader.Skip(4);
            ShootSpeed = reader.ReadShort();
        }

        public override void WriteMFA(ByteWriter writer, params object[] extraInfo)
        {
            //if (FrameEvents.QualifierJumptable.ContainsKey(ObjectInfo))
                //ObjectInfo = FrameEvents.QualifierJumptable[ObjectInfo];
            if (FrameEvents.QualifierJumptable.ContainsKey(Tuple.Create(ObjectInfoParent, TypeParent)))
                ObjectInfoParent = FrameEvents.QualifierJumptable[Tuple.Create(ObjectInfoParent, TypeParent)];

            writer.WriteUShort(ObjectInfoParent);
            writer.WriteUShort((ushort)ShootFlags.Value);
            writer.WriteShort(X);
            writer.WriteShort(Y);
            writer.WriteShort(Slope);
            writer.WriteShort(Angle);
            writer.WriteInt(Direction);
            writer.WriteShort(TypeParent);
            writer.WriteShort(ObjectInfoList);
            writer.WriteShort(Layer);

            writer.WriteUShort(ObjectInstance);
            writer.WriteUShort(ObjectInfo);
            writer.WriteInt(0);
            writer.WriteShort(ShootSpeed);
        }

        string ResolveShootObjectName()
        {
            FrameEvents? fe = Parent?.FrameEvents;
            if (fe != null)
            {
                if (fe.EventObjects.TryGetValue(ObjectInfo, out EventObject? eo))
                {
                    if (eo.ObjectType == 1)
                    {
                        int handle = (int)eo.ItemHandle;
                        Frame? frame = fe.Parent;
                        if (frame?.FrameObjectItems.TryGetValue(handle, out ObjectInfo? localOi) == true && localOi != null)
                            return localOi.Name;
                        if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(handle, out localOi) && localOi != null)
                            return localOi.Name;
                    }
                    if (!string.IsNullOrEmpty(eo.Name))
                        return eo.Name;
                }
                ObjectInfo? resolved = fe.ResolveObject(ObjectInfo);
                if (resolved != null && !string.IsNullOrEmpty(resolved.Name))
                    return resolved.Name;
            }
            if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(ObjectInfo, out ObjectInfo? fallbackOi) && fallbackOi != null)
                return fallbackOi.Name;
            return "Unknown Object";
        }

        public override string ToString()
        {
            string output = ResolveShootObjectName();
            if (!ShootFlags["CalculateDirection"])
                output += " toward " + GetDirection();
            return output + " at speed " + ShootSpeed;
        }

        public string GetDirection()
        {
            return ACEventBase.FormatDirectionMask((uint)Direction, ACEventBase.DirectionMaskStyle.Pick);
        }
    }
}
