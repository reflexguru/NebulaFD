using Nebula;
using Nebula.Core.Data.Chunks.AppChunks;
using Nebula.Core.Data.Chunks.FrameChunks.Events;
using Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Memory;
using Nebula.Core.Utilities;

namespace Nebula.Core.Data.Chunks.FrameChunks
{
    public class FrameEvents : Chunk
    {
        public BitDict OptionFlags = new BitDict( // Option Flags
            "BreakChild" // Break Child
        );

        public int MaxObjects;
        public short MaxObjectInfos;
        public short NumberOfPlayers;
        public short[] ConditionCount = new short[17];
        public List<Qualifier> Qualifiers = new();

        public int EventCount;
        public List<Event> Events = new();

        // For MFA
        public ushort Version;
        public ushort FrameType;
        public Comment[] Comments = new Comment[0];
        public EventGroup[] EventGroups = new EventGroup[0];
        public Dictionary<int, EventObject> EventObjects = new();
        public int EditorData;
        public ushort ConditionWidth;
        public short ObjectHeight;
        public ushort[] ObjectTypes = new ushort[0];
        public ushort[] ObjectHandles = new ushort[0];
        public ushort[] ObjectFlags = new ushort[0];
        public string[] Folders = new string[0];
        public byte[] TimeListData = new byte[0];
        public uint EditorX;
        public uint EditorY;
        public uint EditorCaretType;
        public uint EditorCaretX;
        public uint EditorCaretY;
        public uint EditorLineY;
        public uint EditorLineType;
        public uint EventLineY;
        public uint EventLineType;

        public Frame? Parent = null;

        /// <summary>
        /// Key is [ObjectInfo, Type]
        /// </summary>
        public static Dictionary<Tuple<ushort, short>, ushort> QualifierJumptable = new();
        public static bool OptimizedEvents;

        public FrameEvents()
        {
            ChunkName = "FrameEvents";
            ChunkID = 0x333D;
        }

        public override void ReadCCN(ByteReader reader, params object[] extraInfo)
        {
            if (Parameters.DontIncludeEvents)
                return;

			(Parent = (Frame)extraInfo[0]).FrameEvents = this;
			while (true)
            {
                string identifier = reader.ReadAscii(4);

                if (identifier == "ER>>" || identifier == "KR>>")
                {
                    MaxObjects = reader.ReadShort();
                    MaxObjectInfos = reader.ReadShort();
                    NumberOfPlayers = reader.ReadShort();

                    for (int i = 0; i < ConditionCount.Length; i++)
                        ConditionCount[i] = reader.ReadShort();

                    int qualifierCount = reader.ReadShort();
                    for (int i = 0; i < qualifierCount; i++)
                    {
                        Qualifier qualifier = new Qualifier();
						qualifier.ReadCCN(reader);
                        Qualifiers.Add(qualifier);
                    }

                    // Just incase, 296 shouldnt be writing qualifiers anyway
                    if (NebulaCore.Build >= 296 && NebulaCore.Windows && NebulaCore.Fusion >= 2.5)
                        Qualifiers.Clear();
                }
                else if (identifier == "ERes")
                    EventCount = reader.ReadInt();
                else if (identifier == "ERev")
                {
                    // Seperated bc of issues
                    if (NebulaCore.Android || NebulaCore.HTML)
                    {
                        reader.Skip(4);
                        int count = reader.ReadInt();
                        for (int i = 0; i < count; i++)
                        {
                            Event newEvent = new Event();
                            newEvent.Parent = this;
                            newEvent.ReadCCN(reader);
                            Events.Add(newEvent);
                        }
                    }
                    else
                    {
                        long endPosition = reader.Tell() + reader.ReadInt();
                        if (Parameters.DontIncludeEvents)
                            reader.Seek(endPosition);
                        while (reader.Tell() < endPosition)
                        {
                            Event newEvent = new Event();
                            newEvent.Parent = this;
                            newEvent.ReadCCN(reader);
                            Events.Add(newEvent);
                        }
                    }
                }
                else if (identifier == "ERop")
                    OptionFlags.Value = reader.ReadUInt();
                else if (identifier == "<<ER")
                    break;
            }
        }

