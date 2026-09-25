using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.FrameChunks.Events;
using Nebula.Core.Memory;
using System.Drawing;

namespace Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters
{
    /// <summary>
    /// PARAM_REMARK (code 37). Fusion comment/remark: 16-bit LOGFONT, bar color,
    /// alignment, handle into the Rems chunk, and font style name.
    /// </summary>
    public class ParameterRemark : ParameterChunk
    {
        public short Height;
        public short Width;
        public short Escapement;
        public short Orientation;
        public short Weight;
        public byte Italic;
        public byte Underline;
        public byte StrikeOut;
        public byte CharSet;
        public byte OutPrecision;
        public byte ClipPrecision;
        public byte Quality;
        public byte PitchAndFamily;
        public string FaceName = string.Empty;
        public Color Color = Color.Lime;
        public short Alignment;
        public short CommentHandle;
        public string StyleName = string.Empty;
        public byte[] RawData = new byte[0];

        public ParameterRemark()
        {
            ChunkName = "ParameterRemark";
        }

        public override void ReadCCN(ByteReader reader, params object[] extraInfo)
        {
            int dataSize = extraInfo.Length > 0 && extraInfo[0] is int size ? size : -1;
            ReadFrom(reader, dataSize);
        }

        public void ReadFrom(ByteReader reader, int dataSize)
        {
            long start = reader.Tell();
            if (dataSize <= 0)
                dataSize = (int)Math.Max(0, reader.Size() - start);

            RawData = reader.ReadBytes(dataSize);
            reader.Seek(start);

            if (dataSize < 18)
            {
                if (dataSize >= 2)
                    Height = reader.ReadShort();
                reader.Seek(start + dataSize);
                return;
            }

            Height = reader.ReadShort();
            Width = reader.ReadShort();
            Escapement = reader.ReadShort();
            Orientation = reader.ReadShort();
            Weight = reader.ReadShort();
            Italic = reader.ReadByte();
            Underline = reader.ReadByte();
            StrikeOut = reader.ReadByte();
            CharSet = reader.ReadByte();
            OutPrecision = reader.ReadByte();
            ClipPrecision = reader.ReadByte();
            Quality = reader.ReadByte();
            PitchAndFamily = reader.ReadByte();

            bool unicode = NebulaCore.Yunicode;
            int faceChars = unicode ? 32 : 32;
            FaceName = unicode ? reader.ReadYunicodeStop(faceChars) : reader.ReadAsciiStop(faceChars);

            long afterFace = start + 18 + (unicode ? 64 : 32);
            reader.Seek(afterFace);

            int remaining = dataSize - (int)(reader.Tell() - start);
            if (remaining >= 12)
            {
                reader.Skip(4);
                byte r = reader.ReadByte();
                byte g = reader.ReadByte();
                byte b = reader.ReadByte();
                reader.ReadByte();
                Color = Color.FromArgb(255, r, g, b);
                Alignment = reader.ReadShort();
                CommentHandle = reader.ReadShort();
                remaining = dataSize - (int)(reader.Tell() - start);
                if (remaining >= 2)
                {
                    int styleChars = remaining / (unicode ? 2 : 1);
                    StyleName = unicode ? reader.ReadYunicodeStop(styleChars) : reader.ReadAsciiStop(styleChars);
                }
            }

            reader.Seek(start + dataSize);
        }

        public override void WriteMFA(ByteWriter writer, params object[] extraInfo)
        {
            if (RawData.Length > 0)
            {
                writer.WriteBytes(RawData);
                return;
            }

            writer.WriteShort(Height);
            writer.WriteShort(Width);
            writer.WriteShort(Escapement);
            writer.WriteShort(Orientation);
            writer.WriteShort(Weight);
            writer.WriteByte(Italic);
            writer.WriteByte(Underline);
            writer.WriteByte(StrikeOut);
            writer.WriteByte(CharSet);
            writer.WriteByte(OutPrecision);
            writer.WriteByte(ClipPrecision);
            writer.WriteByte(Quality);
            writer.WriteByte(PitchAndFamily);
            writer.WriteYunicode(FaceName, 32);
            writer.WriteInt(0);
            writer.WriteByte(Color.R);
            writer.WriteByte(Color.G);
            writer.WriteByte(Color.B);
            writer.WriteByte(0);
            writer.WriteShort(Alignment);
            writer.WriteShort(CommentHandle);
            writer.WriteYunicode(StyleName, 40);
        }

        public string ResolveText()
        {
            FrameEvents? fe = Parent?.FrameEvents;
            if (fe?.Comments == null)
                return string.Empty;
            foreach (Comment comment in fe.Comments)
                if (comment.Handle == CommentHandle)
                    return comment.Value;
            return string.Empty;
        }

        public string AlignmentName => Alignment switch
        {
            0 => "Left",
            1 => "Center",
            2 => "Right",
            _ => "Left"
        };

        public override string ToString()
        {
            string text = ResolveText();
            return string.IsNullOrEmpty(text) ? "Comment" : text;
        }
    }
}
