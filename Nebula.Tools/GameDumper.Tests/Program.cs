using System.Text.Json;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.MFAChunks;
using Nebula.Core.Data.Chunks.MFAChunks.MFAObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.PackageReaders;
using Nebula.Tools.GameDumper;

var failures = new List<string>();
Run("frame-local instance metadata wins over a colliding global handle", FrameLocalMetadataWins);
Run("global fallback metadata remains complete when a frame-local type is missing", GlobalFallbackMetadataIsComplete);

return failures.Count == 0 ? 0 : 1;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures.Add(name);
        Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
    }
}

void FrameLocalMetadataWins()
{
    var mfa = new MFAPackageData();
    var subtract = MakeFrame(1, "First", 50, "Black effect", 11, transparent: false);
    var xor = MakeFrame(2, "Encounter", 50, "XOR Effect", 3, transparent: true, convertedInkEffect: 11);
    mfa.Frames.Add(subtract);
    mfa.Frames.Add(xor);
    mfa.FrameItems.Items[50] = subtract.FrameObjectItems[50];

    string output = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    try
    {
        MFSPLExporter.Export(mfa, output);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "objects.json")));
        var frames = document.RootElement.GetProperty("frames");

        var firstType = frames[0].GetProperty("objectTypes").GetProperty("50");
        Equal(false, firstType.GetProperty("transparent").GetBoolean(), "MFA Transparent=false must survive the IR export");
        Equal(true, firstType.GetProperty("antiAliasing").GetBoolean(), "MFA AntiAliasing=true must survive the IR export");

        var xorType = frames[1].GetProperty("objectTypes").GetProperty("50");
        Equal("XOR", xorType.GetProperty("inkEffectName").GetString(), "frame-local type must use the raw MFA ink effect");

        var xorInstance = frames[1].GetProperty("instances")[0];
        Equal("XOR Effect", xorInstance.GetProperty("objectName").GetString(), "instance must carry its frame-local object name");
        Equal(3, xorInstance.GetProperty("inkEffect").GetInt32(), "instance must carry its frame-local ink effect");
        Equal("XOR", xorInstance.GetProperty("inkEffectName").GetString(), "instance must not inherit Subtract from the global handle");
        Equal(true, xorInstance.GetProperty("transparent").GetBoolean(), "instance must carry the source MFA transparency");
        Equal(true, xorInstance.GetProperty("antiAliasing").GetBoolean(), "instance must carry the source MFA anti-aliasing flag");
    }
    finally
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}

void GlobalFallbackMetadataIsComplete()
{
    var mfa = new MFAPackageData();
    var sourceFrame = MakeFrame(1, "Source", 60, "Shared object", 3, transparent: false);
    var referenceFrame = new Frame { Handle = 2, FrameName = "Reference" };
    referenceFrame.FrameInstances.Instances = new[] { new FrameInstance { ObjectInfo = 60 } };
    mfa.Frames.Add(sourceFrame);
    mfa.Frames.Add(referenceFrame);
    mfa.FrameItems.Items[60] = sourceFrame.FrameObjectItems[60];

    string output = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    try
    {
        MFSPLExporter.Export(mfa, output);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "objects.json")));
        var frame = document.RootElement.GetProperty("frames")[1];
        Equal("XOR", frame.GetProperty("objectTypes").GetProperty("60").GetProperty("inkEffectName").GetString(), "fallback type must be present in the frame-local table");
        Equal("Shared object", frame.GetProperty("instances")[0].GetProperty("objectName").GetString(), "fallback instance metadata must be complete");
    }
    finally
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}

static Frame MakeFrame(int frameHandle, string frameName, int objectHandle, string objectName, int inkEffect, bool transparent, int? convertedInkEffect = null)
{
    var objectInfo = new ObjectInfo
    {
        Name = objectName,
        Properties = new ObjectInfoProperties(),
    };
    objectInfo.Header.Handle = objectHandle;
    objectInfo.Header.Type = 2;
    objectInfo.Header.InkEffect = convertedInkEffect ?? inkEffect;

    var frame = new Frame
    {
        Handle = frameHandle,
        FrameName = frameName,
    };
    frame.FrameObjectItems[objectHandle] = objectInfo;
    frame.MFAFrameInfo.Objects = new[]
    {
        new MFAObjectInfo
        {
            Handle = objectHandle,
            Name = objectName,
            ObjectType = 2,
            InkEffect = inkEffect,
            Transparent = transparent,
            AntiAliasing = true,
        },
    };
    frame.FrameInstances.Instances = new[]
    {
        new FrameInstance
        {
            ObjectInfo = (uint)objectHandle,
            PositionX = 10,
            PositionY = 20,
        },
    };
    return frame;
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}; expected {expected}, got {actual}");
}
