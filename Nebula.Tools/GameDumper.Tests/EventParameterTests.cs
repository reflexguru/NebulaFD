using System.Text.Json;
using Nebula;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.FrameChunks.Events;
using Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters;
using Nebula.Core.Data.PackageReaders;
using Nebula.Core.FileReaders;
using Nebula.Core.Memory;
using Nebula.Tools.GameDumper;
using FusionAction = Nebula.Core.Data.Chunks.FrameChunks.Events.Action;

static class EventParameterTests
{
    // Binary actions with 16-bit and 32-bit parameter payloads.
    const string ShortGlobal = "2A00FFFF03000000000000000200060031000500160016000000FFFF00000A0004000000000000000000";
    const string WideGlobal = "2C00FFFF030000000000000002000800310005000000160016000000FFFF00000A0004000000000000000000";
    const string ShortGroup = "1800FFFF070000000000000001000A002700000000000600";
    const string WideGroup = "1A00FFFF070000000000000001000C0027000000000006000000";
    const string NextAction = "0E00020018000D00000000000000";

    public static void ShortGlobalIndex() => CheckGlobal(ShortGlobal, 1);
    public static void WideGlobalIndex()
    {
        CheckGlobal(WideGlobal, 2);
        // A full-width value must retain its high word; masking all indices is incorrect.
        using var reader = new ByteReader(Convert.FromHexString("0800310005000100"));
        var parameter = new Parameter();
        parameter.ReadCCN(reader);
        Equal(65541, ((ParameterInt)parameter.Data).Value, "32-bit index high word");
    }

    static void CheckGlobal(string hex, short identifier)
    {
        var frame = new Frame { FrameName = "Test Frame", Handle = 1 };
        frame.FrameEvents.Parent = frame;
        var evt = new Event { Identifier = identifier, Parent = frame.FrameEvents };
        using var reader = new ByteReader(Convert.FromHexString(hex + NextAction));
        var action = ReadAction(reader, evt);
        Equal(5, ((ParameterInt)action.Parameters[0].Data).Value, "global value index");
        Equal(22, action.Parameters[1].Code, "following expression parameter code");
        var expression = (ParameterExpressions)action.Parameters[1].Data;
        Equal((short)0, expression.Comparison, "expression comparison");
        Equal(1, expression.Expressions.Count, "expression token count");
        Equal(4, ((ExpressionInt)expression.Expressions[0].Expression).Value, "expression value");
        Equal((long)(hex.Length / 2), reader.Tell(), "action boundary");
        CheckNextAction(reader, evt);
        CheckExport(frame, evt, action =>
        {
            Equal("Special : Set Test Value to 4", action.GetProperty("text").GetString(), "IR action name");
            var parameters = action.GetProperty("params");
            Equal("5", parameters[0].GetProperty("value").GetString(), "IR structured global index");
            Equal("4", parameters[1].GetProperty("tokens")[0].GetString(), "IR expression token");
        });
    }

    public static void ShortGroupId() => CheckGroup(ShortGroup, 1);
    public static void WideGroupId()
    {
        CheckGroup(WideGroup, 2);
        using var reader = new ByteReader(Convert.FromHexString("0C0027000000000006000100"));
        var parameter = new Parameter();
        parameter.ReadCCN(reader);
        Equal(65542, ((ParameterGroupPointer)parameter.Data).ID, "32-bit group ID high word");
    }

    static void CheckGroup(string hex, short identifier)
    {
        var frame = new Frame { FrameName = "Test Frame", Handle = 1 };
        frame.FrameEvents.Parent = frame;
        frame.GroupLookupTable[6] = new ParameterGroup { ID = 6, Name = "Test Group" };
        var evt = new Event { Identifier = identifier, Parent = frame.FrameEvents };
        using var reader = new ByteReader(Convert.FromHexString(hex + NextAction));
        var action = ReadAction(reader, evt);
        var pointer = (ParameterGroupPointer)action.Parameters[0].Data;
        Equal(6, pointer.ID, "group ID");
        Equal(0, pointer.Pointer, "MFA unused pointer");
        Equal(0L, pointer.CCNPointer, "zero pointer target");
        Equal((long)(hex.Length / 2), reader.Tell(), "group action boundary");
        CheckNextAction(reader, evt);
        CheckExport(frame, evt, action =>
        {
            Equal("Special : Deactivate Group \"Test Group\"", action.GetProperty("text").GetString(), "IR group name");
            Equal(6, action.GetProperty("params")[0].GetProperty("id").GetInt32(), "IR structured group ID");
        });

        // Both layouts use the start of the parameter header as pointer origin.
        int oldBuild = NebulaCore.Build;
        try
        {
            foreach (int build in new[] { 283, 284 })
            {
                NebulaCore.Build = build;
                byte[] bytes = Convert.FromHexString(hex);
                BitConverter.GetBytes(100).CopyTo(bytes, 18);
                using var pointerReader = new ByteReader(bytes);
                var parsed = ReadAction(pointerReader, new Event { Parent = frame.FrameEvents });
                var nonzero = (ParameterGroupPointer)parsed.Parameters[0].Data;
                Equal(build < 284 ? 112L : 114L, nonzero.CCNPointer, "nonzero pointer target");
            }
        }
        finally { NebulaCore.Build = oldBuild; }
    }

    static FusionAction ReadAction(ByteReader reader, Event evt)
    {
        var action = new FusionAction { Parent = evt };
        action.ReadMFA(reader);
        evt.Actions.Add(action);
        return action;
    }

    static void CheckNextAction(ByteReader reader, Event evt)
    {
        var next = ReadAction(reader, evt);
        Equal((short)2, next.ObjectType, "next action object type");
        Equal((short)24, next.Num, "next action number");
        Equal((ushort)13, next.ObjectInfo, "next action object handle");
        Equal(reader.Size(), reader.Tell(), "next action end position");
        evt.Actions.Remove(next); // Export only the action under test.
    }

    static void CheckExport(Frame frame, Event evt, Action<JsonElement> check)
    {
        var mfa = new MFAPackageData();
        mfa.Frames.Add(frame);
        mfa.GlobalValueNames.Names = new[] { "", "", "", "", "", "Test Value" };
        frame.FrameEvents.Events.Add(evt);
        var oldReader = NebulaCore.CurrentReader;
        string root = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            NebulaCore.CurrentReader = new MFAFileReader { Package = mfa };
            MFSPLExporter.Export(mfa, root);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "events.json")));
            check(doc.RootElement.GetProperty("frames")[0].GetProperty("events")[0].GetProperty("actions")[0]);
        }
        finally
        {
            NebulaCore.CurrentReader = oldReader;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{label}: expected {expected}, got {actual}");
    }
}
