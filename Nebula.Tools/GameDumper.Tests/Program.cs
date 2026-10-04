using System.Text.Json;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.MFAChunks;
using Nebula.Core.Data.Chunks.MFAChunks.MFAObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon.ObjectMovementDefinitions;
using Nebula.Core.Data.PackageReaders;
using Nebula.Tools.GameDumper;

var failures = new List<string>();
Run("frame-local instance metadata wins over a colliding global handle", FrameLocalMetadataWins);
Run("global fallback metadata remains complete when a frame-local type is missing", GlobalFallbackMetadataIsComplete);
Run("common object flags preserve both states, frame-local handles and global fallback", CommonObjectFlagsAreExported);
Run("InAndOut exports degree angles instead of destination coordinates", InAndOutDirectionsAreExported);
Run("backdrops export collisionWithBox instead of collisionType and exclude common properties", BackdropMetadataIsExported);
Run("MFA media exports remain self-contained across independent sessions", MfaResourcesAreIndependent);
Run("tiles use frame-local MFA ink parameters with global fallback", TileExportTests.FrameLocalInk);
Run("shared tile textures preserve opacity and collision through alternatives", TileExportTests.Alternatives);

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
        Equal(false, firstType.TryGetProperty("antiAliasing", out _), "antiAliasing must not be exported in object types");

        var xorType = frames[1].GetProperty("objectTypes").GetProperty("50");
        Equal("XOR", xorType.GetProperty("inkEffectName").GetString(), "frame-local type must use the raw MFA ink effect");

        var xorInstance = frames[1].GetProperty("instances")[0];
        Equal("XOR Effect", xorInstance.GetProperty("objectName").GetString(), "instance must carry its frame-local object name");
        Equal(3, xorInstance.GetProperty("inkEffect").GetInt32(), "instance must carry its frame-local ink effect");
        Equal("XOR", xorInstance.GetProperty("inkEffectName").GetString(), "instance must not inherit Subtract from the global handle");
        Equal(true, xorInstance.GetProperty("transparent").GetBoolean(), "instance must carry the source MFA transparency");
        Equal(false, xorInstance.TryGetProperty("antiAliasing", out _), "antiAliasing must not be exported in instances");
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

void CommonObjectFlagsAreExported()
{
    var mfa = new MFAPackageData();
    var following = MakeFrame(1, "Following", 70, "Following object", 0, transparent: true);
    var fixedObject = MakeFrame(2, "Fixed", 70, "Fixed object", 0, transparent: true);
    var followingProperties = new ObjectCommon();
    followingProperties.NewObjectFlags["DontUseFineDetection"] = true;
    followingProperties.NewObjectFlags["VisibleAtStart"] = true;
    following.FrameObjectItems[70].Properties = followingProperties;
    var fixedProperties = new ObjectCommon();
    fixedProperties.ObjectFlags = new MFAObjectLoader().ObjectFlags;
    fixedProperties.ObjectFlags["DontFollowFrame"] = true;
    fixedProperties.ObjectFlags["CreateAtStart"] = true; // MFA name for the don't-create bit
    fixedProperties.NewObjectFlags["VisibleAtStart"] = true;
    fixedObject.FrameObjectItems[70].Properties = fixedProperties;
    var fallback = new Frame { Handle = 3, FrameName = "Fallback" };
    fallback.FrameInstances.Instances = new[] { new FrameInstance { ObjectInfo = 70 } };
    mfa.Frames.Add(following);
    mfa.Frames.Add(fixedObject);
    mfa.Frames.Add(fallback);
    mfa.FrameItems.Items[70] = following.FrameObjectItems[70];

    string output = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    try
    {
        MFSPLExporter.Export(mfa, output);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "objects.json")));
        Equal(true, document.RootElement.GetProperty("objectTypes").GetProperty("70").GetProperty("followFrame").GetBoolean(), "legacy table must export followFrame");
        Equal(true, document.RootElement.GetProperty("objectTypes").GetProperty("70").GetProperty("createAtStart").GetBoolean(), "MFA's differently named bit must not invert createAtStart");
        Equal(false, document.RootElement.GetProperty("objectTypes").GetProperty("70").GetProperty("fineDetection").GetBoolean(), "legacy table must export fineDetection");
        Equal(false, document.RootElement.GetProperty("objectTypes").GetProperty("70").TryGetProperty("collisionWithBox", out _), "common object types must not export collisionWithBox");
        var frames = document.RootElement.GetProperty("frames");
        for (int i = 0; i < frames.GetArrayLength(); i++)
        {
            bool expected = i != 1;
            Equal(expected, frames[i].GetProperty("objectTypes").GetProperty("70").GetProperty("followFrame").GetBoolean(), "type must use its own common flags, with global fallback when missing");
            Equal(expected, frames[i].GetProperty("instances")[0].GetProperty("followFrame").GetBoolean(), "instance must use its own common flags, with global fallback when missing");
            var type = frames[i].GetProperty("objectTypes").GetProperty("70");
            var instance = frames[i].GetProperty("instances")[0];
            Equal(expected, type.GetProperty("createAtStart").GetBoolean(), "type must export frame-local creation flags even when MFA names the bit CreateAtStart");
            Equal(expected, instance.GetProperty("createAtStart").GetBoolean(), "instance must export frame-local creation flags, with global fallback when missing");
            Equal(!expected, type.GetProperty("fineDetection").GetBoolean(), "type must use its own fine detection flag independently of followFrame and other newObjectFlags");
            Equal(false, type.TryGetProperty("collisionWithBox", out _), "common object types must export fineDetection only");
            Equal(!expected, instance.GetProperty("fineDetection").GetBoolean(), "instance must use frame-local fine detection, with global fallback when missing");
            Equal(false, instance.TryGetProperty("collisionWithBox", out _), "common object instances must export fineDetection only");
        }
    }
    finally
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}

