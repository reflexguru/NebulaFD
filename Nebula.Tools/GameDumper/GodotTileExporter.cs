using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// Converts the faithful backdrop list from MFSPLExporter.ExportTiles (tiles.json) into Godot
    /// tile data. Every backdrop instance is routed by three rules:
    ///
    /// 1. isBackground (image==0 full-frame backdrop / solid / gradient): NEVER split. Kept in
    ///    tilemap.json under "backgrounds" for the level author to place; no nodes are generated.
    /// 2. Otherwise, if the rect is exactly 32x32 AND both x/y are multiples of 32 → a plain TileMap cell.
    /// 3. Otherwise:
    ///    - rects larger than 32 in x or y are split into ceil(w/32) x ceil(h/32) sub-tiles
    ///      (only keeping the overlap with the original rect); sub-tiles that land exactly on 32-aligned
    ///      cells become TileMap cells, the rest become StaticBody2D fragments.
    ///    - rects that are not 32-aligned (regardless of size) become StaticBody2D fragments.
    ///    - if several tiles claim the same TileMap cell (overlap conflict), the one that appears
    ///      LOWER in the source order stays as a StaticBody2D, the higher (first-in-order) wins the cell.
    ///
    /// Tile atlas coords are computed from the source image via motif (tiling) sampling: a sub-tile at
    /// offset (dx,dy) inside the object rect shows the image region ((dx mod imgW), (dy mod imgH)).
    /// Only image-aligned 32x32 regions that fit inside the image become TileMap cells; everything else
    /// is emitted as a StaticBody2D/Sprite2D fragment carrying its own image region.
    ///
    /// Outputs per level: tilemap.json (cells + statics + backgrounds), tileset.tres (TileSetAtlasSource
    /// over the asset PNGs, with collision polygons on obstacle tiles), tilemap.tscn. The TileMapLayer
    /// cells are baked into tile_map_data (Godot 4 format) AND every StaticBody2D fragment (overlap
    /// losers, non-aligned pieces) is baked as a scene node, so the whole level shows in the editor
    /// with zero script execution and opens fast.
    /// </summary>
    public class GodotTileExporter
    {
        public const int Cell = 32;

        public class Result
        {
            public int Frames;
            public int CellTiles;
            public int StaticBodies;
            public int Backgrounds;
            public int Conflicts;
        }

        sealed class ImageInfo
        {
            public uint Handle;
            public string File = "";
            public int Width;
            public int Height;
        }

        /// <param name="tilesJsonDir">Level dir containing tiles.json (and images.json for asset mapping).</param>
        /// <param name="godotAbsRoot">Absolute destination dir for the generated files.</param>
        /// <param name="godotResPath">res:// path prefix for the generated files.</param>
        public static Result Export(string tilesJsonDir, string godotAbsRoot, string godotResPath)
        {
            Directory.CreateDirectory(godotAbsRoot);
            godotResPath = godotResPath.TrimEnd('/');

            var tilesDoc = JsonSerializer.Deserialize<TilesDoc>(File.ReadAllText(Path.Combine(tilesJsonDir, "tiles.json")), JsonOpts);
            if (tilesDoc == null) throw new InvalidDataException("tiles.json is empty or invalid.");

            // asset file mapping from images.json: imageHandle → (png path, size).
            var imageInfo = LoadImageMap(tilesJsonDir);

            // Build the shared image → TileSet source mapping used by BOTH tilemap.json and tileset.tres,
            // so the two agree on which source_id a given image/file maps to.
            var sourceForImage = new Dictionary<uint, int>();
            var fileToSource = new Dictionary<string, int>();
            int nextSource = 0;
            foreach (var handle in tilesDoc.Frames
                         .SelectMany(f => f.Tiles)
                         .Where(t => !t.IsBackground)
                         .Select(t => t.Image).Distinct().OrderBy(h => h))
            {
                if (!imageInfo.TryGetValue(handle, out var info)) continue;
                if (string.IsNullOrEmpty(info.File)) continue;
                if (!fileToSource.TryGetValue(info.File, out int sid))
                {
                    sid = nextSource++;
                    fileToSource[info.File] = sid;
                }
                sourceForImage[handle] = sid;
            }

            var result = new Result { Frames = tilesDoc.Frames.Count };
            var framesOut = new List<object?>();
            // Every TileMap cell across all frames, merged in frame order (later frames overwrite
            // earlier ones on the same cell, matching the old runtime set_cell order). Baked into the
            // scene's tile_map_data so the grid shows without any script/@tool building.
            var allCells = new Dictionary<(int cx, int cy), CellEntry>();
            // Every StaticBody2D fragment across all frames (overlap losers + non-aligned pieces).
            // Baked into tilemap.tscn as scene nodes so they are visible in the editor without any
            // runtime or @tool building.
            var allStatics = new List<object?>();

            // Collect every (atlasX, atlasY) actually used within each source, so tileset.tres can
            // register only the cells the map references.
            var sourceAtlas = new Dictionary<int, HashSet<(int x, int y)>>();
            // (source, atlas) → isObstacle: a tile collides if any placed cell using it is obstacle.
            var sourceAtlasObstacle = new HashSet<(int source, int ax, int ay)>();

            foreach (var frame in tilesDoc.Frames)
            {
                // backgrounds: full-frame, emitted once, never tiled.
                var backgrounds = new List<object?>();
                // cells: (cellX, cellY) -> tile info; later-in-order loses to earlier on conflict.
                var cellMap = new Dictionary<(int cx, int cy), CellEntry>();
                // static bodies (fragments that don't fit a 32-aligned cell).
                var statics = new List<object?>();

                foreach (var tile in frame.Tiles)
                {
                    imageInfo.TryGetValue(tile.Image, out var img);
                    string file = img?.File ?? "";
                    string resFile = file.Length > 0 ? ToResPath(file, godotResPath) : "";

                    if (tile.IsBackground)
                    {
                        backgrounds.Add(new Dictionary<string, object?>
                        {
                            ["x"] = tile.X, ["y"] = tile.Y, ["w"] = tile.W, ["h"] = tile.H,
                            ["image"] = tile.Image,
                            ["res"] = resFile,
                            ["fillType"] = tile.FillType,
                            ["color1"] = tile.Color1, ["color2"] = tile.Color2,
                            ["obstacle"] = tile.Obstacle,
                            ["object"] = tile.Object, ["layer"] = tile.Layer,
                        });
                        result.Backgrounds++;
                        continue;
                    }

                    // Split into 32x32 sub-tiles covering the original rect (clipped to it).
                    int sx = Math.Max(1, (int)Math.Ceiling(tile.W / (double)Cell));
                    int sy = Math.Max(1, (int)Math.Ceiling(tile.H / (double)Cell));

                    for (int j = 0; j < sy; j++)
                        for (int i = 0; i < sx; i++)
                        {
                            int subX = tile.X + i * Cell;
                            int subY = tile.Y + j * Cell;
                            int subW = Math.Min(Cell, tile.X + tile.W - subX);
                            int subH = Math.Min(Cell, tile.Y + tile.H - subY);
                            if (subW <= 0 || subH <= 0) continue;

                            // Exactly 32x32 and aligned → candidate TileMap cell.
                            bool aligned = subX % Cell == 0 && subY % Cell == 0
                                        && subW == Cell && subH == Cell;
                            int cx = subX / Cell, cy = subY / Cell;

                            // Image region via motif tiling: this sub-block's offset inside the object
                            // rect maps to the image with wrapping. (dx,dy are always non-negative here.)
                            int dx = subX - tile.X;
                            int dy = subY - tile.Y;
                            int imgX = img != null && img.Width > 0 ? dx % img.Width : 0;
                            int imgY = img != null && img.Height > 0 ? dy % img.Height : 0;

                            // Can this sub-block be a TileMap cell? Need a full 32x32 region starting on
                            // a 32-aligned point that still fits inside the image. The image itself must
                            // also be a multiple of 32 on both axes: Godot's TileSetAtlasSource slices the
                            // texture on a 32px grid, and a non-multiple texture would create an out-of-grid
                            // tile → "Image width 0" errors. Such images fall back to StaticBody2D.
                            bool tileable = aligned
                                         && img != null
                                         && img.Width > 0 && img.Height > 0
                                         && img.Width % Cell == 0 && img.Height % Cell == 0
                                         && imgX % Cell == 0 && imgY % Cell == 0
                                         && imgX + Cell <= img.Width
                                         && imgY + Cell <= img.Height;

                            if (tileable)
                            {
                                int atlasX = imgX / Cell, atlasY = imgY / Cell;
                                // Overlap conflict: if the cell is already claimed, the later-in-order
                                // tile becomes a StaticBody2D instead of overwriting the winner.
                                if (cellMap.TryGetValue((cx, cy), out var existing))
                                {
                                    statics.Add(BuildStatic(tile, subX, subY, subW, subH,
                                        resFile, imgX, imgY, subW, subH, conflictWith: existing.Order));
                                    result.Conflicts++;
                                    continue;
                                }
                                sourceForImage.TryGetValue(tile.Image, out int sid);
                                if (!sourceAtlas.TryGetValue(sid, out var atlasSet))
                                    sourceAtlas[sid] = atlasSet = new HashSet<(int, int)>();
                                atlasSet.Add((atlasX, atlasY));
                                if (tile.Obstacle != 0)
                                    sourceAtlasObstacle.Add((sid, atlasX, atlasY));
                                cellMap[(cx, cy)] = new CellEntry
                                {
                                    Order = tile.Order,
                                    X = cx, Y = cy,
                                    AtlasX = atlasX, AtlasY = atlasY,
                                    Image = tile.Image, Source = sid,
                                    Obstacle = tile.Obstacle,
                                    Object = tile.Object, Layer = tile.Layer,
                                };
                                result.CellTiles++;
                            }
                            else
                            {
                                // Non-tileable fragment. Region is clipped to the image bounds.
                                int regW = img != null && img.Width > 0 ? Math.Min(subW, img.Width - imgX) : subW;
                                int regH = img != null && img.Height > 0 ? Math.Min(subH, img.Height - imgY) : subH;
                                statics.Add(BuildStatic(tile, subX, subY, subW, subH,
                                    resFile, imgX, imgY, regW, regH, conflictWith: null));
                                result.StaticBodies++;
                            }
                        }
                }

                // TileMap cells sorted deterministically.
                var cells = cellMap.Values
                    .OrderBy(c => c.Y).ThenBy(c => c.X)
                    .Select(c => (object)new Dictionary<string, object?>
                    {
                        ["x"] = c.X, ["y"] = c.Y,
                        ["source"] = c.Source,
                        ["atlasX"] = c.AtlasX, ["atlasY"] = c.AtlasY,
                        ["image"] = c.Image,
                        ["obstacle"] = c.Obstacle,
                        ["object"] = c.Object, ["layer"] = c.Layer, ["order"] = c.Order,
                    }).ToList();

                // Merge this frame's cells into the baked scene data (later frames win the cell).
                foreach (var c in cellMap.Values)
                    allCells[(c.X, c.Y)] = c;
                // Fragments (overlap losers + non-aligned pieces) are baked as scene nodes too.
                allStatics.AddRange(statics);

                framesOut.Add(new Dictionary<string, object?>
                {
                    ["name"] = frame.Name, ["handle"] = frame.Handle,
                    ["width"] = frame.Width, ["height"] = frame.Height,
                    ["backgrounds"] = backgrounds,
                    ["cells"] = cells,
                    ["staticBodies"] = statics,
                });
            }

            var doc = new Dictionary<string, object?>
            {
                ["app"] = tilesDoc.App,
                ["cellSize"] = Cell,
                ["frames"] = framesOut,
            };
            File.WriteAllText(Path.Combine(godotAbsRoot, "tilemap.json"),
                JsonSerializer.Serialize(doc, JsonOptsIndented));

            WriteTileset(godotAbsRoot, godotResPath, imageInfo, sourceForImage, sourceAtlas, sourceAtlasObstacle, tilesDoc);
            WriteScene(godotAbsRoot, godotResPath, allCells.Values, allStatics);

            return result;
        }

        /// <summary>Reads images.json and returns imageHandle → (png path, size).</summary>
        static Dictionary<uint, ImageInfo> LoadImageMap(string tilesJsonDir)
        {
            var map = new Dictionary<uint, ImageInfo>();
            var imgPath = Path.Combine(tilesJsonDir, "images.json");
            if (!File.Exists(imgPath)) return map;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(imgPath));
                if (!doc.RootElement.TryGetProperty("images", out var imgs)) return map;
                foreach (var prop in imgs.EnumerateObject())
                {
                    if (!uint.TryParse(prop.Name, out var handle)) continue;
                    var info = new ImageInfo { Handle = handle };
                    if (prop.Value.TryGetProperty("file", out var f))
                        info.File = Path.GetFullPath(Path.Combine(tilesJsonDir, f.GetString() ?? ""));
                    if (prop.Value.TryGetProperty("width", out var w)) info.Width = w.GetInt32();
                    if (prop.Value.TryGetProperty("height", out var h)) info.Height = h.GetInt32();
                    map[handle] = info;
                }
            }
            catch { /* best-effort */ }
            return map;
        }

        static Dictionary<string, object?> BuildStatic(TileJson tile, int x, int y, int w, int h,
            string resFile, int regionX, int regionY, int regionW, int regionH, int? conflictWith)
        {
            return new Dictionary<string, object?>
            {
                ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h,
                ["res"] = resFile,
                ["regionX"] = regionX, ["regionY"] = regionY,
                ["regionW"] = regionW, ["regionH"] = regionH,
                ["obstacle"] = tile.Obstacle,
                ["object"] = tile.Object, ["layer"] = tile.Layer,
                ["order"] = tile.Order, ["conflictWith"] = conflictWith,
            };
        }

        /// <summary>
        /// Writes a tileset.tres holding one TileSetAtlasSource per distinct tile PNG. Each source
        /// slices its texture into 32x32 cells and registers only the atlas cells the map actually
        /// references (from <paramref name="sourceAtlas"/>). Because each source is backed by its own
        /// texture, the same atlas origin does not collide across sources. Source ids are derived from
        /// <paramref name="sourceForImage"/>, matching tilemap.json.
        /// </summary>
        static void WriteTileset(string godotAbsRoot, string godotResPath,
            Dictionary<uint, ImageInfo> imageInfo, Dictionary<uint, int> sourceForImage,
            Dictionary<int, HashSet<(int x, int y)>> sourceAtlas,
            HashSet<(int source, int ax, int ay)> sourceAtlasObstacle,
            TilesDoc doc)
        {
            // Rebuild the distinct file → source list, preserving the same ids as sourceForImage.
            // Only sources that actually contribute at least one atlas cell are registered: images that
            // were rejected by the tileability check (e.g. not a multiple of 32) never produce cells,
            // and must not become TileSetAtlasSource textures (Godot would error on the out-of-grid tile).
            var sources = new List<(int id, string file)>();
            foreach (var kv in sourceForImage.OrderBy(kv => kv.Value))
            {
                if (!imageInfo.TryGetValue(kv.Key, out var info)) continue;
                if (string.IsNullOrEmpty(info.File)) continue;
                if (!sourceAtlas.ContainsKey(kv.Value)) continue;
                sources.Add((kv.Value, info.File));
            }
            sources = sources.Distinct().ToList();

            // Copy EVERY referenced image (tile sources + background images) into godotAbsRoot/assets so
            // the res:// references in tileset.tres / tilemap.json resolve inside the Godot project.
            // Backgrounds are not TileSet sources but still need their PNG present for Sprite2D loading.
            var copyFiles = sources.Select(s => s.file).ToHashSet();
            foreach (var bg in doc.Frames.SelectMany(f => f.Tiles).Where(t => t.IsBackground))
            {
                if (imageInfo.TryGetValue(bg.Image, out var bi) && !string.IsNullOrEmpty(bi.File))
                    copyFiles.Add(bi.File);
            }
            foreach (var file in copyFiles)
            {
                try
                {
                    string dstDir = Path.Combine(godotAbsRoot, "assets");
                    Directory.CreateDirectory(dstDir);
                    string dst = Path.Combine(dstDir, Path.GetFileName(file));
                    if (File.Exists(file) && !File.Exists(dst))
                        File.Copy(file, dst, overwrite: false);
                }
                catch { /* best-effort asset copy */ }
            }

            if (sources.Count == 0) return; // nothing to tile

            var sb = new StringBuilder();
            sb.AppendLine("[gd_resource type=\"TileSet\" format=3]");
            sb.AppendLine();

            // ext_resource per distinct png.
            int extIdx = 1;
            var extIdBySource = new Dictionary<int, string>();
            foreach (var (id, file) in sources)
            {
                string eid = (extIdx++).ToString();
                extIdBySource[id] = eid;
                string resPath = ToResPath(file, godotResPath);
                sb.AppendLine($"[ext_resource type=\"Texture2D\" path=\"{resPath}\" id=\"{eid}\"]");
                sb.AppendLine();
            }

            // One TileSetAtlasSource sub_resource per source, registering only used atlas cells.
            int subIdx = 0;
            var subIdBySource = new Dictionary<int, string>();
            foreach (var (id, _) in sources)
            {
                string sid = "tileset_source_" + (subIdx++);
                subIdBySource[id] = sid;
                sb.AppendLine($"[sub_resource type=\"TileSetAtlasSource\" id=\"{sid}\"]");
                sb.AppendLine($"texture = ExtResource(\"{extIdBySource[id]}\")");
                sb.AppendLine("texture_region_size = Vector2i(32, 32)");
                if (sourceAtlas.TryGetValue(id, out var atlasSet))
                    foreach (var (ax, ay) in atlasSet.OrderBy(c => c.y).ThenBy(c => c.x))
                    {
                        sb.AppendLine($"{ax}:{ay}/0 = 0");
                        // Obstacle tiles get a full-cell collision polygon (matches the manual
                        // tile_champion-4.tres convention: -16..16 centered on the 32px cell).
                        if (sourceAtlasObstacle.Contains((id, ax, ay)))
                        {
                            sb.AppendLine($"{ax}:{ay}/0/physics_layer_0/polygon_0/points = PackedVector2Array(-16, -16, 16, -16, 16, 16, -16, 16)");
                            sb.AppendLine($"{ax}:{ay}/0/physics_layer_0/polygon_0/one_way = false");
                        }
                    }
                sb.AppendLine();
            }

            sb.AppendLine("[resource]");
            sb.AppendLine("tile_size = Vector2i(32, 32)");
            sb.AppendLine("physics_layer_0/collision_layer = 1");
            sb.AppendLine("physics_layer_0/collision_mask = 1");
            sb.AppendLine();
            // Godot 4 requires expanded dictionary entries (sources/N = ...) for sub-resource values;
            // an inline { 0: SubResource(...) } block is NOT parsed and yields an empty TileSet.
            foreach (var (id, _) in sources)
                sb.AppendLine($"sources/{id} = SubResource(\"{subIdBySource[id]}\")");

            File.WriteAllText(Path.Combine(godotAbsRoot, "tileset.tres"), sb.ToString());
        }

        /// <summary>
        /// Writes tilemap.tscn: a root Node2D with a TileMapLayer whose cells are baked into
        /// tile_map_data, and every StaticBody2D fragment (overlap losers + non-aligned pieces)
        /// baked as a scene node. Nothing runs a script at load time, so the scene opens instantly
        /// in the editor with the full grid AND the fragments visible. Backgrounds are the level
        /// author's responsibility; their data stays in tilemap.json "backgrounds".
        /// </summary>
        static void WriteScene(string godotAbsRoot, string godotResPath,
            IEnumerable<CellEntry> cells, List<object?> statics)
        {
            // --- dedupe textures (ext_resource) and collision rects (sub_resource) ---
            var texPaths = new List<string>();
            var texId = new Dictionary<string, string>();
            var rectSizes = new List<(int w, int h)>();
            var rectId = new Dictionary<(int w, int h), string>();
            foreach (var sObj in statics)
            {
                var s = (Dictionary<string, object?>)sObj;
                var res = (string?)s["res"];
                if (!string.IsNullOrEmpty(res) && !texId.ContainsKey(res))
                {
                    texId[res] = "tex_" + texPaths.Count;
                    texPaths.Add(res);
                }
                if ((uint)s["obstacle"] != 0)
                {
                    var size = ((int)s["w"], (int)s["h"]);
                    if (!rectId.ContainsKey(size))
                    {
                        rectId[size] = "rect_" + rectSizes.Count;
                        rectSizes.Add(size);
                    }
                }
            }

            // --- scene ---
            var tscn = new StringBuilder();
            int loadSteps = 2 + texPaths.Count + rectSizes.Count; // 1 tileset + N textures + M rects + 1
            tscn.AppendLine($"[gd_scene load_steps={loadSteps} format=3]");
            tscn.AppendLine();
            tscn.AppendLine($"[ext_resource type=\"TileSet\" path=\"{godotResPath}/tileset.tres\" id=\"1_tileset\"]");
            foreach (var p in texPaths)
                tscn.AppendLine($"[ext_resource type=\"Texture2D\" path=\"{p}\" id=\"{texId[p]}\"]");
            if (texPaths.Count > 0) tscn.AppendLine();
            foreach (var (w, h) in rectSizes)
            {
                tscn.AppendLine($"[sub_resource type=\"RectangleShape2D\" id=\"{rectId[(w, h)]}\"]");
                tscn.AppendLine($"size = Vector2({w}, {h})");
                tscn.AppendLine();
            }

            tscn.AppendLine("[node name=\"Level\" type=\"Node2D\"]");
            tscn.AppendLine();
            tscn.AppendLine("[node name=\"TileMapLayer\" type=\"TileMapLayer\" parent=\".\"]");
            tscn.AppendLine("tile_set = ExtResource(\"1_tileset\")");
            tscn.AppendLine($"tile_map_data = PackedByteArray(\"{BuildTileMapData(cells)}\")");
            tscn.AppendLine();

            int si = 0;
            foreach (var sObj in statics)
            {
                var s = (Dictionary<string, object?>)sObj;
                int x = (int)s["x"], y = (int)s["y"];
                int w = (int)s["w"], h = (int)s["h"];
                bool obstacle = (uint)s["obstacle"] != 0;
                string? res = (string?)s["res"];
                bool hasTex = !string.IsNullOrEmpty(res);

                tscn.AppendLine($"[node name=\"static_{si}\" type=\"StaticBody2D\" parent=\".\"]");
                tscn.AppendLine($"position = Vector2({x + w * 0.5}, {y + h * 0.5})");
                if (obstacle)
                {
                    tscn.AppendLine();
                    tscn.AppendLine($"[node name=\"Shape\" type=\"CollisionShape2D\" parent=\"static_{si}\"]");
                    tscn.AppendLine($"shape = SubResource(\"{rectId[(w, h)]}\")");
                }
                if (hasTex)
                {
                    tscn.AppendLine();
                    tscn.AppendLine($"[node name=\"Sprite\" type=\"Sprite2D\" parent=\"static_{si}\"]");
                    tscn.AppendLine($"texture = ExtResource(\"{texId[res!]}\")");
                    tscn.AppendLine("centered = false");
                    tscn.AppendLine("region_enabled = true");
                    tscn.AppendLine($"region_rect = Rect2({(int)s["regionX"]}, {(int)s["regionY"]}, {(int)s["regionW"]}, {(int)s["regionH"]})");
                    tscn.AppendLine($"position = Vector2({-w * 0.5}, {-h * 0.5})");
                }
                tscn.AppendLine();
                si++;
            }

            File.WriteAllText(Path.Combine(godotAbsRoot, "tilemap.tscn"), tscn.ToString());
        }

        /// <summary>
        /// Serializes TileMap cells into Godot 4's tile_map_data format: a leading int16 format id
        /// (0), then 12 bytes per cell of little-endian int16 (cellX, cellY, sourceId, atlasX,
        /// atlasY, alternativeTileId). The format header must be present or Godot rejects the data
        /// ("Unsupported tile map data format").
        /// </summary>
        static string BuildTileMapData(IEnumerable<CellEntry> cells)
        {
            using var ms = new MemoryStream();
            WriteS16(ms, 0); // format id (must be <= 0)
            foreach (var c in cells.OrderBy(c => c.Y).ThenBy(c => c.X))
            {
                WriteS16(ms, c.X);
                WriteS16(ms, c.Y);
                WriteS16(ms, c.Source);
                WriteS16(ms, c.AtlasX);
                WriteS16(ms, c.AtlasY);
                WriteS16(ms, 0); // alternative tile
            }
            return Convert.ToBase64String(ms.ToArray());
        }

        static void WriteS16(Stream s, int v)
        {
            s.WriteByte((byte)(v & 0xFF));
            s.WriteByte((byte)((v >> 8) & 0xFF));
        }

        static string ToResPath(string absFile, string godotResPath)
        {
            // map ..\assets\img_x.png → godotResPath/assets/img_x.png (relative, no drive letters)
            var norm = absFile.Replace('\\', '/');
            int idx = norm.LastIndexOf("/assets/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                return godotResPath + norm.Substring(idx);
            return godotResPath + "/" + Path.GetFileName(absFile);
        }

        sealed class CellEntry
        {
            public int Order;
            public int X, Y;
            public int AtlasX, AtlasY;
            public uint Image;
            public int Source;
            public uint Obstacle;
            public string Object = "";
            public uint Layer;
        }

        sealed class TilesDoc
        {
            [JsonPropertyName("app")] public string App { get; set; } = "";
            [JsonPropertyName("frames")] public List<FrameTiles> Frames { get; set; } = new();
        }
        sealed class FrameTiles
        {
            [JsonPropertyName("name")] public string Name { get; set; } = "";
            [JsonPropertyName("handle")] public int Handle { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
            [JsonPropertyName("tiles")] public List<TileJson> Tiles { get; set; } = new();
        }
        sealed class TileJson
        {
            [JsonPropertyName("x")] public int X { get; set; }
            [JsonPropertyName("y")] public int Y { get; set; }
            [JsonPropertyName("w")] public int W { get; set; }
            [JsonPropertyName("h")] public int H { get; set; }
            [JsonPropertyName("obstacle")] public uint Obstacle { get; set; }
            [JsonPropertyName("image")] public uint Image { get; set; }
            [JsonPropertyName("fillType")] public int FillType { get; set; }
            [JsonPropertyName("color1")] public int Color1 { get; set; }
            [JsonPropertyName("color2")] public int Color2 { get; set; }
            [JsonPropertyName("isBackground")] public bool IsBackground { get; set; }
            [JsonPropertyName("object")] public string Object { get; set; } = "";
            [JsonPropertyName("objectType")] public int ObjectType { get; set; }
            [JsonPropertyName("layer")] public uint Layer { get; set; }
            [JsonPropertyName("order")] public int Order { get; set; }
        }

        static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        static readonly JsonSerializerOptions JsonOptsIndented = new() { WriteIndented = true };
    }
}
