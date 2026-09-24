using Nebula;
using Nebula.Core.Data.PackageReaders;
using Nebula.Core.FileReaders;
using Nebula.Core.Memory;
using Nebula.Tools.GameDumper;
using Spectre.Console;
using System.Runtime;

// ---- argument parsing ----
string? input = null;
string outputRoot = Path.GetFullPath("mfspl_ir");
string? godotAbs = null;
string godotRes = "res://mfspl/sprite/ctf";
bool godotOnly = false;
string? godotTilesAbs = null;
string godotTilesRes = "res://mfspl/stage_ctf";
bool tilesOnly = false;
bool archive = false;
bool layersOnly = false;
bool irOnly = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-o":
        case "--out":
            if (i + 1 < args.Length) outputRoot = Path.GetFullPath(args[++i]);
            break;
        case "--godot":
            if (i + 1 < args.Length) godotAbs = Path.GetFullPath(args[++i]);
            break;
        case "--godot-res":
            if (i + 1 < args.Length) godotRes = args[++i];
            break;
        case "--godot-only":
            godotOnly = true;
            break;
        case "--tiles-godot":
            if (i + 1 < args.Length) godotTilesAbs = Path.GetFullPath(args[++i]);
            break;
        case "--tiles-godot-res":
            if (i + 1 < args.Length) godotTilesRes = args[++i];
            break;
        case "--tiles-only":
            tilesOnly = true;
            break;
        case "--archive":
            archive = true;
            break;
        case "--layers-only":
            layersOnly = true;
            break;
        case "--ir-only":
            irOnly = true;
            break;
        case "-h":
        case "--help":
            Console.WriteLine("Usage: mfspL-cli <mfa-file-or-dir> [-o <output-dir>] [--godot <dir> [--godot-res <resPath>]] [--tiles-godot <dir> [--tiles-godot-res <resPath>]]");
            Console.WriteLine("  Exports objects.json + events.json + images.json + sounds.json + tiles.json for each .mfa");
            Console.WriteLine("  into <output-dir>/<level>/; deduplicated images & sounds go to <output-dir>/assets/,");
            Console.WriteLine("  and per-object sprite sheets go to <output-dir>/sprites/.");
            Console.WriteLine("  --godot <dir>          emit Godot SpriteFrames .tres into <dir> (absolute path).");
            Console.WriteLine("  --godot-res <p>        res:// prefix for SpriteFrames (default res://mfspl/sprite/ctf).");
            Console.WriteLine("  --godot-only           skip MFA parsing; re-emit Godot SpriteFrames from existing -o/sprites.");
            Console.WriteLine("  --tiles-godot <dir>    emit per-frame Godot tilemaps into <dir>/<mfa>/frames/<frame>/.");
            Console.WriteLine("  --tiles-godot-res <p>  res:// prefix for tile assets (default res://mfspl/stage_ctf).");
            Console.WriteLine("  --tiles-only           skip MFA parsing; re-emit Godot tile data from existing -o tiles.json.");
            Console.WriteLine("  --archive              archive shared vs per-level assets (images + sprites) into -o; copies only, idempotent.");
    Console.WriteLine("  --layers-only          only emit layers.json (layer names + scroll coefficients) from the MFA sources.");
            Console.WriteLine("  --ir-only              objects.json + events.json + tiles.json + layers.json (no assets/sprites).");
            return 0;
        default:
            input ??= args[i];
            break;
    }
}

if (archive)
{
    if (!Directory.Exists(outputRoot)) { Console.WriteLine("output dir not found: " + outputRoot); return 2; }
    var ar = AssetArchiver.Run(outputRoot);
    Console.WriteLine($"Archive done. shared images={ar.SharedImages}, per-level images={ar.LevelImages}, shared sprites={ar.SharedSprites}, per-level sprites={ar.LevelSprites}, skipped images={ar.SkippedImages}, skipped sprites={ar.SkippedSprites}, copied={ar.CopiedBytes} bytes");
    Console.WriteLine($"  shared → {Path.Combine(outputRoot, "shared")} | {Path.Combine(outputRoot, "shared_sprites")}");
    return 0;
}

