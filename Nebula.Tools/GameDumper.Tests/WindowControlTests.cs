using System.Text.Json;
using Nebula;
using Nebula.Core.Data.Chunks.AppChunks;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.FrameChunks.Events;
using Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.PackageReaders;
using Nebula.Core.FileReaders;
using Nebula.Tools.GameDumper;
using FusionAction = Nebula.Core.Data.Chunks.FrameChunks.Events.Action;

static class WindowControlTests
{
    public static void ExportAce()
    {
        foreach (string fileName in new[] { "kcwctrl", "kcwctrl.mfx", "KCWCTRL.MFX" })
            CheckExport(fileName);
    }

    static void CheckExport(string fileName)
    {
        var mfa = new MFAPackageData();
        mfa.Extensions.Exts[7] = new Extension { FileName = fileName, Name = "Window Control" };
        mfa.Extensions.Exts[8] = new Extension { FileName = "test.mfx", Name = "Test Extension" };
        var frame = new Frame { FrameName = "Test Frame", Handle = 1 };
        frame.FrameEvents.Parent = frame;
        var obj = new ObjectInfo { Name = "Test Window" };
        obj.Header.Handle = 10;
        obj.Header.Type = 39;
        frame.FrameObjectItems[10] = obj;
        mfa.FrameItems.Items[10] = obj;
        frame.FrameEvents.EventObjects[0] = new EventObject
        {
            Handle = 0, ObjectType = 1, ItemType = 39, ItemHandle = 10,
        };
        mfa.Frames.Add(frame);
        var evt = new Event { Parent = frame.FrameEvents };
        frame.FrameEvents.Events.Add(evt);

        Parameter String(string value) => new() { Code = 40, Data = new ParameterString { Value = value } };
        Parameter Number(int value) => new() { Code = 5, Data = new ParameterInt { Value = value } };
        FusionAction Action(short num, params Parameter[] parameters) => new()
        {
            Parent = evt, ObjectType = 39, ObjectInfo = 0, Num = num, Parameters = parameters,
        };
        evt.Actions.Add(Action(98, String("Test Title")));
        evt.Actions.Add(Action(97, Number(-20), Number(30)));
        evt.Actions.Add(Action(108, Number(640), Number(1)));
        evt.Actions.Add(Action(113, Number(720)));
        evt.Actions.Add(Action(114, Number(42)));
        var otherExtension = Action(98, String("Test Title"));
        otherExtension.ObjectType = 40;
        evt.Actions.Add(otherExtension);

        var expressionParam = new Parameter { Code = 45, FrameEvents = frame.FrameEvents };
        var expressions = new ParameterExpressions { Parent = expressionParam };
        expressionParam.Data = expressions;
        expressions.Expressions.Add(new ParameterExpression
        {
            Parent = expressionParam, ObjectType = 39, ObjectInfo = 0, Num = 89,
        });
        evt.Actions.Add(Action(98, expressionParam));
        foreach (short num in new short[] { -81, -82, -83, -84, -85, -86, -87 })
            evt.Conditions.Add(new Condition { Parent = evt, ObjectType = 39, ObjectInfo = 0, Num = num });
        evt.Conditions[2].OtherFlags["Negated"] = true;

        var oldReader = NebulaCore.CurrentReader;
        bool oldMfa = NebulaCore.MFA;
        string root = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
        try
        {
            NebulaCore.CurrentReader = new MFAFileReader { Package = mfa };
            NebulaCore.MFA = true;
            MFSPLExporter.Export(mfa, root);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "events.json")));
            var exported = doc.RootElement.GetProperty("frames")[0].GetProperty("events")[0];
            var actions = exported.GetProperty("actions");
            Equal("Test Window : Set title to \"Test Title\"", actions[0].GetProperty("text").GetString(), "title action");
            Equal(98, actions[0].GetProperty("num").GetInt32(), "original action ID");
            Equal("Test Window : Move to (-20, 30)", actions[1].GetProperty("text").GetString(), "move parameter order");
            Equal("Test Window : Set width of Frame window to 640, adjust frame size = 1", actions[2].GetProperty("text").GetString(), "frame resize parameters");
            Equal("Test Window : Set maximum height to 720", actions[3].GetProperty("text").GetString(), "last supported action");
            Equal(true, actions[4].GetProperty("text").GetString()!.Contains("Event ID 114, 1 Parameters"), "unknown action fallback");
            Equal(true, actions[5].GetProperty("text").GetString()!.Contains("Event ID 98, 1 Parameters"), "another extension must retain fallback");
            Equal("Test Window : Set title to WindowTitle$(\"Test Window\")", actions[6].GetProperty("text").GetString(), "nested title expression");
            var parameter = actions[6].GetProperty("params")[0];
            Equal("WindowTitle$(\"Test Window\")", parameter.GetProperty("tokens")[0].GetString(), "IR expression token");
            string[] conditionTexts =
            {
                "Window is minimized", "Window is maximized", "Window is visible",
                "Application is active", "Window has focus", "Window is attached to desktop",
            };
            var conditions = exported.GetProperty("conditions");
            for (int i = 0; i < conditionTexts.Length; i++)
                Equal((i == 2 ? "NOT " : "") + "Test Window: " + conditionTexts[i], conditions[i].GetProperty("text").GetString(), "condition ID offset");
            Equal(true, conditions[2].GetProperty("negated").GetBoolean(), "condition negation");
            Equal(true, conditions[6].GetProperty("text").GetString()!.Contains("Event ID -87, 0 Parameters"), "unknown condition fallback");
            Equal(true, ACEventBase.TryFormatExtensionExpression(39, 80, "Test Window", out string position), "first expression ID");
            Equal("WinXPos(\"Test Window\")", position, "window position expression");
            Equal(true, ACEventBase.TryFormatExtensionExpression(39, 94, "Test Window", out string height), "last expression ID");
            Equal("FrameWinYSize(\"Test Window\")", height, "frame height expression");
            Equal(false, ACEventBase.TryFormatExtensionExpression(39, 95, "Test Window", out _), "unknown expression fallback");
            Equal(false, ACEventBase.TryFormatExtensionExpression(40, 89, "Test Window", out _), "expression extension isolation");
        }
        finally
        {
            NebulaCore.CurrentReader = oldReader;
            NebulaCore.MFA = oldMfa;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
    }
}
