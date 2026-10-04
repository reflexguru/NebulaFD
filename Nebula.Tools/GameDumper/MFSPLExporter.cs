using Nebula;
using Nebula.Core.Data.Chunks.BankChunks.Images;
using Nebula.Core.Data.Chunks.BankChunks.Sounds;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon.ObjectMovementDefinitions;
using Nebula.Core.Data.Chunks.ChunkTypes;
using Nebula.Core.Data.PackageReaders;
using Nebula.Core.Memory;
using Nebula.Core.Utilities;
using Spectre.Console;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Event = Nebula.Core.Data.Chunks.FrameChunks.Events.Event;
using Action = Nebula.Core.Data.Chunks.FrameChunks.Events.Action;
using Condition = Nebula.Core.Data.Chunks.FrameChunks.Events.Condition;
using ACEventBase = Nebula.Core.Data.Chunks.FrameChunks.Events.ACEventBase;
using EventObject = Nebula.Core.Data.Chunks.FrameChunks.Events.EventObject;
using Parameter = Nebula.Core.Data.Chunks.FrameChunks.Events.Parameter;
using Nebula.Core.Data.Chunks.FrameChunks.Events.Parameters;
using Nebula.Core.Data.Chunks.MFAChunks;
using Image = Nebula.Core.Data.Chunks.BankChunks.Images.Image;

