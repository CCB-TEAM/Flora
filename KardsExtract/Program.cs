// 无头提取工具：从 KARDS 的 Pak 中列出 / 导出 JSON / 导出贴图 PNG。
// 设置来自 FModel 的 AppSettings_Debug.json（AES key 只在内存中使用，不会打印）。
//   dotnet run -c Release -- list <regex>
//   dotnet run -c Release -- json <outDir> <path|regex>...
//   dotnet run -c Release -- tex  <outDir> <path|regex>...
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using Newtonsoft.Json;
using SkiaSharp;

if (args.Length < 2) { Console.WriteLine("usage: list <regex> | json <outDir> <pat>... | tex <outDir> <pat>..."); return 1; }

var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel", "AppSettings_Debug.json");
using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
var per = doc.RootElement.GetProperty("PerDirectory").EnumerateObject().First().Value;
var gameDir = per.GetProperty("GameDirectory").GetString()!;
var usmap = per.GetProperty("Endpoints")[1].GetProperty("FilePath").GetString()!;
var aes = per.GetProperty("AesKeys").GetProperty("mainKey").GetString()!;
var data = Path.Combine(doc.RootElement.GetProperty("OutputDirectory").GetString()!, ".data");

try { ZlibHelper.Initialize(Path.Combine(data, "zlib-ng2.dll")); } catch (Exception e) { Console.Error.WriteLine("zlib: " + e.Message); }
try { OodleHelper.Initialize(Path.Combine(data, "oo2core_9_win64.dll")); } catch (Exception e) { Console.Error.WriteLine("oodle: " + e.Message); }

var provider = new DefaultFileProvider(gameDir, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_UE5_6), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);
provider.ReadScriptData = true; provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(aes));
provider.PostMount();
Console.Error.WriteLine($"mounted files: {provider.Files.Count}");

var cmd = args[0];
if (cmd == "list")
{
    var rx = new Regex(args[1], RegexOptions.IgnoreCase);
    foreach (var p in provider.Files.Keys.Where(k => rx.IsMatch(k)).OrderBy(k => k)) Console.WriteLine(p);
    return 0;
}

if (cmd == "loc")
{
    // loc <locresPath> <out.json>
    if (!provider.TryCreateReader(args[1], out var rd)) { Console.Error.WriteLine("locres not found"); return 2; }
    var res = new CUE4Parse.UE4.Localization.FTextLocalizationResource(rd);
    var o = new Dictionary<string, Dictionary<string, string>>();
    foreach (var (ns, d) in res.Entries)
        o[ns.Str] = d.ToDictionary(kv => kv.Key.Str, kv => kv.Value.LocalizedString);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllText(args[2], JsonConvert.SerializeObject(o, Formatting.Indented));
    Console.Error.WriteLine($"namespaces={o.Count} entries={o.Values.Sum(x => x.Count)}");
    return 0;
}
var outDir = args[1];
var pats = args.Skip(2).Select(a => new Regex(a, RegexOptions.IgnoreCase)).ToList();
var targets = provider.Files.Keys.Where(k => (cmd == "raw" ? !k.EndsWith(".uexp") && !k.EndsWith(".ubulk") : k.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) && pats.Any(r => r.IsMatch(k))).OrderBy(k => k).ToList();
Console.Error.WriteLine($"targets: {targets.Count}");
int ok = 0, fail = 0;
foreach (var path in targets)
{
    try
    {
        var rel = Path.ChangeExtension(path, null);
        if (cmd == "raw")
        {
            var dst = Path.Combine(outDir, path); Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, provider.SaveAsset(path)); ok++; continue;
        }
        if (cmd == "json")
        {
            var objs = provider.LoadPackage(path).GetExports();
            var dst = Path.Combine(outDir, rel + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllText(dst, JsonConvert.SerializeObject(objs, Formatting.Indented));
        }
        else if (cmd == "tex")
        {
            var tex = provider.LoadPackage(path).GetExports().OfType<UTexture>().FirstOrDefault();
            if (tex == null) throw new Exception("no texture export");
            var bmp = tex.Decode();
            if (bmp == null) throw new Exception("decode failed");
            var png = bmp.Encode(ETextureFormat.Png, false, out _);
            var dst = Path.Combine(outDir, rel + ".png"); Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.WriteAllBytes(dst, png);
        }
        ok++;
    }
    catch (Exception e) { fail++; Console.Error.WriteLine($"FAIL {path}: {e.Message}"); }
}
Console.Error.WriteLine($"done ok={ok} fail={fail}");
return 0;
