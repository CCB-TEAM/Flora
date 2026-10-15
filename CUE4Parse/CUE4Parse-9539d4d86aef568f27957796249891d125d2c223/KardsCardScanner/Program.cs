using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

internal static class Program
{
    private const string CardDirectory = "/content/blueprints/cards/";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        try
        {
            if (args.Any(it => it is "--help" or "-h"))
            {
                PrintHelp();
                return 0;
            }

            var options = ReadOptions(args);
            if (options is null)
                return 2;

            var archiveDirectory = ResolveArchiveDirectory(options.SourcePath);
            Console.WriteLine($"Pak/IoStore 目录: {archiveDirectory}");
            Console.WriteLine($"UE 版本: {options.Game}");

            InitializeCompressionLibraries();

            var version = new VersionContainer(options.Game);
            var provider = new DefaultFileProvider(
                archiveDirectory,
                SearchOption.TopDirectoryOnly,
                version,
                StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(options.MappingPath))
            {
                if (!File.Exists(options.MappingPath))
                    throw new FileNotFoundException("找不到 usmap 映射文件", options.MappingPath);

                Console.WriteLine($"加载映射: {options.MappingPath}");
                provider.MappingsContainer = new FileUsmapTypeMappingsProvider(options.MappingPath);
            }

            Console.WriteLine("正在挂载 Pak/IoStore...");
            provider.Initialize();

            if (!string.IsNullOrWhiteSpace(options.AesKey))
            {
                Console.WriteLine("正在提交 AES Key...");
                var mounted = provider.SubmitKey(new FGuid(), new FAesKey(options.AesKey));
                Console.WriteLine($"AES Key 新挂载归档数: {mounted}");
            }

            provider.PostMount();
            return Scan(provider, options);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"错误: {exception.Message}");
            Console.Error.WriteLine("如果是加密或压缩错误，请确认 AES Key、UE 版本和 usmap 映射文件与游戏版本匹配。");
            return 1;
        }
    }

    private static ScannerOptions? ReadOptions(string[] args)
    {
        var values = ParseArguments(args);
        var interactive = args.Length == 0;
        var source = values.GetValueOrDefault("source") ?? values.GetValueOrDefault("game") ?? values.GetValueOrDefault("pak");
        if (interactive && string.IsNullOrWhiteSpace(source))
            source = Prompt("输入 Pak 文件、Pak 目录或游戏目录");

        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("必须提供 Pak/游戏目录。");
            return null;
        }

        var mapping = values.GetValueOrDefault("mapping");
        if (interactive && mapping is null)
            mapping = PromptOptional("输入 .usmap 映射路径（可留空）");

        var game = values.TryGetValue("ue", out var ueValue)
            ? ParseGameVersion(ueValue)
            : interactive ? PromptGameVersion() : EGame.GAME_UE5_3;

        var aesKey = values.GetValueOrDefault("aes-key") ?? values.GetValueOrDefault("aes");
        if (interactive && aesKey is null)
            aesKey = PromptOptional("输入 AES Key（可留空；支持 0x 前缀）");

        var output = values.GetValueOrDefault("output") ?? values.GetValueOrDefault("out");
        if (interactive && output is null)
            output = PromptOptional("输入输出 JSON 路径", "cards.json");

        if (string.IsNullOrWhiteSpace(output))
            output = "cards.json";

        return new ScannerOptions(source, mapping, game, aesKey, output);
    }

    private static Dictionary<string, string?> ParseArguments(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                continue;

            var separator = argument.IndexOf('=');
            if (separator > 2)
            {
                values[argument[2..separator]] = argument[(separator + 1)..];
                continue;
            }

            var key = argument[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                values[key] = args[++i];
            else
                values[key] = string.Empty;
        }

        return values;
    }

    private static EGame PromptGameVersion()
    {
        var versions = new (string Label, EGame Game)[]
        {
            ("UE4.27", EGame.GAME_UE4_27),
            ("UE5.0", EGame.GAME_UE5_0),
            ("UE5.1", EGame.GAME_UE5_1),
            ("UE5.2", EGame.GAME_UE5_2),
            ("UE5.3", EGame.GAME_UE5_3),
            ("UE5.4", EGame.GAME_UE5_4),
            ("UE5.5", EGame.GAME_UE5_5),
            ("UE5.6", EGame.GAME_UE5_6),
        };

        Console.WriteLine("选择 UE 版本（默认 5 = UE5.3）：");
        for (var i = 0; i < versions.Length; i++)
            Console.WriteLine($"  {i + 1}. {versions[i].Label}");
        Console.WriteLine("  0. 输入 EGame 枚举名，例如 GAME_UE5_3");

        while (true)
        {
            var input = PromptOptional("UE 版本", "5");
            if (int.TryParse(input, out var index) && index >= 1 && index <= versions.Length)
                return versions[index - 1].Game;
            if (input == "0")
            {
                var custom = Prompt("EGame 枚举名");
                try { return ParseGameVersion(custom); }
                catch (Exception exception) { Console.WriteLine($"无法识别: {exception.Message}"); }
            }
            else
            {
                try { return ParseGameVersion(input); }
                catch (Exception exception) { Console.WriteLine($"无法识别: {exception.Message}"); }
            }
        }
    }

    private static EGame ParseGameVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return EGame.GAME_UE5_3;

        value = value.Trim().Trim('"');
        var normalized = value.Replace(".", "_").Replace("-", "_");
        if (normalized.StartsWith("UE", StringComparison.OrdinalIgnoreCase))
            normalized = "GAME_" + normalized;
        if (!normalized.StartsWith("GAME_", StringComparison.OrdinalIgnoreCase))
            normalized = "GAME_" + normalized;

        if (Enum.TryParse<EGame>(normalized, true, out var game))
            return game;

        throw new ArgumentException($"未知 UE 版本：{value}");
    }

    private static void InitializeCompressionLibraries()
    {
        Console.WriteLine("正在初始化压缩库（首次运行可能需要下载运行库）...");
        try
        {
            ZlibHelper.Initialize();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Zlib 初始化失败，遇到 Zlib 压缩资产时可能无法读取：{exception.Message}");
        }

        try
        {
            OodleHelper.Initialize();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"内置 Oodle 初始化失败，尝试下载兼容运行库：{exception.Message}");
            try
            {
                var oodlePath = Path.Combine(AppContext.BaseDirectory, OodleHelper.OODLE_NAME_CURRENT);
                if (OodleHelper.DownloadOodleDll(ref oodlePath))
                {
                    OodleHelper.Initialize(oodlePath);
                    Console.WriteLine($"Oodle 运行库已加载: {oodlePath}");
                }
                else
                {
                    Console.WriteLine("Oodle 运行库下载失败，遇到 Oodle 压缩资产时可能无法读取。");
                }
            }
            catch (Exception fallbackException)
            {
                Console.WriteLine($"Oodle 兼容运行库初始化失败，遇到 Oodle 压缩资产时可能无法读取：{fallbackException.Message}");
            }
        }
    }

    private static int Scan(DefaultFileProvider provider, ScannerOptions options)
    {
        var cardFiles = provider.Files.Values
            .Where(IsCardPackage)
            .OrderBy(it => it.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"发现 {cardFiles.Count} 个卡牌 uasset，开始读取...");

        var cards = new List<CardRecord>(cardFiles.Count);
        var errors = new List<ScanError>();
        var missingFieldWarnings = 0;

        foreach (var (file, index) in cardFiles.Select((file, index) => (file, index + 1)))
        {
            Console.Write($"[{index}/{cardFiles.Count}] {file.NameWithoutExtension}");
            try
            {
                if (!provider.TryLoadPackage(file, out var package) || package is null)
                    throw new InvalidOperationException("无法加载包");

                var exports = LoadExports(package, errors, file.Path);
                var generatedClass = FindGeneratedClass(exports, file.NameWithoutExtension);
                if (generatedClass is null)
                    throw new InvalidOperationException("包内没有找到 BlueprintGeneratedClass");

                var cdo = generatedClass.ClassDefaultObject.Load();
                if (cdo is null)
                    throw new InvalidOperationException("无法加载 ClassDefaultObject");

                var record = BuildCardRecord(file, generatedClass, cdo);
                missingFieldWarnings += record.Warnings.Count;
                cards.Add(record);
                Console.WriteLine(" ✓");
            }
            catch (Exception exception)
            {
                errors.Add(new ScanError(file.Path, exception.Message));
                Console.WriteLine($" × {exception.Message}");
            }
        }

        var result = new ScanOutput
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourcePath = options.SourcePath,
            ArchiveDirectory = provider.Files.Count == 0 ? null : Path.GetFullPath(ResolveArchiveDirectory(options.SourcePath)),
            UnrealEngineVersion = options.Game.ToString(),
            MappingPath = string.IsNullOrWhiteSpace(options.MappingPath) ? null : Path.GetFullPath(options.MappingPath),
            Cards = cards,
            Errors = errors,
        };

        var outputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, jsonOptions), new UTF8Encoding(false));

        Console.WriteLine();
        Console.WriteLine($"完成：成功 {cards.Count}，失败 {errors.Count}，字段提示 {missingFieldWarnings}。");
        if (cardFiles.Count == 0)
            Console.WriteLine("没有发现目标卡牌文件；请检查 Pak 是否已解密、UE 版本是否匹配，以及路径是否包含 kards/Content/Blueprints/Cards。");
        Console.WriteLine($"JSON 已写入: {outputPath}");
        return cards.Count > 0 ? 0 : 1;
    }

    private static List<UObject> LoadExports(IPackage package, List<ScanError> errors, string sourcePath)
    {
        var exports = new List<UObject>();
        foreach (var lazyExport in package.ExportsLazy)
        {
            try
            {
                exports.Add(lazyExport.Value);
            }
            catch (Exception exception)
            {
                errors.Add(new ScanError(sourcePath, $"某个 export 加载失败：{exception.Message}"));
            }
        }

        return exports;
    }

    private static UClass? FindGeneratedClass(IEnumerable<UObject> exports, string assetName)
    {
        var exportList = exports.ToList();
        var expectedClassName = assetName + "_C";

        var generated = exportList
            .OfType<UBlueprintGeneratedClass>()
            .FirstOrDefault(it => it.Name.Equals(expectedClassName, StringComparison.OrdinalIgnoreCase))
            ?? exportList.OfType<UBlueprintGeneratedClass>().FirstOrDefault();
        if (generated is not null)
            return generated;

        foreach (var blueprint in exportList.OfType<UBlueprintCore>())
        {
            try
            {
                var generatedClass = blueprint.GeneratedClass?.Load<UClass>();
                if (generatedClass is not null)
                    return generatedClass;
            }
            catch
            {
                // Try the generic UClass fallback below.
            }
        }

        return exportList
            .OfType<UClass>()
            .FirstOrDefault(it => it.Name.Equals(expectedClassName, StringComparison.OrdinalIgnoreCase))
            ?? exportList.OfType<UClass>().FirstOrDefault(it =>
                it.ExportType.Contains("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase));
    }

    private static CardRecord BuildCardRecord(GameFile file, UClass generatedClass, UObject cdo)
    {
        var allProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in cdo.Properties)
        {
            if (property.Tag?.GenericValue is null)
                continue;

            var key = property.Name.Text;
            if (allProperties.ContainsKey(key))
                key = $"{key}[{property.ArrayIndex}]";
            allProperties[key] = FormatValue(property.Tag.GenericValue);
        }

        var (displayName, _) = ReadMappedValue(cdo, "DisplayName", "CardName", "Title");
        var (rarity, rarityFound) = ReadMappedValue(cdo, "Rarity", "CardRarity", "RarityType");
        var (cardSet, cardSetFound) = ReadMappedValue(cdo, "CardSet", "Card_Set", "Set", "SetName");
        var (type, typeFound) = ReadMappedValue(cdo, "Type", "CardType", "Card_Type");

        var warnings = new List<string>();
        if (!rarityFound) warnings.Add("未找到 Rarity 候选字段");
        if (!cardSetFound) warnings.Add("未找到 CardSet 候选字段");
        if (!typeFound) warnings.Add("未找到 Type 候选字段");

        return new CardRecord
        {
            Name = file.NameWithoutExtension,
            Rarity = rarity,
            CardSet = cardSet,
            Type = type,
            Source = file.Path,
            ExportName = generatedClass.Name,
            DisplayName = displayName,
            Properties = allProperties,
            Warnings = warnings,
        };
    }

    private static (string? Value, bool Found) ReadMappedValue(UObject cdo, params string[] candidates)
    {
        var candidateNames = candidates.Select(NormalizeName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in cdo.Properties)
        {
            if (!candidateNames.Contains(NormalizeName(property.Name.Text)) || property.Tag?.GenericValue is null)
                continue;
            return (FormatValue(property.Tag.GenericValue), true);
        }

        return (null, false);
    }

    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
                builder.Append(char.ToUpperInvariant(character));
        }

        return builder.ToString();
    }

    private static string? FormatValue(object? value)
    {
        return value switch
        {
            null => null,
            FName name => name.Text,
            FText text => text.Text,
            FPackageIndex packageIndex => packageIndex.ResolvedObject?.GetPathName() ?? packageIndex.ToString(),
            string text => text,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
    }

    private static bool IsCardPackage(GameFile file)
    {
        if (!file.IsUePackage || !file.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase))
            return false;

        var normalizedPath = "/" + file.Path.Replace('\\', '/').TrimStart('/').ToLowerInvariant() + "/";
        return normalizedPath.Contains(CardDirectory, StringComparison.Ordinal) &&
               file.NameWithoutExtension.StartsWith("card_", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveArchiveDirectory(string sourcePath)
    {
        var cleanPath = sourcePath.Trim().Trim('"');
        if (File.Exists(cleanPath))
        {
            if (!cleanPath.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) &&
                !cleanPath.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输入文件必须是 .pak 或 .utoc");
            return Path.GetDirectoryName(Path.GetFullPath(cleanPath))!;
        }

        if (!Directory.Exists(cleanPath))
            throw new DirectoryNotFoundException($"找不到输入路径: {cleanPath}");

        var directory = Path.GetFullPath(cleanPath);
        if (HasArchive(directory))
            return directory;

        var candidates = new[]
        {
            Path.Combine(directory, "Paks"),
            Path.Combine(directory, "Content", "Paks"),
            Path.Combine(directory, "kards", "Content", "Paks"),
        };
        foreach (var candidate in candidates)
        {
            if (HasArchive(candidate))
                return candidate;
        }

        try
        {
            var nestedArchive = Directory.EnumerateFiles(directory, "*.pak", SearchOption.AllDirectories).FirstOrDefault()
                ?? Directory.EnumerateFiles(directory, "*.utoc", SearchOption.AllDirectories).FirstOrDefault();
            if (nestedArchive is not null)
                return Path.GetDirectoryName(nestedArchive)!;
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to the clearer error below.
        }

        throw new DirectoryNotFoundException($"目录中没有找到 .pak 或 .utoc: {directory}");
    }

    private static bool HasArchive(string directory)
    {
        if (!Directory.Exists(directory))
            return false;
        return Directory.EnumerateFiles(directory, "*.pak", SearchOption.TopDirectoryOnly).Any() ||
               Directory.EnumerateFiles(directory, "*.utoc", SearchOption.TopDirectoryOnly).Any();
    }

    private static string Prompt(string label)
    {
        Console.Write($"{label}: ");
        return Console.ReadLine()?.Trim().Trim('"') ?? string.Empty;
    }

    private static string? PromptOptional(string label, string? defaultValue = null)
    {
        Console.Write(defaultValue is null ? $"{label}: " : $"{label} [{defaultValue}]: ");
        var value = Console.ReadLine()?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("KardsCardScanner - 扫描 KARDS 卡牌蓝图并导出 JSON");
        Console.WriteLine();
        Console.WriteLine("无参数运行会进入交互式 CLI。也可以使用参数：");
        Console.WriteLine("  --source <路径>       Pak 文件、Pak 目录或游戏目录");
        Console.WriteLine("  --mapping <路径>      .usmap 映射文件，可省略");
        Console.WriteLine("  --ue <版本>           GAME_UE5_3、UE5.3 等");
        Console.WriteLine("  --aes-key <密钥>      AES Key，可省略");
        Console.WriteLine("  --output <路径>       输出 JSON，默认 cards.json");
        Console.WriteLine();
        Console.WriteLine("示例：dotnet run -- --source D:\\KARDS\\default\\game\\kards --ue UE5.3 --output cards.json");
    }
}

internal sealed record ScannerOptions(
    string SourcePath,
    string? MappingPath,
    EGame Game,
    string? AesKey,
    string OutputPath);

internal sealed class ScanOutput
{
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public string? ArchiveDirectory { get; init; }
    public string UnrealEngineVersion { get; init; } = string.Empty;
    public string? MappingPath { get; init; }
    public List<CardRecord> Cards { get; init; } = [];
    public List<ScanError> Errors { get; init; } = [];
}

internal sealed class CardRecord
{
    public string Name { get; init; } = string.Empty;
    public string? Rarity { get; init; }
    public string? CardSet { get; init; }
    public string? Type { get; init; }
    public string Source { get; init; } = string.Empty;
    public string ExportName { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public Dictionary<string, string?> Properties { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; init; } = [];
}

internal sealed record ScanError(string Source, string Message);