void BackdropMetadataIsExported()
{
    var mfa = new MFAPackageData();
    var quick = MakeFrame(1, "Quick backdrop", 90, "Quick backdrop", 0, transparent: true);
    quick.FrameObjectItems[90].Header.Type = 0;
    quick.FrameObjectItems[90].Properties = new ObjectQuickBackdrop { CollisionType = 1 };
    var backdrop = MakeFrame(2, "Backdrop", 91, "Backdrop", 0, transparent: true);
    backdrop.FrameObjectItems[91].Header.Type = 1;
    backdrop.FrameObjectItems[91].Properties = new ObjectBackdrop { CollisionType = 0 };
    mfa.Frames.Add(quick);
    mfa.Frames.Add(backdrop);
    mfa.FrameItems.Items[90] = quick.FrameObjectItems[90];
    mfa.FrameItems.Items[91] = backdrop.FrameObjectItems[91];

    string output = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    try
    {
        MFSPLExporter.Export(mfa, output);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "objects.json")));
        var frames = document.RootElement.GetProperty("frames");
        for (int i = 0; i < 2; i++)
        {
            string key = (90 + i).ToString();
            var type = frames[i].GetProperty("objectTypes").GetProperty(key);
            foreach (var entry in new[] { type, document.RootElement.GetProperty("objectTypes").GetProperty(key), frames[i].GetProperty("instances")[0] })
            {
                Equal(i == 0, entry.GetProperty("collisionWithBox").GetBoolean(), "backdrops must export collisionWithBox from their own CollisionType");
                Equal(false, entry.TryGetProperty("collisionType", out _), "collisionWithBox must replace the numeric collisionType");
                Equal(false, entry.TryGetProperty("fineDetection", out _), "backdrops must not export common object fineDetection");
                Equal(false, entry.TryGetProperty("createAtStart", out _), "backdrops must not export createAtStart");
                Equal(false, entry.TryGetProperty("antiAliasing", out _), "backdrops must not export antiAliasing");
            }
        }
    }
    finally
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}

