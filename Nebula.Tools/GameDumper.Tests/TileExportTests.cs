using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.MFAChunks;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon;
using Nebula.Core.Data.PackageReaders;
using Nebula.Tools.GameDumper;

static class TileExportTests
{
    public static void FrameLocalInk()
    {
        var mfa = new MFAPackageData();
        foreach (int index in new[] { 1, 2 })
        {
            var frame = new Frame { Handle = index, FrameName = "Frame " + index };
            var obj = new ObjectInfo { Name = "Backdrop", Properties = new ObjectBackdrop { Image = 1, Width = 32, Height = 32 } };
            obj.Header.Handle = 50;
            obj.Header.Type = 1;
            frame.FrameObjectItems[50] = obj;
            frame.MFAFrameInfo.Objects = new[] { new MFAObjectInfo
            {
                Handle = 50, ObjectType = 1, InkEffect = 1, InkEffectParameter = (uint)(index * 32),
                Transparent = index == 2,
                ObjectEffects = new MFAObjectEffects { BlendCoeff = (byte)(index * 20), RGBCoeff = System.Drawing.Color.FromArgb(index * 60, 100, 200) },
            } };
            frame.FrameInstances.Instances = new[] { new FrameInstance { ObjectInfo = 50 } };
            mfa.Frames.Add(frame);
        }
        mfa.FrameItems.Items[50] = mfa.Frames[0].FrameObjectItems[50];
        var fallback = new Frame { Handle = 3, FrameName = "Fallback" };
        fallback.FrameInstances.Instances = new[] { new FrameInstance { ObjectInfo = 50 } };
        mfa.Frames.Add(fallback);
        InTempDirectory(root =>
        {
            MFSPLExporter.ExportTiles(mfa, root);
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tiles.json")));
            var frames = doc.RootElement.GetProperty("frames");
            for (int i = 0; i < 3; i++)
            {
                int source = i == 1 ? 2 : 1;
                var tile = frames[i].GetProperty("tiles")[0];
                Equal(1, tile.GetProperty("inkEffect").GetInt32(), "ink effect must come from source objectlist");
                Equal(source * 32, tile.GetProperty("inkEffectParam").GetInt32(), "frame-local coefficient must win over a colliding global handle");
                Equal(source * 20, tile.GetProperty("blendCoeff").GetInt32(), "source blend coefficient must win over converted header");
                Equal(source * 60, tile.GetProperty("rgbCoeff").GetProperty("r").GetInt32(), "source RGB coefficient must win over converted header");
                Equal(source == 2, tile.GetProperty("transparent").GetBoolean(), "MFA transparency flag must survive export and fallback");
            }
        });
    }

