using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Memory;
using System.Drawing;

namespace Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters
{
    public class ParameterGroupPointer : ParameterChunk
    {
        public long CCNPointer;
        public int Pointer;
        public int ID;

        public ParameterGroupPointer()
        {
            ChunkName = "ParameterGroupPointer";
        }

        public override void ReadCCN(ByteReader reader, params object[] extraInfo)
        {
            Pointer = reader.ReadInt();
            ID = reader.ReadInt();

            if (Pointer == 0)
                CCNPointer = 0;
            else
            {
                CCNPointer = reader.Tell() - 12 + Pointer;

                if (NebulaCore.Build < 284)
                    CCNPointer -= 2;
            }
                
        }

        public override void WriteMFA(ByteWriter writer, params object[] extraInfo)
        {
            writer.WriteInt(0); // Pointer goes unused by the Editor
            writer.WriteInt(ID);
        }

        public override string ToString()
        {
            Frame? frame = Parent?.FrameEvents?.Parent;
            if (frame != null)
            {
                if (ID >= short.MinValue && ID <= short.MaxValue &&
                    frame.GroupLookupTable.TryGetValue((short)ID, out ParameterGroup? group))
                    return group.Name;

                foreach (EventGroup eg in frame.FrameEvents.EventGroups)
                    if (eg.Handle == ID)
                        return eg.Name;
            }

            return $"Group {ID}";
        }
    }
}