void InAndOutDirectionsAreExported()
{
    var mfa = new MFAPackageData();
    var frame = MakeFrame(1, "InAndOut", 80, "Moving object", 0, transparent: true);
    var cases = new[]
    {
        (0, "Right"), (45, "Top-Right"), (90, "Top"), (135, "Top-Left"),
        (180, "Left"), (225, "Bottom-Left"), (270, "Bottom"), (315, "Bottom-Right"), (37, "Custom"),
    };
    var movements = new List<ObjectMovement>();
    foreach (bool prefix in new[] { true, false })
    {
        foreach (var (direction, _) in cases)
        {
            // Clickteam's payload: version byte, then type, degrees, duration,
            // flags, destination X and destination Y (little-endian int32).
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            if (prefix)
                writer.Write((byte)0);
            writer.Write(prefix ? 1 : 0); // Smooth / Linear
            writer.Write(direction);
            writer.Write(1500); // Duration in milliseconds
            writer.Write(prefix ? 3 : 1); // Move at start is bit 1, out at start is bit 0
            writer.Write(772); // Must never be read as the direction
            writer.Write(-200);
            movements.Add(new ObjectMovement
            {
                MovementDefinition = new ObjectMovementExtension { FileName = "InAndOut.mvx", Data = stream.ToArray() },
            });
        }
    }
    // Actual INTRODUCING payload from SCENKA - EPILOGUE #1:
    // type=1, direction=270, duration=500, flags=0, destination=(772, 32).
    movements.Add(new ObjectMovement
    {
        MovementDefinition = new ObjectMovementExtension
        {
            FileName = "InAndOut.mvx",
            Data = Convert.FromHexString("00010000000E010000F4010000000000000403000020000000"),
        },
    });
    frame.FrameObjectItems[80].Properties = new ObjectCommon
    {
        ObjectMovements = new ObjectMovements { Movements = movements.ToArray() },
    };
    mfa.Frames.Add(frame);
    mfa.FrameItems.Items[80] = frame.FrameObjectItems[80];

    string output = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    try
    {
        MFSPLExporter.Export(mfa, output);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "objects.json")));
        var exported = document.RootElement.GetProperty("frames")[0].GetProperty("objectTypes").GetProperty("80").GetProperty("movements");
        for (int i = 0; i < cases.Length * 2; i++)
        {
            var (direction, name) = cases[i % cases.Length];
            Equal(direction, exported[i].GetProperty("direction").GetInt32(), "InAndOut direction must preserve the MFA angle in degrees");
            Equal(name, exported[i].GetProperty("directionName").GetString(), "direction name must use degrees rather than an ordinal");
            Equal(i < cases.Length ? 1 : 0, exported[i].GetProperty("inAndOutType").GetInt32(), "movement type must come from the first payload integer");
            Equal(i < cases.Length ? "Smooth" : "Linear", exported[i].GetProperty("inAndOutTypeName").GetString(), "movement type describes interpolation");
            Equal(1500, exported[i].GetProperty("speed").GetInt32(), "existing duration value must be preserved");
            Equal(i < cases.Length ? 3 : 1, exported[i].GetProperty("flags").GetInt32(), "flags must come from the fourth payload integer");
            Equal(i < cases.Length, exported[i].GetProperty("movingAtStart").GetBoolean(), "movingAtStart must use bit 1 independently of out-at-start");
        }
        var introducing = exported[cases.Length * 2];
        Equal(270, introducing.GetProperty("direction").GetInt32(), "INTRODUCING must export 270 degrees, not destination X=772");
        Equal("Bottom", introducing.GetProperty("directionName").GetString(), "INTRODUCING direction must be Bottom");
        Equal(500, introducing.GetProperty("speed").GetInt32(), "INTRODUCING duration must remain 500 milliseconds");
        Equal(0, introducing.GetProperty("flags").GetInt32(), "INTRODUCING has no start flags");
        Equal(false, introducing.GetProperty("movingAtStart").GetBoolean(), "INTRODUCING must not move at start");
    }
    finally
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }
}