    public static void Alternatives()
    {
        InTempDirectory(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            File.WriteAllBytes(Path.Combine(root, "assets", "shared.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(root, "assets", "static.png"), new byte[] { 2 });
            var tiles = new List<Dictionary<string, object?>>();
            Dictionary<string, object?> Tile(int x, int y = 0, uint image = 1, int ink = 0, int param = 0, int blend = 0, int obstacle = 0)
            {
                var tile = new Dictionary<string, object?>
                {
                    ["x"] = x, ["y"] = y, ["w"] = 32, ["h"] = 32, ["image"] = image,
                    ["inkEffect"] = ink, ["inkEffectParam"] = param, ["blendCoeff"] = blend,
                    ["obstacle"] = obstacle, ["order"] = tiles.Count,
                };
                tiles.Add(tile);
                return tile;
            }
            Tile(0, obstacle: 1);                       // base: opaque + collision
            Tile(32, image: 2, ink: 1, param: 64);      // same PNG, different handle and parameters
            Tile(64, ink: 1, param: 64);               // must reuse the preceding alternative
            Tile(96, ink: 1, param: 128);              // fully transparent
            Tile(128, blend: 128);                    // HWA coefficient is on the 0..255 scale
            Tile(160)["rgbCoeff"] = new { r = 128, g = 255, b = 255 };
            Tile(192)["transparent"] = false;        // distinct source parameters even with equal modulation
            Tile(224, ink: 11);                       // distinct ink effect must not merge with the base
            var legacy = Tile(256);                   // older IR lacks all ink fields
            foreach (string key in new[] { "inkEffect", "inkEffectParam", "blendCoeff" }) legacy.Remove(key);
            Tile(288, ink: 1, param: 64)["w"] = 64;   // split motif uses both atlas coordinates
            Tile(1, 64, ink: 1, param: 64)["w"] = 31; // non-aligned fragment with fractional center
            Tile(32, ink: 1, param: 64);               // overlap fragment
            Tile(1, 96, image: 3, ink: 1, param: 64);  // texture used exclusively by a fragment
            var background = Tile(0, 96, ink: 1, param: 64);
            background["isBackground"] = true;
            background["w"] = 512;
            File.WriteAllText(Path.Combine(root, "tiles.json"), JsonSerializer.Serialize(new
            {
                app = "Regression", frames = new[] { new { name = "TEST", handle = 1, width = 352, height = 128, tiles } },
            }));
            File.WriteAllText(Path.Combine(root, "images.json"), JsonSerializer.Serialize(new
            {
                images = new Dictionary<string, object>
                {
                    ["1"] = new { file = "assets/shared.png", width = 64, height = 32 },
                    ["2"] = new { file = "assets/shared.png", width = 64, height = 32 },
                    ["3"] = new { file = "assets/static.png", width = 32, height = 32 },
                },
            }));
            var originalCulture = CultureInfo.CurrentCulture;
            string output = Path.Combine(root, "godot");
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
                GodotTileExporter.Export(root, output, "res://regression");
            }
            finally { CultureInfo.CurrentCulture = originalCulture; }

            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "tilemap.json")));
            var frame = doc.RootElement.GetProperty("frames")[0];
            var cells = frame.GetProperty("cells");
            Equal(11, cells.GetArrayLength(), "aligned motif must split into both cells");
            var alternatives = cells.EnumerateArray().Select(c => c.GetProperty("alternativeTile").GetInt32()).ToArray();
            Equal(true, alternatives.SequenceEqual(new[] { 0, 1, 1, 2, 3, 4, 5, 6, 7, 1, 0 }), "only equal parameters may share an alternative, scoped to their atlas coordinate");
            Equal(1, cells.EnumerateArray().Select(c => c.GetProperty("source").GetInt32()).Distinct().Count(), "identical PNGs must still share one atlas source");
            float Alpha(JsonElement c) => c.GetProperty("modulate")[3].GetSingle();
            Equal(0.5f, Alpha(cells[1]), "legacy coefficient 64 is half opacity");
            Equal(0f, Alpha(cells[3]), "legacy coefficient 128 is invisible");
            Equal(1f - 128f / 255f, Alpha(cells[4]), "HWA blend uses 255 steps");
            Equal(128f / 255f, cells[5].GetProperty("modulate")[0].GetSingle(), "RGB tint must survive");
            Equal(1f, Alpha(cells[8]), "older IR defaults to opaque white");
            foreach (var fragment in frame.GetProperty("staticBodies").EnumerateArray())
                Equal(0.5f, Alpha(fragment), "non-aligned and overlap fragments must retain their opacity");
            Equal(0.5f, Alpha(frame.GetProperty("backgrounds")[0]), "background metadata must retain opacity");

            string scene = File.ReadAllText(Path.Combine(output, "frames", "TEST", "tilemap.tscn"));
            string encoded = Regex.Match(scene, "tile_map_data = PackedByteArray\\(\"([^\"]+)\"\\)").Groups[1].Value;
            byte[] data = Convert.FromBase64String(encoded);
            Equal(2 + cells.GetArrayLength() * 12, data.Length, "scene cell buffer must have one header and all cells");
            for (int i = 0; i < alternatives.Length; i++)
                Equal(alternatives[i], (int)BitConverter.ToInt16(data, 2 + i * 12 + 10), "baked cell alternative ID must match the manifest");
            Equal(3, Regex.Matches(scene, "modulate = Color\\(1, 1, 1, 0.5\\)").Count, "all fragment sprites must have their own modulation");
            Equal(true, scene.Contains("position = Vector2(16.5, 80)"), "fractional fragment center must use invariant decimals");
            Equal(true, scene.Contains("position = Vector2(-15.5, -16)"), "fractional sprite offset must use invariant decimals");
            Equal(true, File.Exists(Path.Combine(output, "assets", "static.png")), "textures used exclusively by fragments must be copied");
            string tileset = File.ReadAllText(Path.Combine(output, "frames", "TEST", "tileset.tres"));
            Equal(true, tileset.Contains("0:0/1/modulate = Color(1, 1, 1, 0.5)"), "alternative opacity must be serialized using invariant decimals");
            Equal(true, tileset.Contains("0:0/0/physics_layer_0/polygon_0/points"), "solid base must keep its collision");
            Equal(false, tileset.Contains("0:0/1/physics_layer_0/polygon_0/points"), "decorative alternative must not inherit collision from the same sprite");
        });
    }

    static void InTempDirectory(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(root); }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "nebula-gamedumper-tests"));
            if (Path.GetDirectoryName(resolved) != parent)
                throw new InvalidOperationException("Test cleanup path escaped its temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}; expected {expected}, got {actual}");
    }
}
