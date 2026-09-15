using System.Drawing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// Converts the MFSPLExporter sprite sheets (sprites/&lt;object&gt;/sheet.json + aX_dY.png) into Godot
    /// <c>SpriteFrames</c> resources (.tres) plus a per-frame offset manifest. One CTF animation direction
    /// becomes one Godot animation; frames are AtlasTexture regions of that direction's PNG (left-to-right,
    /// original order). Because Godot's SpriteFrames has no per-frame hotspot, the hotspot→offset mapping
    /// (centered mode: offset = (w/2 - hotspotX, h/2 - hotspotY)) is written to offset.json for the engine
    /// runtime to apply.
    /// </summary>
    public class GodotSpriteFramesExporter
    {
        // CTF's per-direction minSpeed/maxSpeed are object velocity thresholds (when the animation
        // plays / how fast it plays relative to movement), NOT an FPS. The closest faithful default is
        // the CTF runtime frame rate (50 FPS) — an animation plays one frame per tick once its movement
        // speed reaches minSpeed. We emit that as the base speed, and also persist minSpeed/maxSpeed into
        // offset.json so the engine runtime can later reproduce CTF's velocity→frame-rate mapping.
        const double DefaultSpeed = 50.0;

        // Mirrored-direction dedup: if a left direction (d16) is the horizontal mirror of its right
        // counterpart (d0), we drop the left one and let the engine flip_h at runtime. Similarity below
        // this threshold keeps both directions.
        const double MirrorSimilarityThreshold = 0.90;

        public class Result
        {
            public int Objects;
            public int Animations;
        }

        public static Result Export(string spritesDir, string godotAbsRoot, string godotResPath)
        {
            Directory.CreateDirectory(godotAbsRoot);
            godotResPath = godotResPath.TrimEnd('/');

            int objects = 0, animations = 0;
            foreach (var dir in Directory.GetDirectories(spritesDir))
            {
                var sheetPath = Path.Combine(dir, "sheet.json");
                if (!File.Exists(sheetPath)) continue;
                try
                {
                    var count = ExportOne(dir, sheetPath, godotAbsRoot, godotResPath);
                    if (count > 0) { objects++; animations += count; }
                }
                catch { /* best-effort per object */ }
            }

            return new Result { Objects = objects, Animations = animations };
        }

        static int ExportOne(string srcDir, string sheetPath, string godotAbsRoot, string godotResPath)
        {
            var sheet = JsonSerializer.Deserialize<SheetJson>(File.ReadAllText(sheetPath), JsonOpts);
            if (sheet == null || sheet.Directions.Count == 0) return 0;

            string objDir = SanitizeDir(Path.GetFileName(srcDir));
            string dstDir = Path.Combine(godotAbsRoot, objDir);
            string resDir = godotResPath + "/" + objDir;

            // Drop left directions that are just the horizontal mirror of their right counterpart.
            DetectMirrorPairs(sheet, srcDir);

            // Recreate the destination cleanly so directions dropped by dedup (and their stale
            // .import files from a previous export) don't linger.
            if (Directory.Exists(dstDir)) Directory.Delete(dstDir, recursive: true);
            Directory.CreateDirectory(dstDir);

            // Copy each kept direction's PNG into the Godot project.
            var copied = new HashSet<string>();
            foreach (var d in sheet.Directions)
            {
                if (d.Skip || string.IsNullOrEmpty(d.Sheet)) continue;
                var src = Path.Combine(srcDir, d.Sheet);
                var dst = Path.Combine(dstDir, d.Sheet);
                if (!File.Exists(src)) continue;
                if (copied.Add(d.Sheet))
                    File.Copy(src, dst, overwrite: true);
            }

            WriteTres(dstDir, resDir, sheet);
            WriteOffsetJson(dstDir, sheet);

            int kept = 0;
            foreach (var d in sheet.Directions) if (!d.Skip) kept++;
            return kept;
        }

        /// <summary>
        /// For each animation that has both a right (d0) and left (d16) direction, compare the left
        /// direction's frames — horizontally flipped — against the right direction's. When the average
        /// per-frame similarity reaches <see cref="MirrorSimilarityThreshold"/>, the left direction is
        /// marked <c>Skip</c> and the right direction is marked <c>Mirrored</c> (engine flips at runtime).
        /// </summary>
        static void DetectMirrorPairs(SheetJson sheet, string srcDir)
        {
            foreach (var group in sheet.Directions.GroupBy(d => d.AnimationIndex))
            {
                var right = group.FirstOrDefault(d => d.DirectionIndex == 0 && !d.Skip);
                var left = group.FirstOrDefault(d => d.DirectionIndex == 16 && !d.Skip);
                if (right == null || left == null) continue;
                if (IsMirrorPair(right, left, srcDir))
                {
                    left.Skip = true;
                    right.Mirrored = true;
                }
            }
        }

        static bool IsMirrorPair(Direction right, Direction left, string srcDir)
        {
            if (right.Frames.Count == 0 || right.Frames.Count != left.Frames.Count) return false;

            var rightSheet = Path.Combine(srcDir, right.Sheet);
            var leftSheet = Path.Combine(srcDir, left.Sheet);
            if (!File.Exists(rightSheet) || !File.Exists(leftSheet)) return false;

            try
            {
                using var rBmp = new Bitmap(rightSheet);
                using var lBmp = new Bitmap(leftSheet);

                // Match each right frame to a same-size left frame (greedy), comparing after a horizontal
                // flip. Mirrored directions may store frames in a different order, so match by geometry.
                var used = new bool[left.Frames.Count];
                double sum = 0;
                foreach (var rf in right.Frames)
                {
                    double best = 0;
                    int bestIdx = -1;
                    for (int i = 0; i < left.Frames.Count; i++)
                    {
                        if (used[i]) continue;
                        var lf = left.Frames[i];
                        if (rf.Rect.W != lf.Rect.W || rf.Rect.H != lf.Rect.H) continue;
                        double sim = FrameMirrorSimilarity(rBmp, rf.Rect, lBmp, lf.Rect);
                        if (sim > best) { best = sim; bestIdx = i; }
                    }
                    if (bestIdx < 0) return false; // no same-size left frame
                    used[bestIdx] = true;
                    sum += best;
                }

                return sum / right.Frames.Count >= MirrorSimilarityThreshold;
            }
            catch { return false; }
        }

        /// <summary>Similarity of a left frame (horizontally flipped) against a same-size right frame.</summary>
        static double FrameMirrorSimilarity(Bitmap rightBmp, Rect rightRect, Bitmap leftBmp, Rect leftRect)
        {
            int w = rightRect.W, h = rightRect.H;
            int total = w * h;
            if (total == 0) return 0;

            int match = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var rc = rightBmp.GetPixel(rightRect.X + x, rightRect.Y + y);
                    var lc = leftBmp.GetPixel(leftRect.X + (w - 1 - x), leftRect.Y + y);
                    if (PixelsEqual(rc, lc)) match++;
                }
            return (double)match / total;
        }

        static bool PixelsEqual(Color a, Color b)
        {
            if (a.A != b.A) return false;
            if (a.A == 0) return true; // both transparent — RGB is undefined, ignore it
            return a.R == b.R && a.G == b.G && a.B == b.B;
        }

        static void WriteTres(string dstDir, string resDir, SheetJson sheet)
        {
            bool single = sheet.Directions.Count == 1;
            var sb = new StringBuilder();
            sb.AppendLine("[gd_resource type=\"SpriteFrames\" format=3]");
            sb.AppendLine();

            // One ext_resource per direction PNG (no uid — Godot assigns one on first import).
            var extIdBySheet = new Dictionary<string, string>();
            int extIdx = 1;
            foreach (var d in sheet.Directions)
            {
                if (d.Skip) continue;
                if (extIdBySheet.ContainsKey(d.Sheet)) continue;
                string id = (extIdx++).ToString();
                extIdBySheet[d.Sheet] = id;
                sb.AppendLine($"[ext_resource type=\"Texture2D\" path=\"{resDir}/{d.Sheet}\" id=\"{id}\"]");
                sb.AppendLine();
            }

            // AtlasTexture per frame.
            int subIdx = 0;
            var subByKey = new Dictionary<string, string>();
            var anims = new List<(string name, Direction dir, List<string> subIds)>();

            foreach (var d in sheet.Directions)
            {
                if (d.Skip) continue;
                string name = single ? "default" : $"anim{d.AnimationIndex}_d{d.DirectionIndex}";
                var subIds = new List<string>();
                foreach (var f in d.Frames)
                {
                    string key = $"{d.Sheet}:{f.Rect.X}:{f.Rect.Y}:{f.Rect.W}:{f.Rect.H}";
                    if (!subByKey.TryGetValue(key, out var sid))
                    {
                        sid = "AtlasTexture_" + (subIdx++);
                        subByKey[key] = sid;
                        sb.AppendLine($"[sub_resource type=\"AtlasTexture\" id=\"{sid}\"]");
                        sb.AppendLine($"atlas = ExtResource(\"{extIdBySheet[d.Sheet]}\")");
                        sb.AppendLine($"region = Rect2({f.Rect.X}, {f.Rect.Y}, {f.Rect.W}, {f.Rect.H})");
                        sb.AppendLine();
                    }
                    subIds.Add(sid);
                }
                anims.Add((name, d, subIds));
            }

            sb.AppendLine("[resource]");
            sb.AppendLine("animations = [{");
            for (int i = 0; i < anims.Count; i++)
            {
                var (name, dir, subIds) = anims[i];
                sb.AppendLine("\"frames\": [{");
                for (int j = 0; j < subIds.Count; j++)
                {
                    sb.AppendLine("\"duration\": 1.0,");
                    sb.AppendLine($"\"texture\": SubResource(\"{subIds[j]}\")");
                    sb.AppendLine(j < subIds.Count - 1 ? "}, {" : "}],");
                }
                sb.AppendLine($"\"loop\": {(dir.Repeat == 0 ? "true" : "false")},");
                sb.AppendLine($"\"name\": &{QuoteString(name)},");
                sb.AppendLine($"\"speed\": {DefaultSpeed.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)}");
                sb.AppendLine(i < anims.Count - 1 ? "}, {" : "}]");
            }

            File.WriteAllText(Path.Combine(dstDir, Path.GetFileName(dstDir) + ".tres"), sb.ToString());
        }

        static void WriteOffsetJson(string dstDir, SheetJson sheet)
        {
            bool single = sheet.Directions.Count == 1;
            var anims = new List<object?>();
            foreach (var d in sheet.Directions)
            {
                if (d.Skip) continue;
                string name = single ? "default" : $"anim{d.AnimationIndex}_d{d.DirectionIndex}";
                var frames = new List<object?>();
                foreach (var f in d.Frames)
                {
                    // centered=true: offset = (w/2 - hotspotX, h/2 - hotspotY)
                    double ox = f.Rect.W / 2.0 - f.HotspotX;
                    double oy = f.Rect.H / 2.0 - f.HotspotY;
                    frames.Add(new Dictionary<string, object?>
                    {
                        ["handle"] = f.Handle,
                        ["offsetX"] = ox,
                        ["offsetY"] = oy,
                    });
                }
                anims.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["animationIndex"] = d.AnimationIndex,
                    ["directionIndex"] = d.DirectionIndex,
                    ["sourceAnimation"] = d.Animation,
                    ["mirrored"] = d.Mirrored,
                    ["minSpeed"] = d.MinSpeed,
                    ["maxSpeed"] = d.MaxSpeed,
                    ["frames"] = frames,
                });
            }

            var doc = new Dictionary<string, object?>
            {
                ["object"] = sheet.Object,
                ["centered"] = true,
                ["animations"] = anims,
            };
            File.WriteAllText(Path.Combine(dstDir, "offset.json"),
                JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        static string QuoteString(string s)
        {
            // .tres StringName literal: &"..." with minimal escaping for quotes/backslash/newline.
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    default: sb.Append(c); break;
                }
            }
            return "\"" + sb + "\"";
        }

        static string SanitizeDir(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "_" : s;
        }

        static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        sealed class SheetJson
        {
            [JsonPropertyName("object")] public string Object { get; set; } = "";
            [JsonPropertyName("directions")] public List<Direction> Directions { get; set; } = new();
        }
        sealed class Direction
        {
            [JsonPropertyName("animation")] public string Animation { get; set; } = "";
            [JsonPropertyName("animationIndex")] public int AnimationIndex { get; set; }
            [JsonPropertyName("directionIndex")] public int DirectionIndex { get; set; }
            [JsonPropertyName("minSpeed")] public int MinSpeed { get; set; }
            [JsonPropertyName("maxSpeed")] public int MaxSpeed { get; set; }
            [JsonPropertyName("repeat")] public int Repeat { get; set; }
            [JsonPropertyName("repeatFrame")] public int RepeatFrame { get; set; }
            [JsonPropertyName("sheet")] public string Sheet { get; set; } = "";
            [JsonPropertyName("sheetWidth")] public int SheetWidth { get; set; }
            [JsonPropertyName("sheetHeight")] public int SheetHeight { get; set; }
            [JsonPropertyName("frames")] public List<Frame> Frames { get; set; } = new();

            // Runtime-only (not serialized): Skip marks a mirrored left direction that gets dropped;
            // Mirrored marks the surviving right direction that the engine flips at runtime.
            [JsonIgnore] public bool Skip { get; set; }
            [JsonIgnore] public bool Mirrored { get; set; }
        }
        sealed class Frame
        {
            [JsonPropertyName("handle")] public uint Handle { get; set; }
            [JsonPropertyName("rect")] public Rect Rect { get; set; } = new();
            [JsonPropertyName("hotspotX")] public int HotspotX { get; set; }
            [JsonPropertyName("hotspotY")] public int HotspotY { get; set; }
            [JsonPropertyName("actionPointX")] public int ActionPointX { get; set; }
            [JsonPropertyName("actionPointY")] public int ActionPointY { get; set; }
        }
        sealed class Rect
        {
            [JsonPropertyName("x")] public int X { get; set; }
            [JsonPropertyName("y")] public int Y { get; set; }
            [JsonPropertyName("w")] public int W { get; set; }
            [JsonPropertyName("h")] public int H { get; set; }
        }
    }
}