void MfaResourcesAreIndependent()
{
    string root = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
    bool gpu = Nebula.Core.Utilities.Parameters.Inst.gpu_acceleration;
    Nebula.Core.Utilities.Parameters.Inst.gpu_acceleration = false;
    MFAPackageData Source(string animationName, byte color)
    {
        var mfa = new MFAPackageData();
        var image = new Nebula.Core.Data.Chunks.BankChunks.Images.ImageMFA
        {
            Handle = 1, Width = 2, Height = 1, GraphicMode = 8,
            ImageData = new byte[] { color, 100, 200, 255, color, 100, 200, 255 },
        };
        mfa.ImageBank.Images[1] = image;
        mfa.SoundBank.Sounds[1] = new Nebula.Core.Data.Chunks.BankChunks.Sounds.Sound
        {
            Name = "Sound", Data = new byte[] { 82, 73, 70, 70, 1, 2, 3, 4 },
        };
        var common = new ObjectCommon();
        var animation = new ObjectAnimation { Name = animationName };
        animation.Directions.Add(new ObjectDirection { Frames = new uint[] { 1 } });
        common.ObjectAnimations.Animations[0] = animation;
        var obj = new ObjectInfo { Name = "Mario", Properties = common };
        obj.Header.Type = 2;
        mfa.FrameItems.Items[1] = obj;
        return mfa;
    }
    void VerifyManifest(string dir, string manifest, string entries)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, manifest)));
        string file = doc.RootElement.GetProperty(entries).GetProperty("1").GetProperty("file").GetString()!;
        Equal("assets", Path.GetDirectoryName(file), "manifest must reference this MFA's assets folder");
        Equal(true, File.Exists(Path.Combine(dir, file)), "manifest must resolve to an existing local file");
    }
    try
    {
        string first = Path.Combine(root, "First");
        string second = Path.Combine(root, "Second");
        foreach (var (dir, mfa) in new[] { (first, Source("First", 10)), (second, Source("Second", 10)) })
        {
            MFSPLExporter.Export(mfa, dir);
            var result = MFSPLExporter.ExportResources(mfa, dir);
            Equal(1, result.Assets.Images, "each MFA must write even an identical image");
            Equal(1, result.Assets.Sounds, "each MFA must write even an identical sound");
            Equal(1, result.Sprites.Sheets, "same-named objects must get a sheet in each MFA");
            VerifyManifest(dir, "images.json", "images");
            VerifyManifest(dir, "sounds.json", "sounds");
            using var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "sprites", "index.json")));
            Equal("Mario", index.RootElement.GetProperty("Mario")[0].GetString(), "each MFA must have its own sprite index");
        }
        string firstSheet = Path.Combine(first, "sprites", "Mario", "sheet.json");
        string original = File.ReadAllText(firstSheet);
        var untouched = Directory.GetFiles(second, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        MFSPLExporter.ExportResources(Source("Updated", 20), first);
        Equal(false, original == File.ReadAllText(firstSheet), "re-export must update the chosen MFA");
        foreach (var kv in untouched)
            Equal(true, kv.Value.AsSpan().SequenceEqual(File.ReadAllBytes(kv.Key)), "another MFA's files must remain unchanged");
        Equal(false, Directory.Exists(Path.Combine(root, "assets")), "no shared assets folder at IR root");
        Equal(false, Directory.Exists(Path.Combine(root, "sprites")), "no shared sprites folder at IR root");

        var archived = AssetArchiver.Run(root);
        Equal(0, archived.SkippedImages, "archiver must resolve local asset references");
        Equal(0, archived.SharedSprites, "different sheets with the same object name must stay separate");
        Equal(2, archived.LevelSprites, "both local sprite variants must survive archiving");
        Equal(true, File.Exists(firstSheet), "archiving must preserve original sheets");

        MFSPLExporter.ExportResources(Source("Updated", 30), second);
        Equal(0, AssetArchiver.Run(root).SharedSprites, "matching sheet metadata with different pixels must not be shared");
        MFSPLExporter.ExportResources(Source("Updated", 20), second);
        var shared = AssetArchiver.Run(root);
        Equal(1, shared.SharedImages, "identical local images must still be recognized as shared");
        Equal(1, shared.SharedSprites, "identical local sheets must still be recognized as shared");
        Equal(1, Directory.GetDirectories(Path.Combine(root, "shared_sprites")).Length, "only one shared sheet copy");
        Equal(1, AssetArchiver.Run(root).SharedSprites, "archiving local sheets must remain idempotent");
    }
    finally
    {
        Nebula.Core.Utilities.Parameters.Inst.gpu_acceleration = gpu;
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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