if (godotOnly)
{
    if (godotAbs == null) { Console.WriteLine("--godot-only requires --godot <dir>"); return 2; }
    string godotSpritesDir = Path.Combine(outputRoot, "sprites");
    if (!Directory.Exists(godotSpritesDir)) { Console.WriteLine("sprites dir not found: " + godotSpritesDir); return 3; }
    var gres = GodotSpriteFramesExporter.Export(godotSpritesDir, godotAbs, godotRes);
    Console.WriteLine($"Godot SpriteFrames: {gres.Objects} objects, {gres.Animations} animations → {godotAbs}");
    return 0;
}

if (tilesOnly)
{
    if (godotTilesAbs == null) { Console.WriteLine("--tiles-only requires --tiles-godot <dir>"); return 2; }
    if (!Directory.Exists(outputRoot)) { Console.WriteLine("output dir not found: " + outputRoot); return 3; }
    var tileDirs = Directory.GetDirectories(outputRoot)
        .Where(d => File.Exists(Path.Combine(d, "tiles.json")) && File.Exists(Path.Combine(d, "images.json")))
        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
    int tOk = 0, tFail = 0;
    int cells = 0, statics = 0, backgrounds = 0, conflicts = 0;
    foreach (var d in tileDirs)
    {
        try
        {
            string dst = Path.Combine(godotTilesAbs, Path.GetFileName(d));
            var tres = GodotTileExporter.Export(d, dst, godotTilesRes + "/" + Path.GetFileName(d));
            cells += tres.CellTiles; statics += tres.StaticBodies;
            backgrounds += tres.Backgrounds; conflicts += tres.Conflicts;
            Console.WriteLine($"  tile→ {Path.GetFileName(d)}: {tres.FrameScenes} frame scenes, {tres.CellTiles} cells, {tres.StaticBodies} statics, {tres.Conflicts} conflicts");
            tOk++;
        }
        catch (Exception ex) { Console.WriteLine($"  tile FAILED {Path.GetFileName(d)}: {ex.Message}"); tFail++; }
    }
    Console.WriteLine($"Godot tiles: {tOk} levels OK ({tFail} failed) → {godotTilesAbs} [cells={cells}, statics={statics}, backgrounds={backgrounds}, conflicts={conflicts}]");
    return tFail == 0 ? 0 : 3;
}

if (input == null)
{
    Console.WriteLine("Usage: mfspL-cli <mfa-file-or-dir> [-o <output-dir>]");
    return 1;
}

if (!Directory.Exists(input) && !File.Exists(input))
{
    Console.WriteLine("Input not found: " + input);
    return 2;
}

NebulaCore.Init();

// 静默 Nebula 解析阶段的 Spectre 日志（"Unknown Chunk" 等调试信息刷屏），只保留我们自己的进度输出
AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
{
    Out = new AnsiConsoleOutput(TextWriter.Null),
});

