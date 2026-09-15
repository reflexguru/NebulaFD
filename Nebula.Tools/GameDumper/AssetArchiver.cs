using System.Text;
using System.Text.Json;

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// 将已导出的 IR（mfspl_ir/）里的全部贴图素材按「公共 / 独有」归档，供导入 Godot 作为参考。
    /// - 图片（assets/img_&lt;hash&gt;.png）：按每关 images.json 的 file 引用统计，被 ≥2 关引用 → shared/，仅 1 关 → 该关 assets/
    /// - 精灵（sprites/&lt;对象&gt;/）：按每关 objects.json 的 objectTypes 统计对象名，被 ≥2 关引用 → shared_sprites/，仅 1 关 → 该关 sprites/
    /// 归档只做「复制新增」：原 assets/ 与 sprites/ 原样保留，下游引用零影响；重复运行幂等（同名覆盖写）。
    /// 文件名用中文（对象名 + __hash8）增强人类可读性；无对象引用的图用 背景_WxH__hash8。hash8 是原 hash16 的前 8 位，可回源到 assets/。
    /// </summary>
    public class AssetArchiver
    {
        public sealed class Result
        {
            public int SharedImages;
            public int LevelImages;
            public int SharedSprites;
            public int LevelSprites;
            public int SkippedImages;
            public int SkippedSprites;
            public long CopiedBytes;
        }

        public static Result Run(string outputRoot)
        {
            var result = new Result();
            string assetDir = Path.Combine(outputRoot, "assets");
            string spritesDir = Path.Combine(outputRoot, "sprites");
            string sharedDir = Path.Combine(outputRoot, "shared");
            string sharedSpritesDir = Path.Combine(outputRoot, "shared_sprites");

            var levelDirs = Directory.GetDirectories(outputRoot)
                .Where(d => File.Exists(Path.Combine(d, "images.json")))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (levelDirs.Count == 0) return result;

            // ---------- 1. 图片引用统计（hash 文件名 → 引用关卡，同关去重） ----------
            var imgRefLevels = new Dictionary<string, HashSet<string>>();
            var imgSize = new Dictionary<string, (int w, int h)>();
            foreach (var level in levelDirs)
            {
                var seen = new HashSet<string>();
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(level, "images.json")));
                if (!doc.RootElement.TryGetProperty("images", out var imgs)) continue;
                foreach (var prop in imgs.EnumerateObject())
                {
                    var entry = prop.Value;
                    string? file = entry.TryGetProperty("file", out var fp) ? fp.GetString() : null;
                    if (string.IsNullOrEmpty(file)) continue;
                    string filename = Path.GetFileName(Path.GetFullPath(Path.Combine(level, file)));
                    if (string.IsNullOrEmpty(filename)) continue;

                    if (seen.Add(filename))
                    {
                        if (!imgRefLevels.TryGetValue(filename, out var set))
                            imgRefLevels[filename] = set = new HashSet<string>();
                        set.Add(level);
                    }
                    if (!imgSize.ContainsKey(filename))
                    {
                        int w = entry.TryGetProperty("width", out var wp) && wp.TryGetInt32(out var wi) ? wi : 0;
                        int h = entry.TryGetProperty("height", out var hp) && hp.TryGetInt32(out var hi) ? hi : 0;
                        imgSize[filename] = (w, h);
                    }
                }
            }

            // ---------- 2. 图片中文名：优先取引用对象名（多个取最长），无对象引用 → 背景_WxH ----------
            var imgBestName = new Dictionary<string, string>();
            foreach (var level in levelDirs)
            {
                var handleName = new Dictionary<uint, string>();
                var objPath = Path.Combine(level, "objects.json");
                if (File.Exists(objPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(objPath));
                    if (doc.RootElement.TryGetProperty("objectTypes", out var types))
                    {
                        foreach (var prop in types.EnumerateObject())
                        {
                            var td = prop.Value;
                            string? name = td.TryGetProperty("name", out var np) ? np.GetString() : null;
                            if (string.IsNullOrEmpty(name)) continue;
                            int type = td.TryGetProperty("type", out var tp) ? tp.GetInt32() : -1;
                            if (type == 2 && td.TryGetProperty("animations", out var anims))
                            {
                                foreach (var anim in anims.EnumerateArray())
                                {
                                    if (!anim.TryGetProperty("directions", out var dirs)) continue;
                                    foreach (var dir in dirs.EnumerateArray())
                                    {
                                        if (!dir.TryGetProperty("frames", out var frms)) continue;
                                        foreach (var f in frms.EnumerateArray())
                                            if (f.TryGetInt32(out var h) && h > 0)
                                                handleName[(uint)h] = name;
                                    }
                                }
                            }
                            else if (td.TryGetProperty("image", out var imgP) && imgP.TryGetInt32(out var imgH) && imgH > 0)
                                handleName[(uint)imgH] = name;
                        }
                    }
                }
                // tiles.json：QuickBackdrop 的 image handle（objects.json 未输出该字段）
                var tilesPath = Path.Combine(level, "tiles.json");
                if (File.Exists(tilesPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(tilesPath));
                    if (doc.RootElement.TryGetProperty("frames", out var frames))
                    {
                        foreach (var frm in frames.EnumerateArray())
                        {
                            if (!frm.TryGetProperty("tiles", out var tiles)) continue;
                            foreach (var t in tiles.EnumerateArray())
                            {
                                if (!t.TryGetProperty("image", out var ip) || !ip.TryGetInt32(out var ih) || ih <= 0) continue;
                                string? name = t.TryGetProperty("object", out var op) ? op.GetString() : null;
                                if (string.IsNullOrEmpty(name)) continue;
                                if (!handleName.ContainsKey((uint)ih)) handleName[(uint)ih] = name;
                            }
                        }
                    }
                }
                if (handleName.Count == 0) continue;

                using var imgDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(level, "images.json")));
                if (!imgDoc.RootElement.TryGetProperty("images", out var imgs)) continue;
                foreach (var kv in handleName)
                {
                    if (!imgs.TryGetProperty(kv.Key.ToString(), out var entry)) continue;
                    string? file = entry.TryGetProperty("file", out var fp) ? fp.GetString() : null;
                    if (string.IsNullOrEmpty(file)) continue;
                    string filename = Path.GetFileName(Path.GetFullPath(Path.Combine(level, file)));
                    if (string.IsNullOrEmpty(filename)) continue;
                    if (!imgBestName.TryGetValue(filename, out var cur) || kv.Value.Length > cur.Length)
                        imgBestName[filename] = kv.Value;
                }
            }

            // ---------- 3. 复制图片 ----------
            foreach (var kv in imgRefLevels)
            {
                string filename = kv.Key;
                string src = Path.Combine(assetDir, filename);
                if (!File.Exists(src)) { result.SkippedImages++; continue; }

                string stem = Path.GetFileNameWithoutExtension(filename);
                string hash16 = stem.Length > 4 ? stem[4..] : stem;
                string hash8 = hash16.Length > 8 ? hash16[..8] : hash16;

                bool shared = kv.Value.Count >= 2;
                string dstDir = shared ? sharedDir : Path.Combine(kv.Value.First(), "assets");
                Directory.CreateDirectory(dstDir);

                string display = imgBestName.TryGetValue(filename, out var objName)
                    ? objName
                    : "背景_" + (imgSize.TryGetValue(filename, out var size) ? $"{size.w}x{size.h}" : "0x0");

                string baseName = Sanitize(display);
                string suffix = "__" + hash8 + ".png";
                int maxBase = 120 - suffix.Length;
                if (maxBase > 0 && baseName.Length > maxBase) baseName = baseName[..maxBase];
                string dstName = baseName + suffix;

                string dst = Path.Combine(dstDir, dstName);
                if (File.Exists(dst) && !FilesEqual(src, dst))
                    dst = Path.Combine(dstDir, baseName + "__" + hash16 + ".png"); // hash8 碰撞兜底

                File.Copy(src, dst, overwrite: true);
                result.CopiedBytes += new FileInfo(src).Length;
                if (shared) result.SharedImages++; else result.LevelImages++;
            }

            // ---------- 4. 精灵归档 ----------
            var spriteRefLevels = new Dictionary<string, HashSet<string>>();
            foreach (var level in levelDirs)
            {
                var objPath = Path.Combine(level, "objects.json");
                if (!File.Exists(objPath)) continue;
                var names = new HashSet<string>();
                using var doc = JsonDocument.Parse(File.ReadAllText(objPath));
                if (!doc.RootElement.TryGetProperty("objectTypes", out var types)) continue;
                foreach (var prop in types.EnumerateObject())
                {
                    var td = prop.Value;
                    if (!td.TryGetProperty("type", out var tp) || tp.GetInt32() != 2) continue;
                    string? name = td.TryGetProperty("name", out var np) ? np.GetString() : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    names.Add(name);
                }
                foreach (var n in names)
                {
                    if (!spriteRefLevels.TryGetValue(n, out var set))
                        spriteRefLevels[n] = set = new HashSet<string>();
                    set.Add(level);
                }
            }

            var indexPath = Path.Combine(spritesDir, "index.json");
            if (Directory.Exists(spritesDir) && File.Exists(indexPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!spriteRefLevels.TryGetValue(prop.Name, out var levels) || levels.Count == 0)
                    {
                        result.SkippedSprites++;
                        continue;
                    }
                    bool shared = levels.Count >= 2;
                    string dstRoot = shared ? sharedSpritesDir : Path.Combine(levels.First(), "sprites");
                    foreach (var dirEl in prop.Value.EnumerateArray())
                    {
                        string d = dirEl.GetString() ?? "";
                        if (string.IsNullOrEmpty(d)) continue;
                        string src = Path.Combine(spritesDir, d);
                        if (!Directory.Exists(src)) { result.SkippedSprites++; continue; }
                        result.CopiedBytes += CopyDirectory(src, Path.Combine(dstRoot, d));
                        if (shared) result.SharedSprites++; else result.LevelSprites++;
                    }
                }
            }

            return result;
        }

        static string Sanitize(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(invalid.AsSpan().IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);
            var s = sb.ToString().Trim();
            return string.IsNullOrEmpty(s) ? "_" : s;
        }

        static bool FilesEqual(string a, string b)
        {
            try
            {
                byte[] x = File.ReadAllBytes(a);
                byte[] y = File.ReadAllBytes(b);
                return x.AsSpan().SequenceEqual(y);
            }
            catch { return false; }
        }

        static long CopyDirectory(string srcDir, string dstDir)
        {
            Directory.CreateDirectory(dstDir);
            long bytes = 0;
            foreach (var file in Directory.GetFiles(srcDir))
            {
                File.Copy(file, Path.Combine(dstDir, Path.GetFileName(file)), overwrite: true);
                bytes += new FileInfo(file).Length;
            }
            foreach (var sub in Directory.GetDirectories(srcDir))
                bytes += CopyDirectory(sub, Path.Combine(dstDir, Path.GetFileName(sub)));
            return bytes;
        }
    }
}
