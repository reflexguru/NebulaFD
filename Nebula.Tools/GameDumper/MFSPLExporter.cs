using Nebula;
using Nebula.Core.Data.Chunks.BankChunks.Images;
using Nebula.Core.Data.Chunks.BankChunks.Sounds;
using Nebula.Core.Data.Chunks.FrameChunks;
using Nebula.Core.Data.Chunks.ObjectChunks;
using Nebula.Core.Data.Chunks.ObjectChunks.ObjectCommon;
using Nebula.Core.Data.PackageReaders;
using Nebula.Core.Utilities;
using Spectre.Console;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Event = Nebula.Core.Data.Chunks.FrameChunks.Events.Event;
using Action = Nebula.Core.Data.Chunks.FrameChunks.Events.Action;
using Condition = Nebula.Core.Data.Chunks.FrameChunks.Events.Condition;
using ACEventBase = Nebula.Core.Data.Chunks.FrameChunks.Events.ACEventBase;
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
            File.WriteAllText(Path.Combine(outputDir, "events.json"), JsonSerializer.Serialize(BuildEventsDoc(mfa), JsonOptions));
            return new ExportResult { Frames = mfa.Frames.Count, Objects = mfa.FrameItems.Items.Count };
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
                    Bitmap bmp = img.GetBitmap();
                    byte[] png;
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        png = ms.ToArray();
                    }
                    img.DisposeBmp();

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
        /// No splitting or tile classification is performed here — that is the generator's job.
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
                int order = 0;
                foreach (var inst in frm.FrameInstances.Instances)
                {
                    if (!mfa.FrameItems.Items.TryGetValue((int)inst.ObjectInfo, out var oi))
                    {
                        order++;
                        continue;
                    }

                    uint obstacle = 0, image = 0;
                    int w = 0, h = 0;
                    int fillType = 3; // 1=Solid, 2=Gradient, 3=Motif(pattern)
                    int color1 = 0, color2 = 0;
                    bool isBackground = false;

                    switch (oi.Properties)
                    {
                        case ObjectQuickBackdrop qb:
                            obstacle = qb.ObstacleType;
                            w = qb.Width;
                            h = qb.Height;
                            image = qb.Shape.Image;
                            fillType = qb.Shape.FillType;
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

                    tiles.Add(new Dictionary<string, object?>
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
                        ["isBackground"] = isBackground,
                        ["object"] = oi.Name,
                        ["objectType"] = oi.Header.Type,
                        ["layer"] = inst.Layer,
                        ["order"] = order,
                    });
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

        // ---------------- spritesheets ----------------

        /// <summary>
        /// Exports each Active object's animation directions as sprite sheets: one PNG per direction, with
        /// frames laid out left-to-right in the direction's original frame order (never re-sorted) — so a
        /// "left 2 frames / right 2 frames" animation keeps its natural order. Each frame's hotspot / action
        /// point (CTF anchors with no Godot equivalent) is recorded in sheet.json for Sprite2D.offset mapping.
        /// Identical objects across levels share one directory (content-fingerprint dedup); same-named objects
        /// with different content get a hash suffix.
        /// </summary>
        public static SpritesheetResult ExportSpritesheets(
            MFAPackageData mfa,
            string spritesDir,
            Dictionary<string, string>? sheetIndex = null)
        {
            Directory.CreateDirectory(spritesDir);
            sheetIndex ??= new Dictionary<string, string>(); // key -> fingerprint

            int objects = 0, sheets = 0, skipped = 0;

            foreach (var oi in mfa.FrameItems.Items.Values)
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
                    var bmp = img.GetBitmap();
                    byte[] png;
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        png = ms.ToArray();
                    }
                    img.DisposeBmp();
                    var f = new DirFrame
                    {
                        Handle = handle,
                        Png = png,
                        Hash = Convert.ToHexString(SHA256.HashData(png))[..16].ToLowerInvariant(),
                        Width = img.Width,
                        Height = img.Height,
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

                // One PNG per direction, frames left-to-right in original order (no re-sorting).
                int sheetW = dd.Frames.Sum(f => f.Width);
                int sheetH = dd.Frames.Max(f => f.Height);
                string sheetName = $"a{dd.AnimIndex}_d{dd.DirIndex}.png";

                var framesDoc = new List<object?>();
                using (var atlas = new Bitmap(sheetW, sheetH))
                using (var g = Graphics.FromImage(atlas))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    int x = 0;
                    foreach (var f in dd.Frames)
                    {
                        using (var ms = new MemoryStream(f.Png))
                        using (var bmp = new Bitmap(ms))
                            g.DrawImage(bmp, x, 0, f.Width, f.Height);
                        framesDoc.Add(new Dictionary<string, object?>
                        {
                            ["handle"] = f.Handle,
                            ["rect"] = new Dictionary<string, object?> { ["x"] = x, ["y"] = 0, ["w"] = f.Width, ["h"] = f.Height },
                            ["hotspotX"] = f.HotspotX,
                            ["hotspotY"] = f.HotspotY,
                            ["actionPointX"] = f.ActionPointX,
                            ["actionPointY"] = f.ActionPointY,
                        });
                        x += f.Width;
                    }
                    atlas.Save(Path.Combine(sheetDir, sheetName), ImageFormat.Png);
                }

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
                    ["sheetWidth"] = sheetW,
                    ["sheetHeight"] = sheetH,
                    ["frames"] = framesDoc,
                });
            }

            File.WriteAllText(Path.Combine(sheetDir, "sheet.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["object"] = objectName,
                ["directions"] = dirsDoc,
            }, JsonOptions));
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
            public byte[] Png = Array.Empty<byte>();
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
            var objectTypes = new Dictionary<string, object?>();
            foreach (var kv in mfa.FrameItems.Items)
                objectTypes[kv.Key.ToString()] = BuildTypeDef(kv.Value);

            var frames = new List<object?>();
            foreach (var frm in mfa.Frames)
                frames.Add(BuildFrame(frm));

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
                ["frames"] = frames,
            };
        }

        static Dictionary<string, object?> BuildTypeDef(ObjectInfo oi)
        {
            var d = new Dictionary<string, object?>
            {
                ["handle"] = oi.Header.Handle,
                ["name"] = oi.Name,
                ["type"] = oi.Header.Type,
                ["typeName"] = TypeName(oi.Header.Type),
                ["objectFlags"] = oi.Header.ObjectFlags.Value,
            };

            try
            {
                switch (oi.Properties)
                {
                    case ObjectQuickBackdrop qb:
                        d["obstacleType"] = qb.ObstacleType;
                        d["collisionType"] = qb.CollisionType;
                        d["width"] = qb.Width;
                        d["height"] = qb.Height;
                        break;
                    case ObjectBackdrop bd:
                        d["obstacleType"] = bd.ObstacleType;
                        d["collisionType"] = bd.CollisionType;
                        d["image"] = bd.Image;
                        break;
                    case ObjectCommon oc:
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
                        d["movements"] = oc.ObjectMovements.Movements.Select(m => (object)new Dictionary<string, object?>
                        {
                            ["name"] = m.Name,
                            ["type"] = m.Type,
                            ["move"] = m.Move,
                            ["opt"] = m.Opt,
                            ["startingDirection"] = m.StartingDirection,
                        }).ToList();
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

            return d;
        }

        static Dictionary<string, object?> BuildFrame(Frame frm)
        {
            var instances = new List<object?>();
            foreach (var inst in frm.FrameInstances.Instances)
                instances.Add(new Dictionary<string, object?>
                {
                    ["objectInfo"] = inst.ObjectInfo,
                    ["x"] = inst.PositionX,
                    ["y"] = inst.PositionY,
                    ["layer"] = inst.Layer,
                    ["instanceValue"] = inst.InstanceValue,
                    ["parentType"] = inst.ParentType,
                    ["parentHandle"] = inst.ParentHandle,
                });

            return new Dictionary<string, object?>
            {
                ["name"] = frm.FrameName,
                ["handle"] = frm.Handle,
                ["width"] = frm.FrameHeader.Width,
                ["height"] = frm.FrameHeader.Height,
                ["instances"] = instances,
            };
        }

        // ---------------- events.json ----------------

        static Dictionary<string, object?> BuildEventsDoc(MFAPackageData mfa)
        {
            var globalEvents = new List<object?>();
            foreach (var evt in mfa.GlobalEvents.Events)
                globalEvents.Add(BuildEvent(evt));

            // Dedup index: fingerprint -> { event, occurrence list }
            var fpMap = new Dictionary<string, Dictionary<string, object?>>();
            var fpOrder = new List<string>();

            void IndexEvent(Event evt, string occurrence)
            {
                string fp = FingerprintEvent(evt);
                if (!fpMap.ContainsKey(fp))
                {
                    fpMap[fp] = new Dictionary<string, object?>
                    {
                        ["fingerprint"] = fp,
                        ["frames"] = new List<string>(),
                        ["event"] = BuildEvent(evt),
                    };
                    fpOrder.Add(fp);
                }
                ((List<string>)fpMap[fp]["frames"]!).Add(occurrence);
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
                frames.Add(new Dictionary<string, object?>
                {
                    ["name"] = frm.FrameName,
                    ["events"] = evts,
                });
            }

            var dedup = new List<object?>();
            foreach (var fp in fpOrder)
            {
                var entry = fpMap[fp];
                var occ = (List<string>)entry["frames"]!;
                dedup.Add(new Dictionary<string, object?>
                {
                    ["fingerprint"] = fp,
                    ["count"] = occ.Count,
                    ["frames"] = occ,
                    ["event"] = entry["event"],
                });
            }

            return new Dictionary<string, object?>
            {
                ["globalEvents"] = globalEvents,
                ["frames"] = frames,
                ["dedup"] = dedup,
            };
        }

        static Dictionary<string, object?> BuildEvent(Event evt)
        {
            return new Dictionary<string, object?>
            {
                ["conditions"] = evt.Conditions.Select(c => BuildCondition(c)).ToList(),
                ["actions"] = evt.Actions.Select(a => BuildAction(a)).ToList(),
                ["flags"] = evt.EventFlags.Value,
            };
        }

        static Dictionary<string, object?> BuildCondition(Condition c)
        {
            return new Dictionary<string, object?>
            {
                ["objectType"] = c.ObjectType,
                ["num"] = c.Num,
                ["objectInfo"] = c.ObjectInfo,
                ["negated"] = c.OtherFlags["Negated"],
                ["object"] = SafeObjectName(c),
                ["text"] = SafeText(c),
                ["params"] = c.Parameters.Select(p => BuildParam(p)).ToList(),
            };
        }

        static Dictionary<string, object?> BuildAction(Action a)
        {
            return new Dictionary<string, object?>
            {
                ["objectType"] = a.ObjectType,
                ["num"] = a.Num,
                ["objectInfo"] = a.ObjectInfo,
                ["object"] = SafeObjectName(a),
                ["text"] = SafeText(a),
                ["params"] = a.Parameters.Select(p => BuildParam(p)).ToList(),
            };
        }

        static Dictionary<string, object?> BuildParam(Nebula.Core.Data.Chunks.FrameChunks.Events.Parameter p)
        {
            return new Dictionary<string, object?>
            {
                ["code"] = p.Code,
                ["text"] = SafeText(p),
            };
        }

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
              .Append(SafeObjectName(c)).Append(',').Append(c.OtherFlags["Negated"]);
            foreach (var p in c.Parameters)
                sb.Append(',').Append(p.Code).Append(':').Append(SafeText(p));
            return sb.ToString();
        }

        static string FingerprintAction(Action a)
        {
            var sb = new StringBuilder();
            sb.Append("A").Append(a.ObjectType).Append(',').Append(a.Num).Append(',')
              .Append(SafeObjectName(a));
            foreach (var p in a.Parameters)
                sb.Append(',').Append(p.Code).Append(':').Append(SafeText(p));
            return sb.ToString();
        }

        static string SafeObjectName(ACEventBase ace)
        {
            try { return ace.GetObjectName(); }
            catch { return $"#{ace.ObjectInfo}"; }
        }

        static string SafeText(object o)
        {
            try { return o.ToString() ?? ""; }
            catch { return ""; }
        }

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
}