var files = new List<string>();
if (Directory.Exists(input))
    files.AddRange(Directory.GetFiles(input, "*.mfa", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
else
    files.Add(input);

Console.WriteLine($"Found {files.Count} .mfa file(s). Output → {outputRoot}");

// 共享资源池：跨关卡按内容哈希去重，图片/声音只写一份到 assets/
string assetDir = Path.Combine(outputRoot, "assets");
var imagePool = new Dictionary<string, string>();
var soundPool = new Dictionary<string, string>();

// 精灵表：跨关卡按内容指纹去重，同名对象只生成一份
string spritesDir = Path.Combine(outputRoot, "sprites");
var sheetIndex = new Dictionary<string, string>();
int totalSheets = 0, totalSkipped = 0;
int totalTiles = 0;
int totalLayers = 0;

int ok = 0, fail = 0;
for (int i = 0; i < files.Count; i++)
{
    string file = files[i];
    string name = Path.GetFileName(file);
    try
    {
        NebulaCore.CurrentReader = new MFAFileReader();
        var br = new ByteReader(File.ReadAllBytes(file));
        NebulaCore.CurrentReader.LoadGame(br, file);
        br.Dispose();

        var mfa = (MFAPackageData)NebulaCore.PackageData;
        string outDir = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(file));

        if (layersOnly)
        {
            var layerOnly = MFSPLExporter.ExportLayers(mfa, outDir);
            Console.WriteLine($"[{i + 1}/{files.Count}] {name} OK — frames={layerOnly.Frames}, layers={layerOnly.Layers}");
            ok++;
            continue;
        }

        var result = MFSPLExporter.Export(mfa, outDir);
        var tiles = MFSPLExporter.ExportTiles(mfa, outDir);
        totalTiles += tiles.Tiles;
        var layers = MFSPLExporter.ExportLayers(mfa, outDir);
        totalLayers += layers.Layers;
        if (irOnly)
        {
            Console.WriteLine($"[{i + 1}/{files.Count}] {name} OK — frames={result.Frames}, objects={result.Objects}, tiles={tiles.Tiles}, layers={layers.Layers} (ir-only)");
            ok++;
            continue;
        }
        var assets = MFSPLExporter.ExportAssets(mfa, outDir, assetDir, imagePool, soundPool);
        var sprites = MFSPLExporter.ExportSpritesheets(mfa, spritesDir, sheetIndex);
        totalSheets += sprites.Sheets;
        totalSkipped += sprites.Skipped;

        Console.WriteLine($"[{i + 1}/{files.Count}] {name} OK — frames={result.Frames}, objects={result.Objects}, images={assets.Images} (+{assets.ImageDuplicates} dup), sounds={assets.Sounds} (+{assets.SoundDuplicates} dup), sheets={sprites.Sheets} (+{sprites.Skipped} dup), tiles={tiles.Tiles}, layers={layers.Layers}");
        ok++;
    }
        catch (Exception ex)
        {
            Console.WriteLine($"[{i + 1}/{files.Count}] {name} FAILED — {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            fail++;
        }
    finally
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}

if (layersOnly)
{
    Console.WriteLine($"Done (layers only). OK={ok}, FAILED={fail}. Layers → per-level layers.json");
    return fail == 0 ? 0 : 3;
}

if (irOnly)
{
    Console.WriteLine($"Done (ir-only). OK={ok}, FAILED={fail}. JSON → {outputRoot}");
    Console.WriteLine($"Tiles: {totalTiles} backdrop rects; Layers: {totalLayers}");
    return fail == 0 ? 0 : 3;
}

MFSPLExporter.WriteSpriteIndex(spritesDir);

Console.WriteLine($"Done. OK={ok}, FAILED={fail}. Output → {outputRoot}");
Console.WriteLine($"Assets: {imagePool.Count} unique images, {soundPool.Count} unique sounds → {assetDir}");
Console.WriteLine($"Spritesheets: {totalSheets} sheets (+{totalSkipped} dup) → {spritesDir}");
Console.WriteLine($"Tiles: {totalTiles} backdrop rects → per-level tiles.json");
Console.WriteLine($"Layers: {totalLayers} frame layers (scroll coefficients) → per-level layers.json");

if (godotAbs != null)
{
    var gres = GodotSpriteFramesExporter.Export(spritesDir, godotAbs, godotRes);
    Console.WriteLine($"Godot SpriteFrames: {gres.Objects} objects, {gres.Animations} animations → {godotAbs}");
}

if (godotTilesAbs != null)
{
    var tileDirs = Directory.GetDirectories(outputRoot)
        .Where(d => File.Exists(Path.Combine(d, "tiles.json")) && File.Exists(Path.Combine(d, "images.json")))
        .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
    int tOk = 0, tFail = 0;
    int cells = 0, statics = 0, backgrounds = 0, conflicts = 0;
    foreach (var d in tileDirs)
    {
        try
        {
            string dst = Path.Combine(godotTilesAbs, Path.GetFileName(d));
            var tres = GodotTileExporter.Export(d, dst, godotTilesRes + "/" + Path.GetFileName(d));
            cells += tres.CellTiles; statics += tres.StaticBodies;
            backgrounds += tres.Backgrounds; conflicts += tres.Conflicts;
            Console.WriteLine($"  tile→ {Path.GetFileName(d)}: {tres.FrameScenes} frame scenes, {tres.CellTiles} cells, {tres.StaticBodies} statics, {tres.Conflicts} conflicts");
            tOk++;
        }
        catch (Exception ex) { Console.WriteLine($"  tile FAILED {Path.GetFileName(d)}: {ex.Message}"); tFail++; }
    }
    Console.WriteLine($"Godot tiles: {tOk} levels OK ({tFail} failed) → {godotTilesAbs} [cells={cells}, statics={statics}, backgrounds={backgrounds}, conflicts={conflicts}]");
}

return fail == 0 ? 0 : 3;