        public override void ReadMFA(ByteReader reader, params object[] extraInfo)
        {
            uint size = reader.ReadUInt();
            long endOffset = reader.Tell() + size;
            if (size == 0 && extraInfo.Length > 0)
                return;

            if (extraInfo[0] is Frame parentFrame)
			    Parent = parentFrame;

			while (true)
            {
                string identifier = reader.ReadAscii(4);

                if (identifier == "Evts" || identifier == "STVE")
                {
                    long endPosition = reader.Tell() + reader.ReadInt();
                    while (reader.Tell() < endPosition)
                    {
                        if (endPosition - reader.Tell() < 2 || reader.PeekShort() == 0)
                            break;
                        Event newEvent = new Event();
                        newEvent.Parent = this;
                        newEvent.ReadMFA(reader);
                        Events.Add(newEvent);
                    }
                    reader.Seek(endPosition);
                }
                else if (identifier == "Rems" || identifier == "SMER")
                {
                    Comments = new Comment[reader.ReadInt()];
                    for (int i = 0; i < Comments.Length; i++)
                    {
                        Comments[i] = new Comment();
                        Comments[i].ReadMFA(reader);
                    }
                }
                else if (identifier == "SPRG")
                {
                    EventGroups = new EventGroup[reader.ReadInt()];
                    reader.Skip(4); // Max Handle
                    for (int i = 0; i < EventGroups.Length; i++)
                    {
                        EventGroups[i] = new EventGroup();
                        EventGroups[i].ReadMFA(reader);
                    }

                    // Last group's UUID is a 75-wchar slot; MFA can omit the
                    // final 2 bytes when TYAL follows immediately.
                    if (reader.HasMemory(4))
                    {
                        long tagPos = reader.Tell();
                        string next = reader.ReadAscii(4);
                        reader.Seek(tagPos);
                        if (!IsMfaEventTag(next) && tagPos >= 2)
                        {
                            reader.Seek(tagPos - 2);
                            next = reader.ReadAscii(4);
                            reader.Seek(tagPos - 2);
                            if (!IsMfaEventTag(next))
                                reader.Seek(tagPos);
                        }
                    }
                }
                else if (identifier == "EvOb" || identifier == "SJBO")
                {
                    EventObjects = new();
                    int cnt = reader.ReadInt();
                    for (int i = 0; i < cnt; i++)
                    {
                        EventObject evtObj = new EventObject();
                        evtObj.ReadMFA(reader);
                        EventObjects.Add(evtObj.Handle, evtObj);
                    }
                }
                else if (identifier == "EvCs")
                {
                    EditorData = reader.ReadInt();
                    ConditionWidth = reader.ReadUShort();
                    ObjectHeight = reader.ReadShort();
                    reader.Skip(12);
                }
                else if (identifier == "EvEd")
                {
                    short header = reader.ReadShort();
                    short objectCount = header == -1 ? reader.ReadShort() : header;

                    ObjectTypes = new ushort[objectCount];
                    ObjectHandles = new ushort[objectCount];
                    ObjectFlags = new ushort[objectCount];
                    
                    for (int i = 0; i < objectCount * 3; i++)
                    {
                        if (i < objectCount)
                            ObjectTypes[i] = reader.ReadUShort();
                        else if (i < objectCount * 2)
                            ObjectHandles[i % objectCount] = reader.ReadUShort();
                        else
                            ObjectFlags[i % objectCount] = reader.ReadUShort();
                    }

                    if (header == -1)
                    {
                        Folders = new string[reader.ReadUShort()];
                        for (int i = 0; i < Folders.Length; i++)
                            Folders[i] = reader.ReadAutoYuniversal();
                    }
                }
                else if (identifier == "EvTs")
                {
                    reader.Skip(2);
                    EditorX = reader.ReadUInt();
                    EditorY = reader.ReadUInt();
                    EditorCaretType = reader.ReadUInt();
                    EditorCaretX = reader.ReadUInt();
                    EditorCaretY = reader.ReadUInt();
                }
                else if (identifier == "EvLs")
                {
                    reader.Skip(2);
                    EditorLineY = reader.ReadUInt();
                    EditorLineType = reader.ReadUInt();
                    EventLineY = reader.ReadUInt();
                    EventLineType = reader.ReadUInt();
                }
                else if (identifier == "E2Ts" || identifier == "TYAL")
                    reader.Skip(reader.ReadInt());
                else if (identifier == "!DNE")
                    break;
            }

            if (Parent == null) // Global Events
                reader.Seek(endOffset);
        }

        public override void WriteCCN(ByteWriter writer, params object[] extraInfo)
        {

        }