#pragma warning disable CS8602
#pragma warning disable CA1416

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// Dumps object instances and events (with cross-frame dedup) as JSON,
    /// serving as the intermediate representation (IR) for the CTF → Godot migration.
    /// Output: Dumps\&lt;AppName&gt;\mfspl\objects.json + events.json
    /// </summary>
    public class MFSPLExporter : INebulaTool
    {
        public string Name => "MFSPL Exporter";

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public void Execute()
        {
            if (NebulaCore.PackageData is not MFAPackageData mfa)
            {
                AnsiConsole.MarkupLine("[red]MFSPL Exporter only supports MFA projects.[/]");
                return;
            }

            string basePath = "Dumps\\" + Utilities.ClearName(mfa.AppName) + "\\mfspl\\";
            var result = Export(mfa, basePath);
            var assets = ExportAssets(mfa, basePath, basePath);
            AnsiConsole.MarkupLine($"[green]Done. {result.Objects} objects, {result.Frames} frames; {assets.Images} images ({assets.ImageDuplicates} dup), {assets.Sounds} sounds ({assets.SoundDuplicates} dup) → {Path.GetFullPath(basePath)}[/]");
        }

        /// <summary>
        /// Exports objects.json + events.json for an already-parsed MFA into <paramref name="outputDir"/>.
        /// Returns summary counts. Used both by the interactive tool and the batch CLI.
        /// </summary>
        public static ExportResult Export(MFAPackageData mfa, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, "objects.json"), JsonSerializer.Serialize(BuildObjectsDoc(mfa), JsonOptions));
            File.WriteAllText(Path.Combine(outputDir, "events.json"), JsonSerializer.Serialize(BuildEventsDoc(mfa), new JsonSerializerOptions { WriteIndented = false }));
            return new ExportResult { Frames = mfa.Frames.Count, Objects = mfa.FrameItems.Items.Count };
        }

        /// <summary>
        /// Exports self-contained media alongside an MFA's IR manifests. Deduplication is local
        /// to this MFA so another export/session cannot skip its files or overwrite its sheets.
        /// </summary>
        public static (AssetExportResult Assets, SpritesheetResult Sprites) ExportResources(MFAPackageData mfa, string outputDir)
        {
            var assets = ExportAssets(mfa, outputDir, Path.Combine(outputDir, "assets"));
            string spritesDir = Path.Combine(outputDir, "sprites");
            var sprites = ExportSpritesheets(mfa, spritesDir);
            WriteSpriteIndex(spritesDir);
            return (assets, sprites);
        }

        /// <summary>
        /// Dumps ImageBank (PNG) and SoundBank (audio) as one flat asset folder, deduplicating by content
        /// hash so identical assets are written only once. Per-handle metadata (hotspot / action point /
        /// dimensions / name) is preserved in images.json + sounds.json so the dedup never loses the
        /// handle → file mapping used by objects.json / events.json.
        /// </summary>
        /// <param name="manifestDir">Where images.json / sounds.json are written (per level).</param>
        /// <param name="assetDir">Where the deduplicated PNG / audio files are written (shared across levels).</param>
        /// <param name="imagePool">Optional cross-level hash → filename map, so duplicates across MFAs collapse to one file.</param>
        /// <param name="soundPool">Optional cross-level hash → filename map for sounds.</param>
        public static AssetExportResult ExportAssets(
            MFAPackageData mfa,
            string manifestDir,
            string assetDir,
            Dictionary<string, string>? imagePool = null,
            Dictionary<string, string>? soundPool = null)
        {
            Directory.CreateDirectory(manifestDir);
            Directory.CreateDirectory(assetDir);
            var imgByHash = imagePool ?? new Dictionary<string, string>();
            var sndByHash = soundPool ?? new Dictionary<string, string>();

            // ---------------- images ----------------
            int imgUnique = 0, imgDup = 0, imgFail = 0;
            var imagesDoc = new Dictionary<string, object?>();

            foreach (var kv in mfa.ImageBank.Images)
            {
                try
                {
                    Image img = kv.Value;
                    byte[] png = MfaImageEncoder.ToPng(img);
                    if (png.Length == 0) { imgFail++; continue; }

                    string hash = Convert.ToHexString(SHA256.HashData(png))[..16].ToLowerInvariant();
                    string file;
                    if (imgByHash.TryGetValue(hash, out var existing))
                    {
                        file = existing;
                        imgDup++;
                    }
                    else
                    {
                        file = $"img_{hash}.png";
                        imgByHash[hash] = file;
                        File.WriteAllBytes(Path.Combine(assetDir, file), png);
                        imgUnique++;
                    }

                    imagesDoc[kv.Key.ToString()] = new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetRelativePath(manifestDir, Path.Combine(assetDir, file)),
                        ["width"] = img.Width,
                        ["height"] = img.Height,
                        ["hotspotX"] = img.HotspotX,
                        ["hotspotY"] = img.HotspotY,
                        ["actionPointX"] = img.ActionPointX,
                        ["actionPointY"] = img.ActionPointY,
                    };
                }
                catch { imgFail++; }
            }

            File.WriteAllText(Path.Combine(manifestDir, "images.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["unique"] = imgUnique,
                ["duplicates"] = imgDup,
                ["failed"] = imgFail,
                ["images"] = imagesDoc,
            }, JsonOptions));

            // ---------------- sounds ----------------
            int sndUnique = 0, sndDup = 0, sndFail = 0;
            var soundsDoc = new Dictionary<string, object?>();

            foreach (var kv in mfa.SoundBank.Sounds)
            {
                try
                {
                    Sound snd = kv.Value;
                    if (snd.Data.Length == 0) { sndFail++; continue; }

                    string hash = Convert.ToHexString(SHA256.HashData(snd.Data))[..16].ToLowerInvariant();
                    string file;
                    if (sndByHash.TryGetValue(hash, out var existing))
                    {
                        file = existing;
                        sndDup++;
                    }
                    else
                    {
                        file = $"snd_{hash}{GetSoundExtension(snd.Data)}";
                        sndByHash[hash] = file;
                        File.WriteAllBytes(Path.Combine(assetDir, file), snd.Data);
                        sndUnique++;
                    }

                    soundsDoc[kv.Key.ToString()] = new Dictionary<string, object?>
                    {
                        ["file"] = Path.GetRelativePath(manifestDir, Path.Combine(assetDir, file)),
                        ["name"] = snd.Name,
                    };
                }
                catch { sndFail++; }
            }

            File.WriteAllText(Path.Combine(manifestDir, "sounds.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["unique"] = sndUnique,
                ["duplicates"] = sndDup,
                ["failed"] = sndFail,
                ["sounds"] = soundsDoc,
            }, JsonOptions));

            return new AssetExportResult
            {
                Images = imgUnique,
                ImageDuplicates = imgDup,
                Sounds = sndUnique,
                SoundDuplicates = sndDup,
            };
        }

        static string GetSoundExtension(byte[] data)
        {
            if (data.Length >= 4)
            {
                if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') return ".wav";
                if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S') return ".ogg";
                if (data[0] == 'F' && data[1] == 'O' && data[2] == 'R' && data[3] == 'M') return ".aiff";
            }
            if (data.Length >= 3 && data[0] == 'I' && data[1] == 'D' && data[2] == '3') return ".mp3";
            if (data.Length >= 2 && data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return ".mp3";
            return ".wav";
        }

        // ---------------- tiles (backdrop) ----------------

        /// <summary>
        /// Exports every frame's backdrop instances (QuickBackdrop type 0 and Backdrop type 1) as a
        /// faithful raw-rect list: one entry per placed instance with its original position/size,
        /// obstacle flag, image handle and the instance order (index in the frame's instance list).
        /// Image/name are taken from the <b>frame-local</b> object definition (same object name on
        /// another frame can be a different graphic). No splitting or tile classification is
        /// performed here — that is the generator's job.
        /// Output: tiles.json (one per level).
        /// </summary>
        public static TileExportResult ExportTiles(MFAPackageData mfa, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            int tileCount = 0;
            var framesDoc = new List<object?>();

            foreach (var frm in mfa.Frames)
            {
                var tiles = new List<object?>();
                var sourceObjects = SourceObjectsByHandle(frm);
                int order = 0;
                foreach (var inst in frm.FrameInstances.Instances)
                {
                    if (!TryGetFrameObject(mfa, frm, inst.ObjectInfo, out var oi) || oi == null)
                    {
                        order++;
                        continue;
                    }

                    uint obstacle = 0, image = 0;
                    int w = 0, h = 0;
                    int fillType = 3; // 1=Solid, 2=Gradient, 3=Motif(pattern)
                    int color1 = 0, color2 = 0;
                    bool verticalGradient = true;
                    bool isBackground = false;

                    switch (oi.Properties)
                    {
                        case ObjectQuickBackdrop qb:
                            obstacle = qb.ObstacleType;
                            w = qb.Width;
                            h = qb.Height;
                            image = qb.Shape.Image;
                            fillType = qb.Shape.FillType;
                            verticalGradient = qb.Shape.VerticalGradient;
                            if (fillType is 1 or 2)
                            {
                                color1 = qb.Shape.Color1.ToArgb();
                                color2 = qb.Shape.Color2.ToArgb();
                                image = 0;
                            }
                            // image==0 QuickBackdrop = a full-frame backdrop (single big picture or
                            // solid/gradient fill). Mark it as a background that must NOT be split
                            // into tiles; the generator places it once as a background sprite/layer.
                            if (image == 0)
                                isBackground = true;
                            break;
                        case ObjectBackdrop bd:
                            obstacle = bd.ObstacleType;
                            w = bd.Width;
                            h = bd.Height;
                            image = bd.Image;
                            // A Backdrop whose image handle is 0 is also a plain/full backdrop.
                            if (image == 0)
                                isBackground = true;
                            break;
                        default:
                            order++;
                            continue;
                    }

                    // Backdrops parsed from MFA carry no Width/Height; fall back to image size.
                    if (w <= 0 || h <= 0)
                    {
                        if (mfa.ImageBank.Images.TryGetValue(image, out var img))
                        {
                            if (w <= 0) w = img.Width;
                            if (h <= 0) h = img.Height;
                        }
                    }

                    // Any backdrop whose size exceeds 480px on either axis is treated as a background
                    // (a full-frame image / large backdrop), not as a tile. It must NOT be split.
                    if (w > 480 || h > 480)
                        isBackground = true;

                    var tile = new Dictionary<string, object?>
                    {
                        ["x"] = inst.PositionX,
                        ["y"] = inst.PositionY,
                        ["w"] = w,
                        ["h"] = h,
                        ["obstacle"] = obstacle,
                        ["image"] = image,
                        ["fillType"] = fillType,
                        ["color1"] = color1,
                        ["color2"] = color2,
                        ["verticalGradient"] = verticalGradient,
                        ["isBackground"] = isBackground,
                        ["object"] = oi.Name,
                        ["objectInfo"] = inst.ObjectInfo,
                        ["objectType"] = oi.Header.Type,
                        ["layer"] = inst.Layer,
                        ["order"] = order,
                    };
                    if (!sourceObjects.TryGetValue((int)inst.ObjectInfo, out MFAObjectInfo? source))
                        source = FindSourceObject(mfa, oi);
                    foreach (var ink in InkOf(oi, source))
                        tile[ink.Key] = ink.Value;
                    if (fillType is 1 or 2 && oi.Properties is ObjectQuickBackdrop qbColors)
                    {
                        tile["color1Rgb"] = Rgb(qbColors.Shape.Color1);
                        tile["color2Rgb"] = Rgb(qbColors.Shape.Color2);
                    }
                    tiles.Add(tile);
                    tileCount++;
                    order++;
                }

                framesDoc.Add(new Dictionary<string, object?>
                {
                    ["name"] = frm.FrameName,
                    ["handle"] = frm.Handle,
                    ["width"] = frm.FrameHeader.Width,
                    ["height"] = frm.FrameHeader.Height,
                    ["tiles"] = tiles,
                });
            }

            File.WriteAllText(Path.Combine(outputDir, "tiles.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["app"] = mfa.AppName,
                ["frames"] = framesDoc,
            }, JsonOptions));

            return new TileExportResult { Frames = mfa.Frames.Count, Tiles = tileCount };
        }

        // ---------------- layers (scroll coefficients) ----------------

        /// <summary>
        /// Exports every frame's layer list: layer name, the CTF scroll coefficients
        /// (XCoefficient / YCoefficient = how much a layer moves relative to the camera) and the
        /// layer flags (wrap horizontally / vertically, hidden at start, ...). This is the data the
        /// Godot side needs to rebuild the CTF parallax background stack: each CTF layer maps to one
        /// parallax layer whose scroll scale = (XCoefficient, YCoefficient).
        /// Output: layers.json (one per level).
        /// </summary>
        public static LayerExportResult ExportLayers(MFAPackageData mfa, string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            int layerCount = 0;
            var framesDoc = new List<object?>();

            foreach (var frm in mfa.Frames)
            {
                var layers = new List<object?>();
                for (int i = 0; i < frm.FrameLayers.Layers.Length; i++)
                {
                    var layer = frm.FrameLayers.Layers[i];
                    var flags = layer.MFALayerFlags;
                    layers.Add(new Dictionary<string, object?>
                    {
                        ["index"] = i,
                        ["name"] = layer.Name,
                        ["xCoefficient"] = layer.XCoefficient,
                        ["yCoefficient"] = layer.YCoefficient,
                        ["backdropCount"] = layer.BackdropCount,
                        ["backdropIndex"] = layer.BackdropIndex,
                        ["flags"] = new Dictionary<string, object?>
                        {
                            ["visible"] = flags["Visible"],
                            ["locked"] = flags["Locked"],
                            ["hiddenAtStart"] = flags["HiddenAtStart"],
                            ["dontSaveBackground"] = flags["DontSaveBackground"],
                            ["wrapHorizontally"] = flags["WrapHorizontally"],
                            ["wrapVertically"] = flags["WrapVertically"],
                            ["prevEffect"] = flags["PrevEffect"],
                            ["raw"] = flags.Value,
                        },
                        ["layerFlagsRaw"] = layer.LayerFlags.Value,
                        ["effect"] = LayerEffectOf(layer.Effect),
                    });
                    layerCount++;
                }

                var frameFx = frm.FrameEffects;
                framesDoc.Add(new Dictionary<string, object?>
                {
                    ["name"] = frm.FrameName,
                    ["handle"] = frm.Handle,
                    ["width"] = frm.FrameHeader.Width,
                    ["height"] = frm.FrameHeader.Height,
                    ["frameEffect"] = frameFx != null
                        ? new Dictionary<string, object?>
                        {
                            ["inkEffect"] = frameFx.InkEffect,
                            ["inkEffectName"] = InkEffectName(frameFx.InkEffect),
                            ["blendCoeff"] = frameFx.BlendCoeff,
                            ["rgbCoeff"] = Rgb(frameFx.RGBCoeff),
                        }
                        : null,
                    ["layers"] = layers,
                });
            }

            File.WriteAllText(Path.Combine(outputDir, "layers.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["app"] = mfa.AppName,
                ["frames"] = framesDoc,
            }, JsonOptions));

            return new LayerExportResult { Frames = mfa.Frames.Count, Layers = layerCount };
        }

        // ---------------- spritesheets ----------------

        /// <summary>
        /// Exports each Active object's animation directions as sprite sheets: one PNG per direction.
        /// Frame <em>order</em> is never re-sorted (a "left 2 / right 2" animation keeps its natural
        /// sequence). Multi-frame directions are:
        /// <list type="bullet">
        /// <item>Aligned onto a shared cell so every frame's hotspot sits at the same pixel — Godot
        /// SpriteFrames has no per-frame hotspot, so the alignment is baked into the PNG (the first
        /// frame is padded too when later frames extend past it).</item>
        /// <item>Packed into a 2D atlas when a single row would be much wider than it is tall, wrapping
        /// whole frames onto new rows. A frame is never split across rows. Sheets already &lt; 256px
        /// wide, or layouts that would not shrink (or would grow) the atlas, stay as a single row.</item>
        /// </list>
        /// Each frame's (now cell-relative) hotspot / action point is recorded in sheet.json for
        /// Sprite2D.offset mapping. Identical objects across levels share one directory
        /// (content-fingerprint dedup); same-named objects with different content get a hash suffix.
        /// </summary>
        public static SpritesheetResult ExportSpritesheets(
            MFAPackageData mfa,
            string spritesDir,
            Dictionary<string, string>? sheetIndex = null)
        {
            Directory.CreateDirectory(spritesDir);
            sheetIndex ??= new Dictionary<string, string>(); // key -> fingerprint

            int objects = 0, sheets = 0, skipped = 0;

            // Walk every frame's own object list. A name like "Backdrop 26" / "Now Playing"
            // can refer to a different Active on another frame; the global FrameItems map
            // only keeps the first handle and would drop the rest.
            IEnumerable<ObjectInfo> AllFrameObjects()
            {
                bool anyLocal = false;
                foreach (var frm in mfa.Frames)
                {
                    if (frm.FrameObjectItems.Count == 0) continue;
                    anyLocal = true;
                    foreach (var oi in frm.FrameObjectItems.Values)
                        yield return oi;
                }
                if (!anyLocal)
                {
                    foreach (var oi in mfa.FrameItems.Items.Values)
                        yield return oi;
                }
            }

            foreach (var oi in AllFrameObjects())
            {
                if (oi.Header.Type != 2) continue; // Active only
                if (oi.Properties is not ObjectCommon oc) continue;
                if (oc.ObjectAnimations.Animations.Count == 0) continue;

                objects++;
                var dirs = CollectDirections(oc, mfa.ImageBank);
                if (dirs.Count == 0) continue;

                string fp = FingerprintSheet(dirs);
                string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(oi.Name) ? $"obj_{oi.Header.Handle}" : oi.Name);

                string key = safeName;
                if (sheetIndex.TryGetValue(key, out var existing) && existing != fp)
                    key = safeName + "__" + fp[..8];

                if (sheetIndex.ContainsKey(key))
                {
                    skipped++;
                    continue;
                }

                sheetIndex[key] = fp;
                WriteSheet(Path.Combine(spritesDir, key), oi.Name, dirs);
                sheets++;
            }

            return new SpritesheetResult { Objects = objects, Sheets = sheets, Skipped = skipped };
        }

        /// <summary>Writes sprites/index.json mapping each object name to its sheet directory name(s).</summary>
        public static void WriteSpriteIndex(string spritesDir)
        {
            if (!Directory.Exists(spritesDir)) return;
            var index = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Directory.GetDirectories(spritesDir))
            {
                var jsonPath = Path.Combine(dir, "sheet.json");
                if (!File.Exists(jsonPath)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    var name = doc.RootElement.GetProperty("object").GetString() ?? Path.GetFileName(dir);
                    if (!index.TryGetValue(name, out var list)) index[name] = list = new List<string>();
                    var dirName = Path.GetFileName(dir);
                    if (!list.Contains(dirName)) list.Add(dirName);
                }
                catch { }
            }
            File.WriteAllText(Path.Combine(spritesDir, "index.json"), JsonSerializer.Serialize(index, JsonOptions));
        }

        /// <summary>Collects every animation direction with its frames decoded (per-handle decode cache).</summary>
        static List<DirData> CollectDirections(ObjectCommon oc, ImageBank imageBank)
        {
            var cache = new Dictionary<uint, DirFrame>();

            DirFrame? GetFrame(uint handle)
            {
                if (cache.TryGetValue(handle, out var cached)) return cached;
                if (!imageBank.Images.TryGetValue(handle, out var img)) return null;
                try
                {
                    if (!MfaImageEncoder.TryDecode(img, out var decoded)) return null;
                    var f = new DirFrame
                    {
                        Handle = handle,
                        Bgra = decoded.Bgra,
                        Hash = Convert.ToHexString(SHA256.HashData(decoded.Bgra))[..16].ToLowerInvariant(),
                        Width = decoded.Width,
                        Height = decoded.Height,
                        HotspotX = img.HotspotX,
                        HotspotY = img.HotspotY,
                        ActionPointX = img.ActionPointX,
                        ActionPointY = img.ActionPointY,
                    };
                    cache[handle] = f;
                    return f;
                }
                catch { return null; }
            }

            var dirs = new List<DirData>();
            foreach (var anim in oc.ObjectAnimations.Animations.OrderBy(a => a.Key))
                foreach (var dir in anim.Value.Directions)
                {
                    var dd = new DirData
                    {
                        AnimIndex = anim.Key,
                        AnimName = anim.Value.Name,
                        DirIndex = dir.Index,
                        MinSpeed = dir.MinimumSpeed,
                        MaxSpeed = dir.MaximumSpeed,
                        Repeat = dir.Repeat,
                        RepeatFrame = dir.RepeatFrame,
                    };
                    foreach (uint handle in dir.Frames)
                    {
                        var f = GetFrame(handle);
                        if (f != null) dd.Frames.Add(f);
                    }
                    dirs.Add(dd);
                }
            return dirs;
        }

        static string FingerprintSheet(List<DirData> dirs)
        {
            var sb = new StringBuilder();
            foreach (var dd in dirs)
            {
                sb.Append('|').Append(dd.AnimIndex).Append(':').Append(dd.AnimName)
                  .Append('[').Append(dd.DirIndex).Append(',').Append(dd.MinSpeed).Append(',')
                  .Append(dd.MaxSpeed).Append(',').Append(dd.Repeat).Append(',').Append(dd.RepeatFrame).Append(']');
                foreach (var f in dd.Frames)
                    sb.Append(',').Append(f.Hash).Append(':').Append(f.HotspotX).Append(':').Append(f.HotspotY);
            }
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
        }

        static void WriteSheet(string sheetDir, string objectName, List<DirData> dirs)
        {
            Directory.CreateDirectory(sheetDir);

            var dirsDoc = new List<object?>();
            foreach (var dd in dirs)
            {
                if (dd.Frames.Count == 0) continue;

                AlignFramesToHotspot(dd.Frames, out int cellW, out int cellH, out int hotX, out int hotY, out var blit);
                var layout = PackFrameCells(dd.Frames.Count, cellW, cellH);
                string sheetName = $"a{dd.AnimIndex}_d{dd.DirIndex}.png";

                var framesDoc = new List<object?>();
                var atlas = new byte[layout.SheetW * layout.SheetH * 4];
                for (int i = 0; i < dd.Frames.Count; i++)
                {
                    var f = dd.Frames[i];
                    int cx = layout.Positions[i].X;
                    int cy = layout.Positions[i].Y;
                    MfaImageEncoder.Blit(f.Bgra, f.Width, f.Height, atlas, layout.SheetW, layout.SheetH,
                        cx + blit[i].X, cy + blit[i].Y);
                    framesDoc.Add(new Dictionary<string, object?>
                    {
                        ["handle"] = f.Handle,
                        ["rect"] = new Dictionary<string, object?> { ["x"] = cx, ["y"] = cy, ["w"] = cellW, ["h"] = cellH },
                        ["hotspotX"] = hotX,
                        ["hotspotY"] = hotY,
                        ["actionPointX"] = f.ActionPointX + blit[i].X,
                        ["actionPointY"] = f.ActionPointY + blit[i].Y,
                    });
                }
                File.WriteAllBytes(Path.Combine(sheetDir, sheetName),
                    MfaImageEncoder.ToPng(new MfaImageEncoder.Decoded { Bgra = atlas, Width = layout.SheetW, Height = layout.SheetH }));

                dirsDoc.Add(new Dictionary<string, object?>
                {
                    ["animation"] = dd.AnimName,
                    ["animationIndex"] = dd.AnimIndex,
                    ["directionIndex"] = dd.DirIndex,
                    ["minSpeed"] = dd.MinSpeed,
                    ["maxSpeed"] = dd.MaxSpeed,
                    ["repeat"] = dd.Repeat,
                    ["repeatFrame"] = dd.RepeatFrame,
                    ["sheet"] = sheetName,
                    ["sheetWidth"] = layout.SheetW,
                    ["sheetHeight"] = layout.SheetH,
                    ["frames"] = framesDoc,
                });
            }

            File.WriteAllText(Path.Combine(sheetDir, "sheet.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["object"] = objectName,
                ["directions"] = dirsDoc,
            }, JsonOptions));
        }

        /// <summary>
        /// Pads every frame onto a shared cell so hotspots coincide. Deltas are taken from the first
        /// frame's hotspot; if a later frame extends further left/up, the whole group (including
        /// frame 0) is shifted so every blit is in-bounds. The cell hotspot is therefore
        /// <c>(max hotspotX, max hotspotY)</c> across the direction.
        /// </summary>
        static void AlignFramesToHotspot(
            List<DirFrame> frames,
            out int cellW, out int cellH,
            out int hotX, out int hotY,
            out Point[] blit)
        {
            int n = frames.Count;
            blit = new Point[n];
            hotX = frames[0].HotspotX;
            hotY = frames[0].HotspotY;
            int maxRight = frames[0].Width - frames[0].HotspotX;
            int maxBottom = frames[0].Height - frames[0].HotspotY;
            for (int i = 1; i < n; i++)
            {
                var f = frames[i];
                if (f.HotspotX > hotX) hotX = f.HotspotX;
                if (f.HotspotY > hotY) hotY = f.HotspotY;
                int right = f.Width - f.HotspotX;
                int bottom = f.Height - f.HotspotY;
                if (right > maxRight) maxRight = right;
                if (bottom > maxBottom) maxBottom = bottom;
            }

            cellW = Math.Max(1, hotX + maxRight);
            cellH = Math.Max(1, hotY + maxBottom);
            for (int i = 0; i < n; i++)
                blit[i] = new Point(hotX - frames[i].HotspotX, hotY - frames[i].HotspotY);
        }

        // Don't wrap a strip that is already narrower than this — wrapping 32px walk-cycles
        // into a column does not help, and Godot atlas regions stay trivial to inspect.
        const int MinWidthForWrap = 256;

        sealed class SheetLayout
        {
            public int SheetW;
            public int SheetH;
            public Point[] Positions = Array.Empty<Point>();
        }

        /// <summary>
        /// Packs <paramref name="count"/> equal cells of <paramref name="cellW"/>×<paramref name="cellH"/>
        /// into an atlas. Default is a single row (original order, left-to-right). When that row is at
        /// least <see cref="MinWidthForWrap"/> px wide, whole cells may wrap onto further rows — mixed
        /// row/column placement is allowed whenever it strictly shrinks <c>max(width,height)</c>. A
        /// cell is never split across rows (Godot needs a contiguous AtlasTexture region per frame).
        /// Layouts that would not change that max, or would increase it, keep the single row.
        /// </summary>
        static SheetLayout PackFrameCells(int count, int cellW, int cellH)
        {
            var linear = GridLayout(count, count, cellW, cellH);
            if (count <= 1 || linear.SheetW < MinWidthForWrap)
                return linear;

            int linearMax = Math.Max(linear.SheetW, linear.SheetH);
            int bestCols = count;
            int bestMax = linearMax;
            long bestArea = (long)linear.SheetW * linear.SheetH;
            int bestDiff = Math.Abs(linear.SheetW - linear.SheetH);
            int bestH = linear.SheetH;

            for (int cols = 1; cols < count; cols++)
            {
                int rows = (count + cols - 1) / cols;
                int w = cols * cellW;
                int h = rows * cellH;
                int mx = Math.Max(w, h);
                if (mx >= linearMax) continue; // not smaller, or would grow

                long area = (long)w * h;
                int diff = Math.Abs(w - h);
                bool better = mx < bestMax
                    || (mx == bestMax && area < bestArea)
                    || (mx == bestMax && area == bestArea && diff < bestDiff)
                    || (mx == bestMax && area == bestArea && diff == bestDiff && h < bestH);
                if (!better) continue;

                bestCols = cols;
                bestMax = mx;
                bestArea = area;
                bestDiff = diff;
                bestH = h;
            }

            return bestCols == count ? linear : GridLayout(count, bestCols, cellW, cellH);
        }

        static SheetLayout GridLayout(int count, int cols, int cellW, int cellH)
        {
            if (count < 1) count = 1;
            if (cols < 1) cols = 1;
            int rows = (count + cols - 1) / cols;
            var pos = new Point[count];
            for (int i = 0; i < count; i++)
                pos[i] = new Point((i % cols) * cellW, (i / cols) * cellH);
            return new SheetLayout
            {
                SheetW = Math.Max(1, cols * cellW),
                SheetH = Math.Max(1, rows * cellH),
                Positions = pos,
            };
        }

        static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "_" : s;
        }

        sealed class DirFrame
        {
            public uint Handle;
            public byte[] Bgra = Array.Empty<byte>();
            public string Hash = "";
            public int Width;
            public int Height;
            public short HotspotX;
            public short HotspotY;
            public short ActionPointX;
            public short ActionPointY;
        }

        sealed class DirData
        {
            public int AnimIndex;
            public string AnimName = "";
            public int DirIndex;
            public int MinSpeed;
            public int MaxSpeed;
            public int Repeat;
            public int RepeatFrame;
            public List<DirFrame> Frames = new();
        }

        // ---------------- objects.json ----------------

        static Dictionary<string, object?> BuildObjectsDoc(MFAPackageData mfa)
        {
            // Compatibility-only global map. MFA object handles are frame-local and can
            // describe entirely different objects on different frames.
            var objectTypes = new Dictionary<string, object?>();
            foreach (var kv in mfa.FrameItems.Items)
                objectTypes[kv.Key.ToString()] = BuildTypeDef(kv.Value, FindSourceObject(mfa, kv.Value));

            var frames = new List<object?>();
            foreach (var frm in mfa.Frames)
                frames.Add(BuildFrame(mfa, frm));

            return new Dictionary<string, object?>
            {
                ["app"] = new Dictionary<string, object?>
                {
                    ["name"] = mfa.AppName,
                    ["build"] = mfa.ProductBuild,
                    ["width"] = mfa.AppHeader.AppWidth,
                    ["height"] = mfa.AppHeader.AppHeight,
                },
                ["objectTypes"] = objectTypes,
                ["objectTypesScope"] = "global-first-seen",
                ["objectTypesUsage"] = "legacy-compatibility-only; use frames[].objectTypes or embedded instance metadata",
                ["objectTypesAuthoritative"] = false,
                ["frameObjectTypesAuthoritative"] = true,
                ["frames"] = frames,
            };
        }

        static Dictionary<string, object?> BuildTypeDef(ObjectInfo oi, MFAObjectInfo? source = null)
        {
            var d = new Dictionary<string, object?>
            {
                ["handle"] = oi.Header.Handle,
                ["name"] = oi.Name,
                ["type"] = oi.Header.Type,
                ["typeName"] = TypeName(oi.Header.Type),
                ["objectFlags"] = oi.Header.ObjectFlags.Value,
            };
            if (oi.Header.Type >= 32
                && NebulaCore.PackageData?.Extensions?.Exts != null
                && NebulaCore.PackageData.Extensions.Exts.TryGetValue(oi.Header.Type - 32, out var ext)
                && ext != null)
            {
                d["extension"] = ext.FileName;
                d["extensionName"] = ext.Name;
            }

            try
            {
                switch (oi.Properties)
                {
                    case ObjectQuickBackdrop qb:
                        d["obstacleType"] = qb.ObstacleType;
                        d["collisionWithBox"] = qb.CollisionType != 0;
                        d["width"] = qb.Width;
                        d["height"] = qb.Height;
                        d["shape"] = qb.Shape.ShapeType;
                        d["shapeName"] = ShapeName(qb.Shape.ShapeType);
                        d["fillType"] = qb.Shape.FillType;
                        d["fillTypeName"] = FillTypeName(qb.Shape.FillType);
                        d["borderSize"] = qb.Shape.BorderSize;
                        d["borderColor"] = Rgb(qb.Shape.BorderColor);
                        d["color1"] = Rgb(qb.Shape.Color1);
                        d["color2"] = Rgb(qb.Shape.Color2);
                        d["verticalGradient"] = qb.Shape.VerticalGradient;
                        d["image"] = qb.Shape.Image;
                        d["lineFlags"] = new Dictionary<string, object?>
                        {
                            ["flipX"] = qb.Shape.LineFlags["FlipX"],
                            ["flipY"] = qb.Shape.LineFlags["FlipY"],
                        };
                        break;
                    case ObjectBackdrop bd:
                        d["obstacleType"] = bd.ObstacleType;
                        d["collisionWithBox"] = bd.CollisionType != 0;
                        d["image"] = bd.Image;
                        break;
                    case ObjectCommon oc:
                        d["createAtStart"] = CreateAtStartOf(oc);
                        d["followFrame"] = !oc.ObjectFlags["DontFollowFrame"];
                        d["fineDetection"] = !oc.NewObjectFlags["DontUseFineDetection"];
                        d["newObjectFlags"] = oc.NewObjectFlags.Value;
                        d["qualifiers"] = oc.Qualifiers;
                        d["alterableValues"] = new Dictionary<string, object?>
                        {
                            ["names"] = oc.ObjectAlterableValues.Names,
                            ["initial"] = oc.ObjectAlterableValues.AlterableValues,
                        };
                        d["alterableStrings"] = new Dictionary<string, object?>
                        {
                            ["names"] = oc.ObjectAlterableStrings.Names,
                            ["initial"] = oc.ObjectAlterableStrings.AlterableStrings,
                        };
                        d["value"] = new Dictionary<string, object?>
                        {
                            ["initial"] = oc.ObjectValue.Initial,
                            ["minimum"] = oc.ObjectValue.Minimum,
                            ["maximum"] = oc.ObjectValue.Maximum,
                        };
                        d["movements"] = oc.ObjectMovements.Movements.Select(m => (object)BuildMovement(m)).ToList();
                        d["transitionIn"] = BuildTransition(oc.ObjectTransitionIn);
                        d["transitionOut"] = BuildTransition(oc.ObjectTransitionOut);
                        d["animations"] = oc.ObjectAnimations.Animations
                            .OrderBy(a => a.Key)
                            .Select(a => (object)new Dictionary<string, object?>
                            {
                                ["id"] = a.Key,
                                ["name"] = a.Value.Name,
                                ["directions"] = a.Value.Directions.Select(dir => (object)new Dictionary<string, object?>
                                {
                                    ["index"] = dir.Index,
                                    ["minSpeed"] = dir.MinimumSpeed,
                                    ["maxSpeed"] = dir.MaximumSpeed,
                                    ["repeat"] = dir.Repeat,
                                    ["repeatFrame"] = dir.RepeatFrame,
                                    ["frames"] = dir.Frames.Select(f => (object)f).ToList(),
                                }).ToList(),
                            }).ToList();
                        break;
                }
            }
            catch { /* best-effort per object type */ }

            foreach (var ink in InkOf(oi, source))
                d[ink.Key] = ink.Value;

            return d;
        }

        static Dictionary<string, object?> BuildFrame(MFAPackageData mfa, Frame frm)
        {
            var sourceObjects = SourceObjectsByHandle(frm);
            var frameTypes = new Dictionary<string, object?>();
            foreach (var kv in frm.FrameObjectItems)
            {
                sourceObjects.TryGetValue(kv.Key, out MFAObjectInfo? source);
                frameTypes[kv.Key.ToString()] = BuildTypeDef(kv.Value, source);
            }

            var instances = new List<object?>();
            foreach (var inst in frm.FrameInstances.Instances)
            {
                var instance = new Dictionary<string, object?>
                {
                    ["objectInfo"] = inst.ObjectInfo,
                    ["objectTypeRef"] = $"{frm.Handle}:{inst.ObjectInfo}",
                    ["x"] = inst.PositionX,
                    ["y"] = inst.PositionY,
                    ["layer"] = inst.Layer,
                    ["instanceValue"] = inst.InstanceValue,
                    ["parentType"] = inst.ParentType,
                    ["parentHandle"] = inst.ParentHandle,
                };

                if (TryGetFrameObject(mfa, frm, inst.ObjectInfo, out ObjectInfo? oi) && oi != null)
                {
                    if (!sourceObjects.TryGetValue((int)inst.ObjectInfo, out MFAObjectInfo? source))
                        source = FindSourceObject(mfa, oi);

                    string objectKey = inst.ObjectInfo.ToString();
                    if (!frameTypes.ContainsKey(objectKey))
                        frameTypes[objectKey] = BuildTypeDef(oi, source);

                    instance["objectName"] = oi.Name;
                    instance["objectType"] = oi.Header.Type;
                    instance["objectTypeName"] = TypeName(oi.Header.Type);
                    if (oi.Properties is ObjectCommon oc)
                    {
                        instance["createAtStart"] = CreateAtStartOf(oc);
                        instance["followFrame"] = !oc.ObjectFlags["DontFollowFrame"];
                        instance["fineDetection"] = !oc.NewObjectFlags["DontUseFineDetection"];
                    }
                    else if (oi.Properties is ObjectQuickBackdrop qb)
                        instance["collisionWithBox"] = qb.CollisionType != 0;
                    else if (oi.Properties is ObjectBackdrop bd)
                        instance["collisionWithBox"] = bd.CollisionType != 0;
                    foreach (var ink in InkOf(oi, source))
                        instance[ink.Key] = ink.Value;
                }

                instances.Add(instance);
            }

            return new Dictionary<string, object?>
            {
                ["name"] = frm.FrameName,
                ["handle"] = frm.Handle,
                ["width"] = frm.FrameHeader.Width,
                ["height"] = frm.FrameHeader.Height,
                ["objectTypes"] = frameTypes,
                ["objectTypesScope"] = "frame-authoritative-with-global-fallback",
                ["instances"] = instances,
            };
        }

        static Dictionary<int, MFAObjectInfo> SourceObjectsByHandle(Frame frame)
        {
            var result = new Dictionary<int, MFAObjectInfo>();
            foreach (MFAObjectInfo source in frame.MFAFrameInfo.Objects)
                result[source.Handle] = source;
            return result;
        }

        static MFAObjectInfo? FindSourceObject(MFAPackageData mfa, ObjectInfo oi)
        {
            foreach (Frame frame in mfa.Frames)
            {
                if (!frame.FrameObjectItems.TryGetValue(oi.Header.Handle, out ObjectInfo? local)
                    || !ReferenceEquals(local, oi))
                    continue;

                foreach (MFAObjectInfo source in frame.MFAFrameInfo.Objects)
                    if (source.Handle == oi.Header.Handle)
                        return source;
            }
            return null;
        }

        /// <summary>
        /// Resolves an instance's object type from the frame it sits on. MFA object
        /// definitions are per-frame; the app-global FrameItems map is only a fallback.
        /// </summary>
        static bool TryGetFrameObject(MFAPackageData mfa, Frame frm, uint handle, out ObjectInfo? oi)
        {
            if (frm.FrameObjectItems.TryGetValue((int)handle, out oi) && oi != null)
                return true;
            return mfa.FrameItems.Items.TryGetValue((int)handle, out oi);
        }

        // ---------------- events.json ----------------

        static Dictionary<string, object?> BuildEventsDoc(MFAPackageData mfa)
        {
            var globalEvents = new List<object?>();
            foreach (var evt in mfa.GlobalEvents.Events)
                globalEvents.Add(BuildEvent(evt));

            var fpMap = new Dictionary<string, List<string>>();
            var fpOrder = new List<string>();

            void IndexEvent(Event evt, string occurrence)
            {
                string fp = FingerprintEvent(evt);
                if (!fpMap.ContainsKey(fp))
                {
                    fpMap[fp] = new List<string>();
                    fpOrder.Add(fp);
                }
                fpMap[fp].Add(occurrence);
            }

            foreach (var evt in mfa.GlobalEvents.Events)
                IndexEvent(evt, "<global>");

            var frames = new List<object?>();
            foreach (var frm in mfa.Frames)
            {
                var evts = new List<object?>();
                for (int i = 0; i < frm.FrameEvents.Events.Count; i++)
                {
                    var evt = frm.FrameEvents.Events[i];
                    evts.Add(BuildEvent(evt));
                    IndexEvent(evt, $"{frm.FrameName}#{i}");
                }

                var fe = frm.FrameEvents;
                frames.Add(new Dictionary<string, object?>
                {
                    ["name"] = frm.FrameName,
                    ["handle"] = frm.Handle,
                    ["events"] = evts,
                    ["eventObjects"] = fe.EventObjects.Values
                        .OrderBy(o => o.Handle)
                        .Select(o => (object)new Dictionary<string, object?>
                        {
                            ["handle"] = o.Handle,
                            ["name"] = o.Name,
                            ["objectType"] = o.ObjectType,
                            ["itemType"] = o.ItemType,
                            ["itemHandle"] = o.ItemHandle,
                            ["instanceHandle"] = o.InstanceHandle,
                            ["systemQualifier"] = o.SystemQualifier,
                        }).ToList(),
                    ["qualifiers"] = fe.Qualifiers
                        .Select(q => (object)new Dictionary<string, object?>
                        {
                            ["objectInfo"] = q.ObjectInfo,
                            ["type"] = q.Type,
                        }).ToList(),
                    ["eventGroups"] = fe.EventGroups
                        .Select(g => (object)new Dictionary<string, object?>
                        {
                            ["handle"] = g.Handle,
                            ["name"] = g.Name,
                            ["uuid"] = g.UUID,
                        }).ToList(),
                    ["comments"] = fe.Comments
                        .Select(c => (object)new Dictionary<string, object?>
                        {
                            ["handle"] = c.Handle,
                            ["value"] = c.Value,
                        }).ToList(),
                });
            }

            var dedup = new List<object?>();
            foreach (var fp in fpOrder)
            {
                var occ = fpMap[fp];
                dedup.Add(new Dictionary<string, object?>
                {
                    ["fingerprint"] = fp,
                    ["count"] = occ.Count,
                    ["frames"] = occ,
                });
            }

            return new Dictionary<string, object?>
            {
                ["globalEvents"] = globalEvents,
                ["globalEventComments"] = mfa.GlobalEvents.Comments
                    .Select(c => (object)new Dictionary<string, object?>
                    {
                        ["handle"] = c.Handle,
                        ["value"] = c.Value,
                    }).ToList(),
                ["frames"] = frames,
                ["dedup"] = dedup,
            };
        }

        static Dictionary<string, object?> BuildEvent(Event evt)
        {
            var d = new Dictionary<string, object?>
            {
                ["conditions"] = evt.Conditions.Select(c => BuildCondition(c)).ToList(),
                ["actions"] = evt.Actions.Select(a => BuildAction(a)).ToList(),
                ["flags"] = evt.EventFlags.Value,
                ["flagsDecoded"] = new Dictionary<string, object?>
                {
                    ["once"] = evt.EventFlags["Once"],
                    ["notAlways"] = evt.EventFlags["NotAlways"],
                    ["repeat"] = evt.EventFlags["Repeat"],
                    ["noMore"] = evt.EventFlags["NoMore"],
                    ["shuffle"] = evt.EventFlags["Shuffle"],
                    ["hasChildren"] = evt.EventFlags["HasChildren"],
                    ["break"] = evt.EventFlags["Break"],
                    ["grouped"] = evt.EventFlags["Grouped"],
                    ["inactive"] = evt.EventFlags["Inactive"],
                    ["hasParent"] = evt.EventFlags["HasParent"],
                    ["hasOr"] = evt.EventFlags["HasOr"],
                    ["hasStop"] = evt.EventFlags["HasStop"],
                    ["hasOrLogical"] = evt.EventFlags["HasOrLogical"],
                },
                ["restricted"] = evt.Restricted,
                ["restrictCpt"] = evt.RestrictCpt,
                ["identifier"] = evt.Identifier,
            };

            if (evt.Conditions.Count > 0)
            {
                Condition c0 = evt.Conditions[0];
                if (c0.ObjectType == -1 && c0.Num == -10 && c0.Parameters.Length > 0
                    && c0.Parameters[0].Data is ParameterGroup pg)
                {
                    d["kind"] = "group";
                    d["group"] = new Dictionary<string, object?>
                    {
                        ["id"] = pg.ID,
                        ["name"] = pg.Name,
                        ["inactiveOnStart"] = pg.GroupFlags["InactiveOnStart"],
                        ["closed"] = pg.GroupFlags["Closed"],
                    };
                }
                else if (c0.ObjectType == -1 && c0.Num == -9 && c0.Parameters.Length > 0
                    && c0.Parameters[0].Data is ParameterRemark remark)
                {
                    d["kind"] = "comment";
                    d["comment"] = BuildRemark(remark);
                    d["conditions"] = new List<object?>();
                    d["actions"] = new List<object?>();
                }
            }

            return d;
        }

        static Dictionary<string, object?> BuildCondition(Condition c)
        {
            string text = SafeText(c, out string? textErr);
            var d = new Dictionary<string, object?>
            {
                ["objectType"] = c.ObjectType,
                ["num"] = c.Num,
                ["objectInfo"] = c.ObjectInfo,
                ["negated"] = c.OtherFlags["Negated"],
                ["object"] = SafeObjectName(c),
                ["text"] = text,
                ["params"] = c.Parameters.Select(p => BuildParam(p)).ToList(),
                ["identifier"] = c.Identifier,
            };
            if (textErr != null)
                d["error"] = textErr;
            TryAddItemHandle(d, c);
            return d;
        }

        static Dictionary<string, object?> BuildAction(Action a)
        {
            string text = SafeText(a, out string? textErr);
            var d = new Dictionary<string, object?>
            {
                ["objectType"] = a.ObjectType,
                ["num"] = a.Num,
                ["objectInfo"] = a.ObjectInfo,
                ["object"] = SafeObjectName(a),
                ["text"] = text,
                ["params"] = a.Parameters.Select(p => BuildParam(p)).ToList(),
            };
            if (textErr != null)
                d["error"] = textErr;
            TryAddItemHandle(d, a);
            return d;
        }

        static Dictionary<string, object?> BuildParam(Parameter p)
        {
            var d = new Dictionary<string, object?>
            {
                ["code"] = p.Code,
                ["text"] = SafeText(p, out _),
            };

            try
            {
                switch (p.Data)
                {
                    case ParameterGroup grp:
                        d["kind"] = "group";
                        d["id"] = grp.ID;
                        d["name"] = grp.Name;
                        d["inactiveOnStart"] = grp.GroupFlags["InactiveOnStart"];
                        d["closed"] = grp.GroupFlags["Closed"];
                        break;
                    case ParameterGroupPointer ptr:
                        d["kind"] = "groupPointer";
                        d["id"] = ptr.ID;
                        break;
                    case ParameterCreate create:
                        d["kind"] = "create";
                        d["objectInfo"] = create.ObjectInfo;
                        d["x"] = create.X;
                        d["y"] = create.Y;
                        d["layer"] = create.Layer;
                        d["parent"] = create.ObjectInfoParent;
                        d["flags"] = create.CreateFlags.Value;
                        d["flagsDecoded"] = PositionCreateFlagsDecoded(create.CreateFlags);
                        TryAddCreateObjectName(d, p, create.ObjectInfo);
                        break;
                    case ParameterPosition pos:
                        d["kind"] = "position";
                        d["x"] = pos.X;
                        d["y"] = pos.Y;
                        d["layer"] = pos.Layer;
                        d["parent"] = pos.ObjectInfoParent;
                        d["flags"] = pos.PositionFlags.Value;
                        d["flagsDecoded"] = PositionCreateFlagsDecoded(pos.PositionFlags);
                        break;
                    case ParameterShoot shoot:
                        d["kind"] = "shoot";
                        d["objectInfo"] = shoot.ObjectInfo;
                        d["x"] = shoot.X;
                        d["y"] = shoot.Y;
                        d["layer"] = shoot.Layer;
                        d["speed"] = shoot.ShootSpeed;
                        d["flags"] = shoot.ShootFlags.Value;
                        d["flagsDecoded"] = new Dictionary<string, object?>
                        {
                            ["calculateDirection"] = shoot.ShootFlags["CalculateDirection"],
                        };
                        d["direction"] = DirectionMaskDoc((uint)shoot.Direction);
                        break;
                    case ParameterInt pint when p.Code == 29:
                        d["kind"] = "direction";
                        foreach (var kv in DirectionMaskDoc((uint)pint.Value))
                            d[kv.Key] = kv.Value;
                        break;
                    case ParameterExpressions exps:
                        d["kind"] = "expression";
                        d["comparison"] = exps.Comparison;
                        d["comparisonOp"] = ComparisonOp(exps.Comparison);
                        d["tokens"] = exps.Expressions.Select(e => SafeText(e, out _)).ToList();
                        break;
                    case ParameterRemark:
                        d["kind"] = "comment";
                        d.Remove("text");
                        break;
                    case ParameterChildEvent child:
                        d["kind"] = "childEvents";
                        d["objectInfos"] = child.ObjectInfos.Cast<object>().ToList();
                        break;
                    case ParameterObject obj:
                        d["kind"] = "object";
                        d["objectInfo"] = obj.ObjectInfo;
                        d["objectType"] = obj.ObjectType;
                        d["name"] = SafeText(obj, out _);
                        break;
                    case ParameterEvery every:
                        d["kind"] = "every";
                        d["delayMs"] = every.Delay;
                        d["text"] = every.Delay + " ms";
                        break;
                    default:
                        if (p.Data != null)
                        {
                            d["kind"] = "primitive";
                            d["value"] = SafeText(p.Data, out _);
                        }
                        break;
                }
            }
            catch { /* best-effort structured param fields */ }

            return d;
        }

        static Dictionary<string, object?> DirectionMaskDoc(uint mask)
        {
            var indices = ACEventBase.GetDirectionIndices(mask);
            return new Dictionary<string, object?>
            {
                ["text"] = ACEventBase.FormatDirectionMask(mask, ACEventBase.DirectionMaskStyle.List),
                ["mask"] = mask,
                ["directions"] = indices.Cast<object>().ToList(),
                ["degrees"] = indices.Select(i => (object)ACEventBase.DirectionToDegrees(i)).ToList(),
            };
        }

        static void TryAddItemHandle(Dictionary<string, object?> d, ACEventBase ace)
        {
            if (!NebulaCore.MFA)
                return;
            if (ace.ObjectType < 0 && ace.ObjectType != -7)
                return;
            FrameEvents? fe = ace.Parent?.Parent;
            if (fe == null || fe.EventObjects.Count == 0)
                return;
            if (fe.EventObjects.TryGetValue(ace.ObjectInfo, out EventObject? eo) && eo != null)
                d["itemHandle"] = eo.ItemHandle;
        }

        static void TryAddCreateObjectName(Dictionary<string, object?> d, Parameter p, ushort objectInfo)
        {
            try
            {
                FrameEvents? fe = p.FrameEvents;
                Frame? frm = fe?.Parent;
                if (fe != null && fe.EventObjects.TryGetValue(objectInfo, out EventObject? eo))
                {
                    if (eo.ObjectType == 1)
                    {
                        int handle = (int)eo.ItemHandle;
                        d["itemHandle"] = handle;
                        if (frm?.FrameObjectItems.TryGetValue(handle, out ObjectInfo? localOi) == true && localOi != null)
                        {
                            d["objectName"] = localOi.Name;
                            return;
                        }
                        if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(handle, out localOi) && localOi != null)
                        {
                            d["objectName"] = localOi.Name;
                            return;
                        }
                    }
                    if (!string.IsNullOrEmpty(eo.Name))
                    {
                        d["objectName"] = eo.Name;
                        return;
                    }
                }
                ObjectInfo? resolved = fe?.ResolveObject(objectInfo);
                if (resolved != null && !string.IsNullOrEmpty(resolved.Name))
                {
                    d["objectName"] = resolved.Name;
                    return;
                }
                if (NebulaCore.PackageData.FrameItems.Items.TryGetValue(objectInfo, out ObjectInfo? oi) && oi != null)
                    d["objectName"] = oi.Name;
            }
            catch { }
        }

        static Dictionary<string, object?> PositionCreateFlagsDecoded(BitDict flags) => new()
        {
            ["offsetFromActionPoint"] = flags["OffsetFromActionPoint"],
            ["offsetFromDirection"] = flags["OffsetFromDirection"],
            ["inheritDirection"] = flags["InheritDirection"],
            ["dontInheritDirection"] = flags["DontInheritDirection"],
        };

        static string ComparisonOp(short comparison) => comparison switch
        {
            0 => "=",
            1 => "<>",
            2 => "<=",
            3 => "<",
            4 => ">=",
            5 => ">",
            _ => "=",
        };

        // ---------------- fingerprint / helpers ----------------

        static string FingerprintEvent(Event evt)
        {
            var sb = new StringBuilder();
            sb.Append("F").Append(evt.EventFlags.Value).Append(';');
            foreach (var c in evt.Conditions)
                sb.Append(FingerprintCondition(c)).Append(';');
            sb.Append('|');
            foreach (var a in evt.Actions)
                sb.Append(FingerprintAction(a)).Append(';');

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(bytes)[..16];
        }

        static string FingerprintCondition(Condition c)
        {
            var sb = new StringBuilder();
            sb.Append("C").Append(c.ObjectType).Append(',').Append(c.Num).Append(',')
              .Append(c.ObjectInfo).Append(',').Append(c.OtherFlags["Negated"]);
            foreach (var p in c.Parameters)
                sb.Append(',').Append(p.Code).Append(':').Append(SafeText(p, out _));
            return sb.ToString();
        }

        static string FingerprintAction(Action a)
        {
            var sb = new StringBuilder();
            sb.Append("A").Append(a.ObjectType).Append(',').Append(a.Num).Append(',')
              .Append(a.ObjectInfo);
            foreach (var p in a.Parameters)
                sb.Append(',').Append(p.Code).Append(':').Append(SafeText(p, out _));
            return sb.ToString();
        }

        static string SafeObjectName(ACEventBase ace)
        {
            try { return ace.GetObjectName(); }
            catch { return $"#{ace.ObjectInfo}"; }
        }

        static string SafeText(object o) => SafeText(o, out _);

        static string SafeText(object o, out string? error)
        {
            error = null;
            try
            {
                string text = o.ToString() ?? "";
                if (text.StartsWith("[ERROR]", StringComparison.Ordinal))
                    error = text;
                return text;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return "";
            }
        }

        static Dictionary<string, object?> Rgb(System.Drawing.Color c) => new()
        {
            ["r"] = c.R,
            ["g"] = c.G,
            ["b"] = c.B,
            ["a"] = 255,
        };

        // Bit 17 means "don't create at start". MFA uses the misleading key
        // "CreateAtStart" for that same bit, and its BitDict is copied unchanged.
        static bool CreateAtStartOf(ObjectCommon oc) => (oc.ObjectFlags.Value & (1u << 17)) == 0;

        static Dictionary<string, object?> InkOf(ObjectInfo oi, MFAObjectInfo? source = null) => new()
        {
            ["inkEffect"] = source?.InkEffect ?? oi.Header.InkEffect,
            ["inkEffectName"] = InkEffectName(source?.InkEffect ?? oi.Header.InkEffect),
            ["inkEffectParam"] = source?.InkEffectParameter ?? oi.Header.InkEffectParam,
            ["blendCoeff"] = source?.ObjectEffects?.BlendCoeff ?? oi.Header.BlendCoeff,
            ["rgbCoeff"] = Rgb(source?.ObjectEffects?.RGBCoeff ?? oi.Header.RGBCoeff),
            ["transparent"] = source?.Transparent ?? !oi.Header.InkEffectFlags["NotTransparent"],
        };

        static Dictionary<string, object?> LayerEffectOf(FrameLayerEffect effect) => new()
        {
            ["inkEffect"] = effect.InkEffect,
            ["inkEffectName"] = InkEffectName(effect.InkEffect),
            ["inkEffectParam"] = effect.InkEffectParam,
            ["blendCoeff"] = effect.BlendCoeff,
            ["rgbCoeff"] = Rgb(effect.RGBCoeff),
        };

        static Dictionary<string, object?> BuildRemark(ParameterRemark remark)
        {
            var d = new Dictionary<string, object?>
            {
                ["text"] = remark.ResolveText(),
                ["handle"] = remark.CommentHandle,
                ["alignment"] = remark.Alignment,
                ["alignmentName"] = remark.AlignmentName,
                ["color"] = Rgb(remark.Color),
                ["fontFace"] = remark.FaceName,
                ["fontHeight"] = remark.Height,
                ["fontWeight"] = remark.Weight,
                ["fontItalic"] = remark.Italic != 0,
                ["fontUnderline"] = remark.Underline != 0,
                ["fontStyle"] = remark.StyleName,
            };
            return d;
        }

        static Dictionary<string, object?> BuildMovement(ObjectMovement m)
        {
            bool isExt = m.MovementDefinition is ObjectMovementExtension;
            short type = isExt ? (short)14 : m.Type;
            var d = new Dictionary<string, object?>
            {
                ["name"] = m.Name,
                ["type"] = type,
                ["typeName"] = MovementTypeName(type, m.MovementDefinition),
                ["player"] = m.Player,
                ["movingAtStart"] = m.Move != 0,
                ["move"] = m.Move,
                ["opt"] = m.Opt,
                ["startingDirection"] = m.StartingDirection,
            };

            switch (m.MovementDefinition)
            {
                case ObjectMovementMouse mouse:
                    d["dx"] = mouse.Dx;
                    d["fx"] = mouse.Fx;
                    d["dy"] = mouse.Dy;
                    d["fy"] = mouse.Fy;
                    d["mouseFlags"] = mouse.MouseFlags.Value;
                    break;
                case ObjectMovementRace race:
                    d["speed"] = race.Speed;
                    d["acceleration"] = race.Acceleration;
                    d["deceleration"] = race.Deceleration;
                    d["rotation"] = race.Rotation;
                    d["bounceMultiplier"] = race.BounceMultiplier;
                    d["angles"] = race.Angles;
                    d["reversable"] = race.Reversable != 0;
                    break;
                case ObjectMovementGeneric generic:
                    d["speed"] = generic.Speed;
                    d["acceleration"] = generic.Acceleration;
                    d["deceleration"] = generic.Deceleration;
                    d["bounceMultiplier"] = generic.BounceMultiplier;
                    d["directions"] = generic.Direction;
                    break;
                case ObjectMovementBall ball:
                    d["speed"] = ball.Speed;
                    d["bounce"] = ball.Bounce;
                    d["angles"] = ball.Angles;
                    d["anglesCount"] = BallAnglesCount(ball.Angles);
                    d["security"] = ball.Security;
                    d["deceleration"] = ball.Decelerate;
                    break;
                case ObjectMovementPath path:
                    d["minimumSpeed"] = path.MinimumSpeed;
                    d["maximumSpeed"] = path.MaximumSpeed;
                    d["loop"] = path.Loop != 0;
                    d["reposition"] = path.Reposition != 0;
                    d["reverse"] = path.Reverse != 0;
                    d["nodes"] = path.PathNodes.Select(n => (object)new Dictionary<string, object?>
                    {
                        ["speed"] = n.Speed,
                        ["direction"] = n.Direction,
                        ["dx"] = n.Dx,
                        ["dy"] = n.Dy,
                        ["length"] = n.Length,
                        ["pause"] = n.Pause,
                        ["name"] = n.Name,
                    }).ToList();
                    break;
                case ObjectMovementPlatform platform:
                    d["speed"] = platform.Speed;
                    d["acceleration"] = platform.Acceleration;
                    d["deceleration"] = platform.Deceleration;
                    d["jumpControl"] = platform.JumpControl;
                    d["gravity"] = platform.Gravity;
                    d["jump"] = platform.Jump;
                    break;
                case ObjectMovementExtension ext:
                    d["extension"] = ext.FileName;
                    d["identifier"] = MovementFourCC(m.ID);
                    d["id"] = m.ID;
                    if (!string.IsNullOrEmpty(ext.FileName) &&
                        ext.FileName.IndexOf("InAndOut", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (var kv in ParseInAndOut(ext.Data))
                            d[kv.Key] = kv.Value;
                    }
                    else if (!string.IsNullOrEmpty(ext.FileName) &&
                        ext.FileName.IndexOf("circular", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (var kv in ParseCircular(ext.Data))
                            d[kv.Key] = kv.Value;
                    }
                    else if (ext.Data.Length > 0)
                    {
                        d["dataSize"] = ext.Data.Length;
                        d["dataHex"] = Convert.ToHexString(ext.Data).ToLowerInvariant();
                    }
                    break;
            }

            return d;
        }

        static int[] ReadMovementExtInts(byte[] data)
        {
            if (data == null || data.Length < 4)
                return Array.Empty<int>();
            int offset = data.Length % 4 == 1 ? 1 : 0;
            int count = (data.Length - offset) / 4;
            if (count < 1)
                return Array.Empty<int>();
            int[] ints = new int[count];
            for (int i = 0; i < count; i++)
                ints[i] = BitConverter.ToInt32(data, offset + i * 4);
            return ints;
        }

        static Dictionary<string, object?> ParseInAndOut(byte[] data)
        {
            var d = new Dictionary<string, object?>();
            int[] ints = ReadMovementExtInts(data);
            if (ints.Length < 4)
                return d;

            // After the version byte: interpolation type, angle in degrees,
            // duration in milliseconds, flags, destination X, destination Y.
            int flags = ints[3];
            d["flags"] = flags;
            d["movingAtStart"] = (flags & 2) != 0;
            d["durationMs"] = ints[2];
            if (ints.Length > 4)
                d["destinationX"] = ints[4];
            if (ints.Length > 5)
                d["destinationY"] = ints[5];
            d["inAndOutType"] = ints[0];
            d["inAndOutTypeName"] = ints[0] switch
            {
                0 => "Linear",
                1 => "Smooth",
                _ => "Unknown",
            };
            d["direction"] = ints[1];
            d["directionName"] = ints[1] switch
            {
                0 => "Right",
                45 => "Top-Right",
                90 => "Top",
                135 => "Top-Left",
                180 => "Left",
                225 => "Bottom-Left",
                270 => "Bottom",
                315 => "Bottom-Right",
                _ => "Custom",
            };
            return d;
        }

        static Dictionary<string, object?> ParseCircular(byte[] data)
        {
            var d = new Dictionary<string, object?>();
            int[] ints = ReadMovementExtInts(data);
            if (ints.Length < 4)
                return d;

            d["centerX"] = ints[0];
            d["centerY"] = ints[1];
            d["radius"] = ints[2];
            d["startingAngle"] = ints[3];
            if (ints.Length > 4)
                d["minSpiralRadius"] = ints[4];
            if (ints.Length > 5)
                d["maxSpiralRadius"] = ints[5];
            if (ints.Length > 6)
                d["movingAtStart"] = ints[6] != 0;
            if (ints.Length > 7)
            {
                d["onCompletion"] = ints[7];
                d["onCompletionName"] = ints[7] switch
                {
                    0 => "Stop",
                    1 => "Reverse angular velocity",
                    2 => "Reverse spiral velocity",
                    3 => "Reverse both",
                    _ => "Unknown",
                };
            }
            if (ints.Length > 8)
                d["angularVelocity"] = ints[8];
            if (ints.Length > 9)
                d["spiralVelocity"] = ints[9];
            return d;
        }

        static Dictionary<string, object?> BuildTransition(TransitionChunk t)
        {
            if (t == null || (string.IsNullOrEmpty(t.ModuleName) && string.IsNullOrEmpty(t.ID)))
                return new Dictionary<string, object?> { ["name"] = "None" };

            var d = new Dictionary<string, object?>
            {
                ["name"] = string.IsNullOrEmpty(t.ModuleName) ? TransitionName(t.ID) : t.ModuleName,
                ["id"] = t.ID,
                ["fileName"] = t.FileName,
                ["duration"] = t.Duration,
                ["from"] = t.UseColor ? "color" : "background",
            };
            if (t.UseColor)
                d["color"] = Rgb(t.Color);
            else
                d["color"] = Rgb(t.Color);

            var extra = DecodeTransitionParams(t.ID, t.ParameterData);
            if (extra.Count > 0)
                d["params"] = extra;
            return d;
        }

        static Dictionary<string, object?> DecodeTransitionParams(string id, byte[] data)
        {
            var d = new Dictionary<string, object?>();
            if (data == null || data.Length == 0)
                return d;

            int[] ints = ReadLEInts(data);
            string[] names = id switch
            {
                "BAND" => new[] { "bandCount", "direction" },
                "DOOR" => new[] { "style" },
                "SE00" => new[] { "direction", "style" },
                "SE10" => new[] { "style" },
                "SE12" => new[] { "cellSize", "style" },
                "SE03" => new[] { "lineCount", "direction" },
                "MOSA" => new[] { "blockSize" },
                "SE05" => new[] { "style" },
                "SE06" => new[] { "direction" },
                "SCRL" => new[] { "direction" },
                "SE01" => new[] { "style" },
                "SE07" => new[] { "style" },
                "SE09" => new[] { "style" },
                "SE08" => new[] { "style" },
                "SE02" => new[] { "style" },
                "SE13" => new[] { "style" },
                "ZIGZ" => new[] { "style" },
                "SE04" => new[] { "style" },
                "SE11" => new[] { "style" },
                _ => Array.Empty<string>(),
            };

            if (names.Length == 0)
            {
                if (ints.Length == 1)
                    d["value"] = ints[0];
                else if (ints.Length > 1)
                    d["values"] = ints.Cast<object>().ToList();
                else
                    d["dataHex"] = Convert.ToHexString(data).ToLowerInvariant();
                return d;
            }

            for (int i = 0; i < names.Length && i < ints.Length; i++)
                d[names[i]] = ints[i];
            if (ints.Length > names.Length)
                d["extra"] = ints.Skip(names.Length).Cast<object>().ToList();
            return d;
        }

        static int[] ReadLEInts(byte[] data)
        {
            if (data.Length >= 4 && data.Length % 4 == 0)
            {
                int[] ints = new int[data.Length / 4];
                for (int i = 0; i < ints.Length; i++)
                    ints[i] = BitConverter.ToInt32(data, i * 4);
                return ints;
            }
            if (data.Length == 2)
                return new[] { (int)BitConverter.ToInt16(data, 0) };
            if (data.Length >= 2 && data.Length % 2 == 0)
            {
                int[] shorts = new int[data.Length / 2];
                for (int i = 0; i < shorts.Length; i++)
                    shorts[i] = BitConverter.ToInt16(data, i * 2);
                return shorts;
            }
            if (data.Length == 1)
                return new[] { (int)data[0] };
            return Array.Empty<int>();
        }

        static string MovementTypeName(short type, ObjectMovementDefinition def) => def switch
        {
            ObjectMovementExtension ext when !string.IsNullOrEmpty(ext.FileName) =>
                ext.FileName.IndexOf("InAndOut", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "InAndOut"
                    : ext.FileName.IndexOf("circular", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "Circular"
                        : ("Extension (" + ext.FileName + ")"),
            _ => type switch
            {
                0 => "Stopped",
                1 => "Mouse Controlled",
                2 => "Race Car",
                3 => "Eight Directions",
                4 => "Bouncing Ball",
                5 => "Path",
                9 => "Platform",
                14 => "Extension",
                _ => "Unknown",
            }
        };

        static int BallAnglesCount(short angles) => angles switch
        {
            0 => 32,
            1 => 16,
            2 => 8,
            3 => 12,
            _ => angles,
        };

        static string MovementFourCC(int id)
        {
            char c0 = (char)(id & 0xFF);
            char c1 = (char)((id >> 8) & 0xFF);
            char c2 = (char)((id >> 16) & 0xFF);
            char c3 = (char)((id >> 24) & 0xFF);
            if (c0 >= 32 && c0 < 127 && c1 >= 32 && c1 < 127 && c2 >= 32 && c2 < 127 && c3 >= 32 && c3 < 127)
                return new string(new[] { c0, c1, c2, c3 });
            return string.Empty;
        }

        static string TransitionName(string id) => id switch
        {
            "SE00" => "Advanced Scrolling",
            "SE10" => "Back",
            "BAND" => "Bands",
            "SE12" => "Cell",
            "DOOR" => "Door",
            "FADE" => "Fade",
            "SE03" => "Line",
            "MOSA" => "Mosaic",
            "SE05" => "Open",
            "SE06" => "Push",
            "SCRL" => "Scrolling",
            "SE01" => "Square",
            "SE07" => "Stretch",
            "SE09" => "Stretch 2",
            "SE08" => "Turn",
            "SE02" => "Turn 2",
            "SE13" => "Weft",
            "ZIGZ" => "Zigzag",
            "SE04" => "ZigZag 2",
            "ZOOM" => "Zoom",
            "SE11" => "Zoom 2",
            _ => string.IsNullOrEmpty(id) ? "None" : id,
        };

        static string InkEffectName(int inkEffect) => inkEffect switch
        {
            0 => "None",
            1 => "Semi-transparent",
            2 => "Inverted",
            3 => "XOR",
            4 => "AND",
            5 => "OR",
            9 => "Add",
            10 => "Monochrome",
            11 => "Subtract",
            _ => "Unknown",
        };

        static string FillTypeName(int fillType) => fillType switch
        {
            0 => "None",
            1 => "Solid Color",
            2 => "Gradient",
            3 => "Motif",
            _ => "Unknown",
        };

        static string ShapeName(int shape) => shape switch
        {
            0 => "Line",
            1 => "Line",
            2 => "Rectangle",
            3 => "Ellipse",
            _ => "Unknown",
        };

        static string TypeName(int type) => type switch
        {
            0 => "QuickBackdrop",
            1 => "Backdrop",
            2 => "Active",
            3 => "String",
            4 => "Question",
            5 => "Score",
            6 => "Lives",
            7 => "Counter",
            8 => "FormattedText",
            9 => "SubApplication",
            >= 32 => "Extension",
            _ => "Unknown",
        };
    }

    /// <summary>Summary counts returned by <see cref="MFSPLExporter.Export"/>.</summary>
    public class ExportResult
    {
        public int Frames;
        public int Objects;
    }

    /// <summary>Summary counts returned by <see cref="MFSPLExporter.ExportAssets"/>.</summary>
    public class AssetExportResult
    {
        public int Images;
        public int ImageDuplicates;
        public int Sounds;
        public int SoundDuplicates;
    }

    /// <summary>Summary counts returned by <see cref="MFSPLExporter.ExportSpritesheets"/>.</summary>
    public class SpritesheetResult
    {
        public int Objects;
        public int Sheets;
        public int Skipped;
    }

    /// <summary>Summary counts returned by <see cref="MFSPLExporter.ExportTiles"/>.</summary>
    public class TileExportResult
    {
        public int Frames;
        public int Tiles;
    }

    /// <summary>Summary counts returned by <see cref="MFSPLExporter.ExportLayers"/>.</summary>
    public class LayerExportResult
    {
        public int Frames;
        public int Layers;
    }
}