        public override void WriteMFA(ByteWriter writer, params object[] extraInfo)
        {
            writer.WriteUShort(1030);
            writer.WriteUShort(0);

            if (Events.Count > 0)
            {
                writer.WriteAscii("Evts");
                ByteWriter evtsWriter = new ByteWriter(new MemoryStream());
                foreach (Event evt in Events)
                    evt.WriteMFA(evtsWriter);
                writer.WriteUInt((uint)evtsWriter.Tell());
                writer.WriteWriter(evtsWriter);
            }

            if (EventObjects.Count > 0)
            {
                writer.WriteAscii("EvOb");
                writer.WriteInt(EventObjects.Count);
                foreach (EventObject obj in EventObjects.Values)
                    obj.WriteMFA(writer);
            }

            if (Comments.Length > 0)
            {
                writer.WriteAscii("Rems");
                writer.WriteInt(Comments.Length);
                foreach (Comment comment in Comments)
                    comment.WriteMFA(writer);
            }

            writer.WriteAscii("EvEd");
            {
                writer.WriteShort(-1);
                writer.WriteShort((short)ObjectTypes.Length);

                foreach (ushort type in ObjectTypes)
                    writer.WriteUShort(type);
                foreach (ushort handle in ObjectHandles)
                    writer.WriteUShort(handle);
                foreach (ushort flag in ObjectFlags)
                    writer.WriteUShort(flag);

                writer.WriteUShort((ushort)Folders.Length);
                foreach (string folder in Folders)
                    writer.WriteAutoYunicode(folder);
            }

            writer.WriteAscii("EvTs");
            {
                writer.WriteInt(10);
                writer.WriteBytes(new byte[18]);
            }

            writer.WriteAscii("EvLs");
            {
                writer.WriteInt(10);
                writer.WriteBytes(new byte[14]);
            }

            writer.WriteAscii("E2Ts");
            {
                writer.WriteInt(8);
                writer.WriteBytes(new byte[8]);
            }

            writer.WriteAscii("EvCs");
            {
                writer.WriteInt(EditorData);
                writer.WriteUShort(ConditionWidth);
                writer.WriteShort(ObjectHeight);
                writer.WriteBytes(new byte[12]);
            }

            writer.WriteAscii("!DNE");
        }

        void EnsureMfaQualifiers()
        {
            if (!NebulaCore.MFA || Qualifiers.Count > 0)
                return;

            foreach (EventObject eo in EventObjects.Values)
            {
                if (eo.ObjectType != 3)
                    continue;

                ushort oi = (ushort)eo.Handle;
                short type = (short)eo.ItemType;
                if (Qualifiers.Any(q => q.ObjectInfo == oi && q.Type == type))
                    continue;

                Qualifiers.Add(new Qualifier { ObjectInfo = oi, Type = type });
            }
        }

        public ObjectInfo? ResolveObject(int objectInfo)
        {
            EnsureMfaQualifiers();

            if (Qualifiers.Any(x => x.ObjectInfo == objectInfo))
                return null;

            if (NebulaCore.MFA && EventObjects.Count > 0)
            {
                if (!EventObjects.TryGetValue(objectInfo, out EventObject? eo))
                    return null;
                if (eo.ObjectType != 1)
                    return null;

                int handle = (int)eo.ItemHandle;
                if (Parent?.FrameObjectItems.TryGetValue(handle, out ObjectInfo? localOi) == true)
                    return localOi;
                if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(handle, out ObjectInfo? globalOi))
                    return globalOi;
                return null;
            }

            if (Parent?.FrameObjectItems.TryGetValue(objectInfo, out ObjectInfo? frameOi) == true)
                return frameOi;
            if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(objectInfo, out ObjectInfo? fallbackOi))
                return fallbackOi;
            return null;
        }

        public string ResolveObjectName(int objectInfo, short aceObjectType)
        {
            EnsureMfaQualifiers();

            if (aceObjectType < 0 && aceObjectType != -7)
            {
                return aceObjectType switch
                {
                    -1 => "Special",
                    -2 => "Sound",
                    -3 => "Storyboard",
                    -4 => "Timer",
                    -5 => "Create",
                    -6 => "Mouse",
                    -8 => "Game",
                    _ => "System"
                };
            }

            if (NebulaCore.MFA && EventObjects.Count > 0 && EventObjects.TryGetValue(objectInfo, out EventObject? eo))
            {
                if (eo.ObjectType == 3)
                    return GetQualifierDisplayName(eo.SystemQualifier, (short)eo.ItemType);
                if (eo.ObjectType != 1)
                    return !string.IsNullOrEmpty(eo.Name) ? eo.Name : "Unknown Object";
            }

            ObjectInfo? resolved = ResolveObject(objectInfo);
            if (resolved != null && !string.IsNullOrEmpty(resolved.Name))
                return resolved.Name;

            if (EventObjects.TryGetValue(objectInfo, out EventObject? evtObj) && !string.IsNullOrEmpty(evtObj.Name))
                return evtObj.Name;

            Qualifier[] qualifier = Qualifiers.Where(x => x.ObjectInfo == objectInfo && x.Type == aceObjectType).ToArray();
            if (qualifier.Length > 0)
                return GetQualifierDisplayName((ushort)(qualifier[0].ObjectInfo & 0x7FFF), qualifier[0].Type);

            return "Unknown Object";
        }

        public static string GetQualifierDisplayName(ushort systemQualifier, short type)
        {
            string qualifierName = "Group." + (systemQualifier & 0x7FFF) switch
            {
                0 => "Player",
                1 => "Good",
                2 => "Neutral",
                3 => "Bad",
                4 => "Enemies",
                5 => "Friends",
                6 => "Bullets",
                7 => "Arms",
                8 => "Bonus",
                9 => "Collectables",
                10 => "Traps",
                11 => "Doors",
                12 => "Keys",
                13 => "Texts",
                14 => "0",
                15 => "1",
                16 => "2",
                17 => "3",
                18 => "4",
                19 => "5",
                20 => "6",
                21 => "7",
                22 => "8",
                23 => "9",
                24 => "Parents",
                25 => "Children",
                26 => "Data",
                27 => "Timed",
                28 => "Engine",
                29 => "Areas",
                30 => "Reference Points",
                31 => "Radar Enemies",
                32 => "Radar Friends",
                33 => "Radar Neutrals",
                34 => "Music",
                35 => "Sound",
                36 => "Waveform",
                37 => "Background Scenery",
                38 => "Foreground Scenery",
                39 => "Decorations",
                40 => "Water",
                41 => "Clouds",
                42 => "Empty",
                43 => "Fog",
                44 => "Flowers",
                45 => "Animals",
                46 => "Bosses",
                47 => "NPC",
                48 => "Vehicles",
                49 => "Rockets",
                50 => "Balls",
                51 => "Bombs",
                52 => "Explosions",
                53 => "Particles",
                54 => "Clothes",
                55 => "Glow",
                56 => "Arrows",
                57 => "Buttons",
                58 => "Cursors",
                59 => "Drawing Tools",
                60 => "Indicator",
                61 => "Shapes",
                62 => "Shields",
                63 => "Shifting Blocks",
                64 => "Magnets",
                65 => "Negative Matter",
                66 => "Neutral Matter",
                67 => "Positive Matter",
                68 => "Breakable",
                69 => "Dissolving",
                70 => "Dialogue",
                71 => "HUD",
                72 => "Inventory",
                73 => "Inventory Item",
                74 => "Interface",
                75 => "Movable",
                76 => "Perspective",
                77 => "Calculation Objects",
                78 => "Invisible",
                79 => "Masks",
                80 => "Obstacles",
                81 => "Value Holder",
                82 => "Helpful",
                83 => "Powerups",
                84 => "Targets",
                85 => "Trapdoors",
                86 => "Dangers",
                87 => "Forbidden",
                88 => "Physical objects",
                89 => "3D Objects",
                90 => "Generic 1",
                91 => "Generic 2",
                92 => "Generic 3",
                93 => "Generic 4",
                94 => "Generic 5",
                95 => "Generic 6",
                96 => "Generic 7",
                97 => "Generic 8",
                98 => "Generic 9",
                99 => "Generic 10",
                _ => string.Empty
            };

            qualifierName += "." + type switch
            {
                2 => "Sprite",
                3 => "Text",
                4 => "Question",
                5 => "Score",
                6 => "Lives",
                7 => "Counter",
                _ => string.Empty
            };

            if (type >= 32 && NebulaCore.PackageData.Extensions.Exts.ContainsKey(type - 32))
            {
                Extension ext = NebulaCore.PackageData.Extensions.Exts[type - 32];
                qualifierName += ext.Name;
            }

            if (qualifierName.StartsWith("Group.."))
                return "Unknown Qualifier";
            return qualifierName;
        }

        static bool IsMfaEventTag(string tag) =>
            tag is "Evts" or "STVE" or "Rems" or "SMER" or "SPRG"
                or "EvOb" or "SJBO" or "EvCs" or "EvEd" or "EvTs" or "EvLs"
                or "E2Ts" or "TYAL" or "!DNE";
    }
}
