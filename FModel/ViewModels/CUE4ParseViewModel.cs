using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows;
using AdonisUI.Controls;
using CUE4Parse;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.GameTypes.AoC.Objects;
using CUE4Parse.GameTypes.AshEchoes.FileProvider;
using CUE4Parse.GameTypes.Borderlands3.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Assets.Exports;
using CUE4Parse.GameTypes.Borderlands4.Wwise;
using CUE4Parse.GameTypes.KRD.Assets.Exports;
using CUE4Parse.GameTypes.RocoKingdomWorld.Assets.Objects;
using CUE4Parse.GameTypes.SMG.UE4.Assets.Exports.Wwise;
using CUE4Parse.GameTypes.SquareEnix.UE4.Assets.Exports;
using CUE4Parse.MappingsProvider;
using CUE4Parse.MappingsProvider.Jmap;
using FModel.Framework.Mappings;
using CUE4Parse.UE4.AssetRegistry;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.CriWare;
using CUE4Parse.UE4.Assets.Exports.Fmod;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Sound;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.Verse;
using CUE4Parse.UE4.Assets.Exports.Wwise;
using CUE4Parse.UE4.BinaryConfig;
using CUE4Parse.UE4.CriWare;
using CUE4Parse.UE4.CriWare.Readers;
using CUE4Parse.UE4.FMod;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Serialization;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Objects.UObject.Editor;
using CUE4Parse.UE4.Oodle.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;
using CUE4Parse.UE4.Wwise;
using CUE4Parse.Utils;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Sounds;
using EpicManifestParser;
using EpicManifestParser.UE;
using EpicManifestParser.ZlibngDotNetDecompressor;
using FModel.Creator;
using FModel.Extensions;
using FModel.Framework;
using FModel.Services;
using FModel.Settings;
using FModel.Views;
using FModel.Views.Resources.Controls;
using FModel.Views.Snooper;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Serilog;
using SkiaSharp;
using Svg.Skia;
using UAssetAPI.Kismet.Bytecode;
using UAssetApiAsset = UAssetAPI.UAsset;
using UAssetApiUsmap = UAssetAPI.Unversioned.Usmap;
using UE4Config.Parsing;
using Application = System.Windows.Application;
using FGuid = CUE4Parse.UE4.Objects.Core.Misc.FGuid;
using FModel.Framework.Docking;

namespace FModel.ViewModels;

public class CUE4ParseViewModel : ViewModel
{
    private ThreadWorkerViewModel _threadWorkerView => ApplicationService.ThreadWorkerView;
    private ApiEndpointViewModel _apiEndpointView => ApplicationService.ApiEndpointView;
    private readonly Regex _fnLiveRegex = new(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoStatementRegex = new(@"\bstmt:(?<idx>\d+)\b",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JsonStatementRegex = new(@"""StatementIndex""\s*:\s*(?<idx>\d+)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoFunctionDeclarationRegex = new(
        @"\b(?:public|private|protected)\s+[^\r\n;{}]+\s+(?<name>[A-Za-z_]\w*)\s*\(",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoFunctionSignatureLineRegex = new(
        @"^\s*(?:[\w:<>,~\[\]\*&]+\s+)+(?<name>[A-Za-z_]\w*)\s*\([^;{}]*\)\s*(?:\{)?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoEditableJumpRegex = new(
        @"\bLabel_(?<target>\d+)\b.*//\s*\[stmt:(?<stmt>\d+)\s+token:(?<token>[A-Za-z0-9_]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PseudoTokenRegex = new(@"\btoken:(?<token>[A-Za-z0-9_]+)\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const string PseudoUapiPatchMarker = "@uapi:";
    private static readonly HashSet<string> NonEditableScriptMetadataKeys = new(StringComparer.Ordinal)
    {
        "StatementIndex",
        "ExportArrayIndex",
        "offset",
        "size"
    };
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _livePseudoPreviewCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _livePseudoPreviewVersion = new(StringComparer.OrdinalIgnoreCase);
    private const int LivePseudoPreviewDebounceMs = 700;

    private bool _modelIsOverwritingMaterial;
    public bool ModelIsOverwritingMaterial
    {
        get => _modelIsOverwritingMaterial;
        set => SetProperty(ref _modelIsOverwritingMaterial, value);
    }

    private bool _modelIsWaitingAnimation;
    public bool ModelIsWaitingAnimation
    {
        get => _modelIsWaitingAnimation;
        set => SetProperty(ref _modelIsWaitingAnimation, value);
    }

    public bool IsSnooperOpen => _snooper is { Exists: true, IsVisible: true };
    private Snooper _snooper;
    public Snooper SnooperViewer
    {
        get
        {
            if (_snooper != null) return _snooper;

            return Application.Current.Dispatcher.Invoke(delegate
            {
                var scale = ImGuiController.GetDpiScale();
                var htz = Snooper.GetMaxRefreshFrequency();
                return _snooper = new Snooper(
                    new GameWindowSettings { UpdateFrequency = htz },
                    new NativeWindowSettings
                    {
                        ClientSize = new OpenTK.Mathematics.Vector2i(
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenWidth * .75 * scale),
                            Convert.ToInt32(SystemParameters.MaximizedPrimaryScreenHeight * .85 * scale)),
                        NumberOfSamples = Constants.SAMPLES_COUNT,
                        WindowBorder = WindowBorder.Resizable,
                        Flags = ContextFlags.ForwardCompatible,
                        Profile = ContextProfile.Core,
                        Vsync = VSyncMode.Adaptive,
                        APIVersion = new Version(4, 6),
                        StartVisible = false,
                        StartFocused = false,
                        Title = "3D Viewer"
                    });
            });
        }
    }

    public AbstractVfsFileProvider Provider { get; }
    public GameDirectoryViewModel GameDirectory { get; }
    public AssetsFolderViewModel AssetsFolder { get; }
    public SearchViewModel SearchVm { get; }
    public SearchViewModel RefVm { get; }
    public TabControlViewModel TabControl { get; }
    public ConfigIni IoStoreOnDemand { get; }
    private Lazy<WwiseProvider> _wwiseProviderLazy;
    public WwiseProvider WwiseProvider => _wwiseProviderLazy.Value;
    private Lazy<FModProvider> _fmodProviderLazy;
    public FModProvider FmodProvider => _fmodProviderLazy?.Value;
    private Lazy<CriWareProvider> _criWareProviderLazy;
    public CriWareProvider CriWareProvider => _criWareProviderLazy?.Value;
    public ConcurrentBag<string> UnknownExtensions = [];

    public int ExportedCount;
    public int FailedExportCount;

    public CUE4ParseViewModel()
    {
        var currentDir = UserSettings.Default.CurrentDir;
        var gameDirectory = currentDir.GameDirectory;
        var versionContainer = new VersionContainer(
            game: currentDir.UeVersion, platform: currentDir.TexturePlatform,
            customVersions: new FCustomVersionContainer(currentDir.Versioning.CustomVersions),
            optionOverrides: currentDir.Versioning.Options,
            mapStructTypesOverrides: currentDir.Versioning.MapStructTypes);
        var pathComparer = StringComparer.OrdinalIgnoreCase;

        switch (gameDirectory)
        {
            case Constants._FN_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("FortniteLive", versionContainer, pathComparer);
                break;
            }
            case Constants._VAL_LIVE_TRIGGER:
            {
                Provider = new StreamedFileProvider("ValorantLive", versionContainer, pathComparer);
                break;
            }
            default:
            {
                var project = gameDirectory.SubstringBeforeLast(gameDirectory.Contains("eFootball") ? "\\pak" : "\\Content").SubstringAfterLast("\\");
                Provider = project switch
                {
                    "StateOfDecay2" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\Paks"),
                        new(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\StateOfDecay2\\Saved\\DisabledPaks")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    "eFootball" => new DefaultFileProvider(new DirectoryInfo(gameDirectory),
                    [
                        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\KONAMI\\eFootball\\ST\\Download")
                    ], SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_AshEchoes => new AEDefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer),
                    _ when versionContainer.Game is EGame.GAME_BlackStigma => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, StringComparer.Ordinal),
                    _ => new DefaultFileProvider(gameDirectory, SearchOption.AllDirectories, versionContainer, pathComparer)
                };

                break;
            }
        }

        Provider.ReadScriptData = UserSettings.Default.ReadScriptData;
        Provider.ReadShaderMaps = UserSettings.Default.ReadShaderMaps;
        Provider.ReadNaniteData = true;

        GameDirectory = new GameDirectoryViewModel();
        AssetsFolder = new AssetsFolderViewModel();
        SearchVm = new SearchViewModel();
        RefVm = new SearchViewModel();
        TabControl = new TabControlViewModel();
        IoStoreOnDemand = new ConfigIni(nameof(IoStoreOnDemand));
    }

    public async Task Initialize()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            Provider.OnDemandOptions = new IoStoreOnDemandOptions
            {
                ChunkHostUri = new Uri("https://download.epicgames.com/", UriKind.Absolute),
                ChunkCacheDirectory = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")),
                Timeout = TimeSpan.FromSeconds(30)
            };

            switch (Provider)
            {
                case StreamedFileProvider p:
                    switch (p.LiveGame)
                    {
                        case "FortniteLive":
                        {
                            var manifestInfo = _apiEndpointView.EpicApi.GetManifest(cancellationToken);
                            if (manifestInfo is null)
                            {
                                throw new FileLoadException("Could not load latest Fortnite manifest, you may have to switch to your local installation.");
                            }

                            var cacheDir = Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, ".data")).FullName;
                            var manifestOptions = new ManifestParseOptions
                            {
                                ChunkCacheDirectory = cacheDir,
                                ManifestCacheDirectory = cacheDir,
                                ChunkBaseUrl = "http://download.epicgames.com/Builds/Fortnite/CloudDir/",
                                Decompressor = ManifestZlibngDotNetDecompressor.Decompress,
                                DecompressorState = ZlibHelper.Instance,
                                CacheChunksAsIs = false
                            };

                            var startTs = Stopwatch.GetTimestamp();
                            FBuildPatchAppManifest manifest;

                            try
                            {
                                (manifest, _) = manifestInfo.DownloadAndParseAsync(manifestOptions,
                                    cancellationToken: cancellationToken,
                                    elementManifestPredicate: static x => x.Uri.Host == "download.epicgames.com"
                                ).GetAwaiter().GetResult();
                            }
                            catch (HttpRequestException ex)
                            {
                                Log.Error("Failed to download manifest ({ManifestUri})", ex.Data["ManifestUri"]?.ToString() ?? "");
                                throw;
                            }

                            if (manifest.TryFindFile("Cloud/IoStoreOnDemand.ini", out var ioStoreOnDemandFile))
                            {
                                IoStoreOnDemand.Read(new StreamReader(ioStoreOnDemandFile.GetStream()));
                            }

                            Parallel.ForEach(manifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                            {
                                p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                    it => new FRandomAccessStreamArchive(it, manifest.FindFile(it)!.GetStream(), p.Versions));
                            });

                            var manifests = _apiEndpointView.DillyApi.GetManifests(cancellationToken);
                            var downloadUrl = manifests.First(x => x.AppName == "Fortnite_Studio").DownloadUrl;

                            using var client = new HttpClient();
                            var manifestBytes = client.GetByteArrayAsync(downloadUrl).GetAwaiter().GetResult();

                            var uefnManifest = FBuildPatchAppManifest.Deserialize(manifestBytes, manifestOptions);

                            Parallel.ForEach(uefnManifest.Files.Where(x => _fnLiveRegex.IsMatch(x.FileName)), fileManifest =>
                            {
                                p.RegisterVfs(fileManifest.FileName, [fileManifest.GetStream()],
                                    it => new FRandomAccessStreamArchive(it, uefnManifest.FindFile(it)!.GetStream(), p.Versions));
                            });

                            var elapsedTime = Stopwatch.GetElapsedTime(startTs);
                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Fortnite [LIVE] has been loaded successfully in {elapsedTime.TotalMilliseconds:F1}ms", Constants.WHITE, true));
                            break;
                        }
                        case "ValorantLive":
                        {
                            var manifest = _apiEndpointView.ValorantApi.GetManifest(cancellationToken);
                            if (manifest == null)
                            {
                                throw new Exception("Could not load latest Valorant manifest, you may have to switch to your local installation.");
                            }

                            Parallel.ForEach(manifest.Paks, pak =>
                            {
                                p.RegisterVfs(pak.GetFullName(), [pak.GetStream(manifest)]);
                            });

                            FLogger.Append(ELog.Information, () =>
                                FLogger.Text($"Valorant '{manifest.Header.GameVersion}' has been loaded successfully", Constants.WHITE, true));
                            break;
                        }
                    }

                    break;
                case DefaultFileProvider:
                {
                    var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud\\IoStoreOnDemand.ini");
                    if (File.Exists(ioStoreOnDemandPath))
                    {
                        using var s = new StreamReader(ioStoreOnDemandPath);
                        IoStoreOnDemand.Read(s);
                    }
                    break;
                }
            }

            Provider.Initialize();
            _wwiseProviderLazy = new Lazy<WwiseProvider>(() => new WwiseProvider(Provider, UserSettings.Default.GameDirectory));
            _fmodProviderLazy = new Lazy<FModProvider>(() => new FModProvider(Provider, UserSettings.Default.GameDirectory));
            _criWareProviderLazy = new Lazy<CriWareProvider>(() => new CriWareProvider(Provider, UserSettings.Default.GameDirectory));
            Log.Information($"{Provider.Versions.Game} ({Provider.Versions.Platform}) | Archives: x{Provider.UnloadedVfs.Count} | AES: x{Provider.RequiredKeys.Count} | Loose Files: x{Provider.Files.Count}");
        });
    }

    /// <summary>
    /// load virtual files system from GameDirectory
    /// </summary>
    /// <returns></returns>
    public void LoadVfs(IEnumerable<KeyValuePair<FGuid, FAesKey>> aesKeys)
    {
        Provider.SubmitKeys(aesKeys);
        Provider.PostMount();

        var aesMax = Provider.RequiredKeys.Count + Provider.Keys.Count;
        var archiveMax = Provider.UnloadedVfs.Count + Provider.MountedVfs.Count;
        Log.Information($"Project: {Provider.ProjectName} | Mounted: {Provider.MountedVfs.Count}/{archiveMax} | AES: {Provider.Keys.Count}/{aesMax} | Files: x{Provider.Files.Count}");
    }

    public void ClearProvider()
    {
        if (Provider == null) return;

        AssetsFolder.Folders.Clear();
        SearchVm.SearchResults.Clear();
        DockPanels.CloseSearch();
        Provider.UnloadNonStreamedVfs();
        GC.Collect();
    }

    public async Task RefreshAes()
    {
        // game directory dependent, we don't have the provider game name yet since we don't have aes keys
        // except when this comes from the AES Manager
        if (!UserSettings.IsEndpointValid(EEndpointType.Aes, out var endpoint))
            return;

        await _threadWorkerView.Begin(cancellationToken =>
        {
            // deprecated values
            if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/aes") endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/aes";

            var aes = _apiEndpointView.DynamicApi.GetAesKeys(cancellationToken, endpoint.Url, endpoint.Path);
            if (aes is not { IsValid: true }) return;

            UserSettings.Default.CurrentDir.AesKeys = aes;
        });
    }

    public async Task InitInformation()
    {
        await _threadWorkerView.Begin(cancellationToken =>
        {
            var info = _apiEndpointView.FModelApi.GetNews(cancellationToken, Provider.ProjectName);
            if (info == null) return;

            FLogger.Append(ELog.None, () =>
            {
                for (var i = 0; i < info.Messages.Length; i++)
                {
                    FLogger.Text(info.Messages[i], info.Colors[i], bool.Parse(info.NewLines[i]));
                }
            });
        });
    }

    public Task InitMappings(bool force = false)
    {
        if (!UserSettings.IsEndpointValid(EEndpointType.Mapping, out var endpoint))
        {
            Provider.MappingsContainer = null;
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            var l = ELog.Information;
            if (endpoint.Overwrite && File.Exists(endpoint.FilePath))
            {
                Provider.MappingsContainer = CreateMappingsProvider(endpoint.FilePath);
            }
            else if (endpoint.IsValid)
            {
                // deprecated values
                if (endpoint.Path == "$.[?(@.meta.compressionMethod=='Oodle')].['url','fileName']") endpoint.Path = "$.[0].['url','fileName']";
                if (endpoint.Url == "https://fortnitecentral.genxgames.gg/api/v1/mappings")
                {
                    endpoint.Url = "https://uedb.dev/svc/api/v1/fortnite/mappings";
                    endpoint.Path = "$.mappings.ZStandard";
                }

                var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
                var mappings = _apiEndpointView.DynamicApi.GetMappings(CancellationToken.None, endpoint.Url, endpoint.Path);
                if (mappings is { Length: > 0 })
                {
                    foreach (var mapping in mappings)
                    {
                        if (!mapping.IsValid) continue;

                        var mappingPath = Path.Combine(mappingsFolder, mapping.FileName);
                        if (force || !File.Exists(mappingPath) || new FileInfo(mappingPath).Length == 0)
                        {
                            _apiEndpointView.DownloadFile(mapping.Url, mappingPath);
                        }

                        Provider.MappingsContainer = CreateMappingsProvider(mappingPath);
                        break;
                    }
                }

                if (Provider.MappingsContainer == null)
                {
                    var latestMappings = new DirectoryInfo(mappingsFolder).GetFiles("*_oo.usmap")
                        .Concat(new DirectoryInfo(mappingsFolder).GetFiles("*.jmap*").Where(f => FileJmapTypeMappingsProvider.IsJmapFile(f.Name)))
                        .ToArray();
                    if (latestMappings.Length <= 0) return;

                    var latestMappingInfo = latestMappings.OrderBy(f => f.LastWriteTime).Last();
                    Provider.MappingsContainer = CreateMappingsProvider(latestMappingInfo.FullName);
                    l = ELog.Warning;
                }
            }

            if (GetMappingsFilePath(Provider.MappingsContainer) is { } mappingsPath)
            {
                var fileName = Path.GetFileName(mappingsPath);
                Log.Information($"Mappings pulled from '{fileName}'");
                FLogger.Append(l, () => FLogger.Text($"Mappings pulled from '{fileName}'", Constants.WHITE, true));
            }
        });
    }

    public static AbstractTypeMappingsProvider CreateMappingsProvider(string path)
    {
        return FileJmapTypeMappingsProvider.IsJmapFile(path)
            ? new FileJmapTypeMappingsProvider(path)
            : new FileUsmapTypeMappingsProvider(path);
    }

    public static string GetMappingsFilePath(ITypeMappingsProvider mappingsProvider) => mappingsProvider switch
    {
        FileUsmapTypeMappingsProvider usmap => usmap.FilePath,
        FileJmapTypeMappingsProvider jmap => jmap.FilePath,
        _ => null
    };

    /// <summary>
    /// Path of the currently loaded .jmap mappings, or null when mappings come from a .usmap (or none are loaded).
    /// </summary>
    public string JmapPath => Provider.MappingsContainer is FileJmapTypeMappingsProvider jmap ? jmap.FilePath : null;

    public Task VerifyConsoleVariables()
    {
        if (Provider.Versions["StripAdditiveRefPose"])
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text("Additive animations have their reference pose stripped, which will lead to inaccurate preview and export", Constants.WHITE, true));
        }

        if (Provider.Versions.Game is EGame.GAME_UE4_LATEST or EGame.GAME_UE5_LATEST && !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase)) // ignore fortnite globally
        {
            FLogger.Append(ELog.Warning, () =>
                FLogger.Text($"Experimental UE version selected, likely unsuitable for '{Provider.GameDisplayName ?? Provider.ProjectName}'", Constants.WHITE, true));
        }

        return Task.CompletedTask;
    }

    public Task VerifyOnDemandArchives()
    {
        // only local fortnite
        if (Provider is not DefaultFileProvider || !Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        // scuffed but working
        var persistentDownloadDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame/Saved/PersistentDownloadDir");
        var iasFileInfo = new FileInfo(Path.Combine(persistentDownloadDir, "ias", "ias.cache.0"));
        if (!iasFileInfo.Exists || iasFileInfo.Length == 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            var inst = new List<InstructionToken>();
            IoStoreOnDemand.FindPropertyInstructions("Endpoint", "TocPath", inst);
            if (inst.Count <= 0) return;

            var ioStoreOnDemandPath = Path.Combine(UserSettings.Default.GameDirectory, "..\\..\\..\\Cloud", inst[0].Value.SubstringAfterLast("/").SubstringBefore("\""));
            if (!File.Exists(ioStoreOnDemandPath)) return;

            await Provider.RegisterVfsAsync(new IoChunkToc(ioStoreOnDemandPath, Provider.Versions));
            var onDemandCount = await Provider.MountAsync();
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{onDemandCount} on-demand archive{(onDemandCount > 1 ? "s" : "")} streamed via epicgames.com", Constants.WHITE, true));
        });
    }

    public int LocalizedResourcesCount { get; set; }
    public bool LocalResourcesDone { get; set; }
    public bool HotfixedResourcesDone { get; set; }

    public async Task LoadLocalizedResources()
    {
        var snapshot = LocalizedResourcesCount;
        await Task.WhenAll(LoadGameLocalizedResources(), LoadHotfixedLocalizedResources()).ConfigureAwait(false);

        LocalizedResourcesCount = Provider.Internationalization.Count;
        if (snapshot != LocalizedResourcesCount)
        {
            FLogger.Append(ELog.Information, () =>
                FLogger.Text($"{LocalizedResourcesCount} localized resources loaded for '{UserSettings.Default.AssetLanguage.GetDescription()}'", Constants.WHITE, true));
            Utils.Typefaces = new Typefaces(this);
        }
    }

    private Task LoadGameLocalizedResources()
    {
        if (LocalResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var culture = Provider.GetLanguageCode(UserSettings.Default.AssetLanguage);
            LocalResourcesDone = Provider.TryChangeCulture(culture);
            if (Provider.Internationalization.Count > 0)
                return;

            // games name their culture folders differently (KARDS: zh-Hans/zh-CN for "zh"), try the regional variants
            foreach (var alternate in AlternateCultures(culture))
            {
                if (Provider.TryChangeCulture(alternate) && Provider.Internationalization.Count > 0)
                {
                    Log.Information("Localized resources: no '{Culture}' folder, using '{Alternate}'", culture, alternate);
                    LocalResourcesDone = true;
                    return;
                }
            }
        });
    }

    /// <summary>Culture folders of the game's .locres files that are regional variants of <paramref name="culture"/>.</summary>
    private IEnumerable<string> AlternateCultures(string culture)
    {
        var language = culture.Contains('-') ? culture[..culture.IndexOf('-')] : culture;
        var cultures = Provider.Files.Keys
            .Where(path => path.EndsWith(".locres", StringComparison.OrdinalIgnoreCase))
            .Select(path => path.Split('/'))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[^2])
            .Where(folder => !folder.Equals(culture, StringComparison.OrdinalIgnoreCase) &&
                             (folder.Equals(language, StringComparison.OrdinalIgnoreCase) ||
                              folder.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // simplified Chinese is the usual meaning of a bare "zh"
        return cultures.OrderBy(folder => folder.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ? 0 : 1);
    }

    private Task LoadHotfixedLocalizedResources()
    {
        if (!Provider.ProjectName.Equals("fortnitegame", StringComparison.OrdinalIgnoreCase) || HotfixedResourcesDone) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var hotfixes = ApplicationService.ApiEndpointView.DillyApi.GetHotfixes(CancellationToken.None, Provider.GetLanguageCode(UserSettings.Default.AssetLanguage));
            if (hotfixes == null) return;

            Provider.Internationalization.Override(hotfixes);
            HotfixedResourcesDone = true;
        });
    }

    private int _virtualPathCount { get; set; }
    public Task LoadVirtualPaths()
    {
        if (_virtualPathCount > 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            _virtualPathCount = Provider.LoadVirtualPaths(UserSettings.Default.CurrentDir.UeVersion.GetVersion());
            if (_virtualPathCount > 0)
            {
                FLogger.Append(ELog.Information, () =>
                    FLogger.Text($"{_virtualPathCount} virtual paths loaded", Constants.WHITE, true));
            }
            else
            {
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text("Could not load virtual paths, plugin manifest may not exist", Constants.WHITE, true));
            }
        });
    }

    public void ExtractSelected(CancellationToken cancellationToken, IEnumerable<GameFile> assetItems)
    {
        foreach (var entry in assetItems)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();

            if (UserSettings.Default.OpenBlueprintsInViewer && IsBlueprintPackage(entry))
            {
                Application.Current.Dispatcher.Invoke(() => OpenBlueprintViewer(entry));
                continue;
            }

            Extract(cancellationToken, entry, TabControl.HasNoTabs);
        }
    }

    public bool IsBlueprintPackage(GameFile entry)
    {
        if (!entry.Extension.Equals("uasset", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            return FModel.BlueprintGraph.Assets.Asset.GetBPClassIndex(Provider.LoadPackage(entry)) >= 0;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Could not check whether {Path} is a blueprint", entry.Path);
            return false;
        }
    }

    private void BulkFolder(CancellationToken cancellationToken, TreeItem folder, Action<GameFile> action)
    {
        foreach (var entry in folder.AssetsList.Assets)
        {
            Thread.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                action(entry.Asset);
            }
            catch
            {
                // ignore
            }
        }

        foreach (var f in folder.Folders) BulkFolder(cancellationToken, f, action);
    }

    public void ExportFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        Parallel.ForEach(folder.AssetsList.Assets, entry =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExportData(entry.Asset, false);
        });

        foreach (var f in folder.Folders) ExportFolder(cancellationToken, f);
    }

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder, EBulkType bulk)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, bulk));

    public void ExtractFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs));

    public void SaveFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Properties | EBulkType.Auto));

    public void TextureFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Textures | EBulkType.Auto));

    public void ModelFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Meshes | EBulkType.Auto));

    public void AnimationFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Animations | EBulkType.Auto));

    public void AudioFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Audio | EBulkType.Auto));

    public void CodeFolder(CancellationToken cancellationToken, TreeItem folder)
        => BulkFolder(cancellationToken, folder, asset => Extract(cancellationToken, asset, TabControl.HasNoTabs, EBulkType.Code | EBulkType.Auto));

    public void ExportBlueprintCppAndJsonFolder(CancellationToken cancellationToken, TreeItem folder)
    {
        var maxDegree = ResolveBlueprintBatchConcurrency();
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegree
        };

        var assets = EnumerateFolderAssetsRecursive(folder)
            .Where(static entry =>
                string.Equals(entry.Extension, "uasset", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Extension, "umap", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Parallel.ForEach(assets, options, entry =>
        {
            ExportBlueprintCppAndJsonAsset(cancellationToken, entry);
        });
    }

    public void ExportBlueprintCppAndJsonAsset(CancellationToken cancellationToken, GameFile entry)
    {
        if (entry == null)
            return;
        if (!string.Equals(entry.Extension, "uasset", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(entry.Extension, "umap", StringComparison.OrdinalIgnoreCase))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var package = Provider.LoadPackage(entry);
            var mappings = Provider.MappingsForGame ?? new TypeMappings();
            var pseudo = TryDecompileBlueprintToPseudo(package, mappings);
            if (string.IsNullOrWhiteSpace(pseudo))
                return; // non-blueprint package, skip quietly

            pseudo = TryAugmentPseudoWithUAssetApi(entry, package, pseudo);

            var baseDir = UserSettings.Default.CodeDirectory;
            var relativeDir = UserSettings.Default.KeepDirectoryStructure ? entry.Directory : string.Empty;
            var fileBaseName = Path.GetFileNameWithoutExtension(entry.Name) ?? "blueprint";

            var cppPath = Path.Combine(baseDir, relativeDir, $"{fileBaseName}.cpp").Replace('\\', '/');
            Directory.CreateDirectory(Path.GetDirectoryName(cppPath) ?? baseDir);
            File.WriteAllText(cppPath, pseudo);
            if (File.Exists(cppPath))
                Interlocked.Increment(ref ExportedCount);
            else
                Interlocked.Increment(ref FailedExportCount);

            if (!TryStagePackageToTemporaryDirectory(entry, out var stagedUassetPath, out var tempDirectory, out var stageError))
            {
                Log.Warning("Blueprint json export stage failed for {Path}: {Error}", entry.Path, stageError);
                Interlocked.Increment(ref FailedExportCount);
                return;
            }

            try
            {
                var objectVersion = ResolveObjectVersion(package);
                var objectVersionUE5 = ResolveObjectVersionUE5(package);
                var customVersions = ResolveCustomVersions(package, objectVersion, objectVersionUE5);
                var usmap = ResolveUsmapFromCurrentSettings();
                if (package.HasFlags(EPackageFlags.PKG_UnversionedProperties) && usmap == null)
                {
                    Log.Warning("Skipping UAssetAPI json export for unversioned blueprint {Path}: no readable .usmap.", entry.Path);
                    Interlocked.Increment(ref FailedExportCount);
                    return;
                }

                var asset = LoadBlueprintAssetWithDependencyPreload(
                    entry,
                    stagedUassetPath,
                    tempDirectory,
                    objectVersion,
                    objectVersionUE5,
                    customVersions,
                    usmap);
                var json = SerializeBlueprintJsonWithOffsets(asset);
                var jsonPath = Path.Combine(baseDir, relativeDir, $"{fileBaseName}.json").Replace('\\', '/');
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath) ?? baseDir);
                File.WriteAllText(jsonPath, json);
                if (File.Exists(jsonPath))
                    Interlocked.Increment(ref ExportedCount);
                else
                    Interlocked.Increment(ref FailedExportCount);
            }
            finally
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to export blueprint cpp/json for {Path}", entry.Path);
            Interlocked.Increment(ref FailedExportCount);
        }
    }

    private static IEnumerable<GameFile> EnumerateFolderAssetsRecursive(TreeItem folder)
    {
        if (folder == null)
            yield break;

        foreach (var item in folder.AssetsList.Assets)
        {
            if (item?.Asset != null)
                yield return item.Asset;
        }

        foreach (var child in folder.Folders)
        {
            foreach (var nested in EnumerateFolderAssetsRecursive(child))
                yield return nested;
        }
    }

    private static int ResolveBlueprintBatchConcurrency()
    {
        if (!UserSettings.Default.AutoBlueprintBatchConcurrency)
            return Math.Max(1, UserSettings.Default.ManualBlueprintBatchConcurrency);

        var cpu = Math.Max(1, Environment.ProcessorCount);
        // Conservative default to avoid overwhelming Provider/disk and keep UI responsive.
        return Math.Max(1, Math.Min(8, cpu / 2));
    }

    public void Extract(CancellationToken cancellationToken, GameFile entry, bool addNewTab = false, EBulkType bulk = EBulkType.None)
    {
        ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
        Log.Information("User DOUBLE-CLICKED to extract '{FullPath}'", entry.Path);

        if (addNewTab && TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(entry.Extension);

        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveProperties = HasFlag(bulk, EBulkType.Properties);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);
        var saveDecompiled = HasFlag(bulk, EBulkType.Code);
        switch (entry.Extension)
        {
            case "uasset":
            case "umap":
            {
                var result = Provider.GetLoadPackageResult(entry);
                TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;

                if (saveProperties || updateUi)
                {
                    TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(saveProperties), Formatting.Indented), saveProperties, updateUi);
                    if (saveProperties) break; // do not search for viewable exports if we are dealing with jsons
                }

                if (saveDecompiled)
                {
                    if (Decompile(entry, false))
                        TabControl.SelectedTab.SaveDecompiled(updateUi);
                    break;
                }

                for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
                {
                    if (CheckExport(cancellationToken, result.Package, i, bulk))
                        break;
                }

                break;
            }
            case "ini" when entry.Name.Contains("BinaryConfig"):
            {
                var ar = entry.CreateReader();
                var configCache = new FConfigCacheIni(ar);

                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(configCache, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "dat" when Provider.Versions.Game is EGame.GAME_Aion2:
            {
                ProcessAion2DatFile(entry, updateUi, saveProperties);
                break;
            }
            case "bytes" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            {
                ProcessRocoBinFile(entry, updateUi, saveProperties);
                break;
            }
            case "dbc" when Provider.Versions.Game is EGame.GAME_AshesOfCreation:
            {
                ProcessCacheDBFile(entry, updateUi, saveProperties);
                break;
            }
            case "upluginmanifest":
            case "code-workspace":
            case "projectstore":
            case "uefnproject":
            case "uproject":
            case "manifest":
            case "uplugin":
            case "archive":
            case "dnearchive": // Banishers: Ghosts of New Eden
            case "gitignore":
            case "LICENSE":
            case "playstats": // Dispatch
            case "template":
            case "stUMeta": // LIS: Double Exposure
            case "vmodule":
            case "glslfx":
            case "cptake":
            case "uparam": // Steel Hunters
            case "spi1d":
            case "verse":
            case "html":
            case "json5":
            case "json":
            case "uref":
            case "cube":
            case "usda":
            case "ocio":
            case "data" when Provider.ProjectName is "OakGame":
            case "scss":
            case "ini":
            case "txt":
            case "log":
            case "lsd": // Days Gone
            case "bat":
            case "dat":
            case "cfg":
            case "ddr":
            case "ide":
            case "ipl":
            case "zon":
            case "xml":
            case "css":
            case "csv":
            case "pem":
            case "tsv":
            case "tps":
            case "tgc": // State of Decay 2
            case "cpp":
            case "apx":
            case "udn":
            case "doc":
            case "lua":
            case "vdf":
            case "yml":
            case "js":
            case "po":
            case "md":
            case "h":
            case "non" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            case "cam" when Provider.Versions.Game is EGame.GAME_RocoKingdomWorld:
            // Uncharted Waters Origin
            case "crn":
            case "uwt":
            case "wvh":
            case "bf":
            case "bl":
            case "bm":
            case "br":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);

                break;
            }
            case "locmeta":
            {
                var archive = entry.CreateReader();
                var metadata = new FTextLocalizationMetaDataResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(metadata, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "locres":
            {
                var archive = entry.CreateReader();
                var locres = new FTextLocalizationResource(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(locres, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("AssetRegistry", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FAssetRegistryState(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bin" when entry.Name.Contains("GlobalShaderCache", StringComparison.OrdinalIgnoreCase):
            {
                var archive = entry.CreateReader();
                var registry = new FGlobalShaderCache(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(registry, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "bank":
            {
                var archive = entry.CreateReader();
                if (!FmodProvider.TryLoadBank(archive, entry.NameWithoutExtension, out var fmodReader))
                {
                    Log.Error($"Failed to load FMOD bank {entry.Path}");
                    break;
                }

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(fmodReader, Formatting.Indented, converters: [new FmodSoundBankConverter(), new StringEnumConverter()]), saveProperties, updateUi);

                var extractedSounds = FmodProvider.ExtractBankSounds(fmodReader);
                var directory = Path.GetDirectoryName(entry.Path) ?? "/FMOD/Desktop/";
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "bnk":
            case "pck":
            {
                var archive = entry.CreateReader();
                var wwise = new WwiseReader(archive, new WwiseGameFileSource(entry));
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(wwise, Formatting.Indented), saveProperties, updateUi);

                var medias = WwiseProvider.ExtractBankSounds(wwise);
                foreach (var media in medias)
                {
                    SaveAndPlaySound(cancellationToken, media.OutputPath, media.Extension, media.Data?.GetData() ?? [], saveAudio, updateUi);
                }

                break;
            }
            case "awb":
            {
                var archive = entry.CreateReader();
                var awbReader = new AwbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(awbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(awbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "acb":
            {
                var archive = entry.CreateReader();
                var acbReader = new AcbReader(archive);

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(acbReader, Formatting.Indented), saveProperties, updateUi);

                var directory = Path.GetDirectoryName(archive.Name) ?? "/Criware/";
                var extractedSounds = CriWareProvider.ExtractCriWareSounds(acbReader, archive.Name);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name), sound.Extension, sound.Data, saveAudio, updateUi);
                }

                break;
            }
            case "xvag":
            case "flac":
            case "at9":
            case "wem":
            case "wav":
            case "WAV":
            case "ogg":
                // todo: CSCore.MediaFoundation.MediaFoundationException The byte stream type of the given URL is unsupported. case "aif":
            {
                var data = Provider.SaveAsset(entry);
                SaveAndPlaySound(cancellationToken, entry.PathWithoutExtension, entry.Extension, data, saveAudio, updateUi);

                break;
            }
            case "udic":
            {
                var archive = entry.CreateReader();
                var header = new FOodleDictionaryArchive(archive).Header;
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(header, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "png":
            case "jpg":
            case "bmp":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, SKBitmap.Decode(stream), saveTextures, updateUi);

                break;
            }
            case "svg":
            {
                var data = Provider.SaveAsset(entry);
                using var stream = new MemoryStream(data) { Position = 0 };
                var svg = new SKSvg();
                svg.Load(stream);

                int size = 512;
                var bitmap = new SKBitmap(size, size);
                using var canvas = new SKCanvas(bitmap);
                canvas.Clear(SKColors.Transparent);

                if (svg.Picture == null)
                    break;

                var bounds = svg.Picture.CullRect;
                float scale = Math.Min(size / bounds.Width, size / bounds.Height);
                canvas.Scale(scale);
                canvas.Translate(-bounds.Left, -bounds.Top);
                canvas.DrawPicture(svg.Picture);

                TabControl.SelectedTab.AddImage(entry.NameWithoutExtension, false, bitmap, saveTextures, updateUi);

                break;
            }
            case "ufont":
            case "otf":
            case "ttf":
                FLogger.Append(ELog.Warning, () =>
                    FLogger.Text($"Export '{entry.Name}' raw data and change its extension if you want it to be an installable font file", Constants.WHITE, true));
                break;
            case "ushaderbytecode":
            case "ushadercode":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderCodeArchive(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "upipelinecache":
            {
                var archive = entry.CreateReader();
                var ar = new FPipelineCacheFile(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "stinfo":
            {
                var archive = entry.CreateReader();
                var ar = new FShaderTypeHashes(archive);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(ar, Formatting.Indented), saveProperties, updateUi);

                break;
            }
            case "res": // just skip
            case "luac": // compiled lua
            case "bytes": // wuthering waves
                break;
            default:
            {
                Log.Warning($"The package '{entry.Name}' is of an unknown type.");
                if (!UnknownExtensions.Contains(entry.Extension))
                {
                    UnknownExtensions.Add(entry.Extension);
                FLogger.Append(ELog.Warning, () =>
                        FLogger.Text($"There are some packages with an unknown type {entry.Extension}. Check Log file for a full list.", Constants.WHITE, true));
                }
                break;
            }
        }

        // Roco Kingdom: World
        void ProcessRocoBinFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
            var nonFileName = "/" + entry.NameWithoutExtension + ".non";
            var nonPath = Provider.Files.Keys.FirstOrDefault(k => k.EndsWith(nonFileName, StringComparison.OrdinalIgnoreCase));

            // I will only get one localization file because they did not translate any languages, lol
            var locPathKey = entry.Path.Replace("/BinData/", "/BinLocalize/en_US/").Replace("/BinDataCompressed/", "/BinLocalize/en_US/");
            var locFileFound = Provider.Files.TryGetValue(locPathKey, out var locEntry);

            if (!string.IsNullOrEmpty(nonPath) && Provider.Files.TryGetValue(nonPath, out var nonEntry))
            {
                string json = Encoding.UTF8.GetString(nonEntry.Read());
                var schema = JsonConvert.DeserializeObject<FRocoSchema>(json);
                var archive = entry.CreateReader();
                var locArchive = locFileFound ? new FRocoBinData(locEntry.CreateReader(), null, ERocoBinDataType.BinLocalize) : null;

                var data = entry.PathWithoutExtension switch
                {
                    var p when p.Contains("BinDataCompressed") => new FRocoBinData(archive, schema, ERocoBinDataType.BinDataCompressed, locArchive),
                    var p when p.Contains("BinData") => new FRocoBinData(archive, schema, ERocoBinDataType.BinData, locArchive),
                    var p when p.Contains("BinLocalize") => new FRocoBinData(archive, null, ERocoBinDataType.BinLocalize),
                    _ => null
                };

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(data, Formatting.Indented), saveProperties, updateUi);
            }
            else if (entry.PathWithoutExtension.Contains("/Bin/"))
            {
                throw new Exception($"Could not find associated .non file for {entry.Name}");
            }
        }

        void ProcessAion2DatFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
            if (entry.NameWithoutExtension.EndsWith("_MapEvent"))
            {
                var data = Provider.SaveAsset(entry);
                FAion2DatFileArchive.DecryptData(data);
                using var stream = new MemoryStream(data) { Position = 0 };
                using var reader = new StreamReader(stream);

                TabControl.SelectedTab.SetDocumentText(reader.ReadToEnd(), saveProperties, updateUi);
            }
            else if (entry.NameWithoutExtension.Equals("L10NString"))
            {
                var l10nData = new FAion2L10NFile(entry, Provider);
                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(l10nData, Formatting.Indented), saveProperties, updateUi);
            }
            else
            {
                FAion2DataFile datfile = entry.NameWithoutExtension switch
                {
                    "MapDataHierarchy" => new FAion2MapHierarchyFile(entry),
                    "MapData" => new FAion2MapDataFile(entry, Provider),
                    _ when entry.Directory.EndsWith("Data/WorldMap", StringComparison.OrdinalIgnoreCase) => new FAion2MapDataFile(entry, Provider),
                    _ => new FAion2DataTableFile(entry, Provider)
                };

                TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(datfile, Formatting.Indented), saveProperties, updateUi);
            }
        }

        // Ashhes of Creation
        void ProcessCacheDBFile(GameFile entry, bool updateUi, bool saveProperties)
        {
            var data = entry.Read();
            var dbc = new FAoCDBCReader(data, Provider.MappingsForGame, Provider.Versions);
            for (var i = 0; i < dbc.Chunks.Length; i++)
            {
                if (!dbc.TryReadChunk(i, out var category, out var files))
                {
                    Log.Warning("Couldn't read {i} chuck in AoC CacheDB", i);
                    continue;
                }
                var fileName = Path.ChangeExtension(category, ".json");
                var directory = Path.Combine(UserSettings.Default.PropertiesDirectory,
                    UserSettings.Default.KeepDirectoryStructure ? entry.Directory : "", entry.Name, fileName).Replace('\\', '/');

                Directory.CreateDirectory(directory.SubstringBeforeLast('/'));

                File.WriteAllText(directory, JsonConvert.SerializeObject(files, Formatting.Indented));
            }

            TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(dbc, Formatting.Indented), saveProperties, updateUi);
        }
    }

    public void ExtractAndScroll(CancellationToken cancellationToken, string fullPath, string objectName, string parentExportType)
    {
        Log.Information("User CTRL-CLICKED to extract '{FullPath}'", fullPath);

        var entry = Provider[fullPath];
        TabControl.AddTab(entry, parentExportType);
        TabControl.SelectedTab.ScrollTrigger = objectName;

        var result = Provider.GetLoadPackageResult(entry, objectName);

        TabControl.SelectedTab.TitleExtra = result.TabTitleExtra;
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector(""); // json
        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(result.GetDisplayData(), Formatting.Indented), false, false);

        for (var i = result.InclusiveStart; i < result.ExclusiveEnd; i++)
        {
            if (CheckExport(cancellationToken, result.Package, i))
                break;
        }
    }

    private bool CheckExport(CancellationToken cancellationToken, IPackage pkg, int index, EBulkType bulk = EBulkType.None) // return true once you want to stop searching for exports
    {
        var isNone = bulk == EBulkType.None;
        var updateUi = !HasFlag(bulk, EBulkType.Auto);
        var saveTextures = HasFlag(bulk, EBulkType.Textures);
        var saveAudio = HasFlag(bulk, EBulkType.Audio);

        var pointer = new FPackageIndex(pkg, index + 1).ResolvedObject;
        if (pointer?.Object is null) return false;

        var dummy = ((AbstractUePackage) pkg).ConstructObject(pointer.Class, pkg);
        switch (dummy)
        {
            case UVerseDigest when isNone && pointer.Object.Value is UVerseDigest verseDigest:
            {
                if (!TabControl.CanAddTabs) return false;

                TabControl.AddTab($"{verseDigest.ProjectName}.verse");
                TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("verse");
                TabControl.SelectedTab.SetDocumentText(verseDigest.ReadableCode, false, false);
                return true;
            }
            case UTexture when (isNone || saveTextures) && pointer.Object.Value is UTexture texture:
            {
                TabControl.SelectedTab.AddImage(texture, saveTextures, updateUi);
                return false;
            }
            case USvgAsset when (isNone || saveTextures) && pointer.Object.Value is USvgAsset svgasset:
            {
                const int size = 512;
                var data = svgasset.GetOrDefault<byte[]>("SvgData");
                var sourceFile = svgasset.GetOrDefault<string>("SourceFile");
                using var stream = new MemoryStream(data) { Position = 0 };

                var svg = new SKSvg();
                svg.Load(stream);

                if (svg.Picture == null)
                    return false;

                var b = svg.Picture.CullRect;
                float s = Math.Min(size / b.Width, size / b.Height);

                var bitmap = new SKBitmap(size, size);
                using var canvas = new SKCanvas(bitmap);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };

                canvas.Scale(s);
                canvas.Translate(-b.Left, -b.Top);
                canvas.DrawPicture(svg.Picture, paint);

                if (saveTextures)
                {
                    var fileName = sourceFile.SubstringAfterLast('/');
                    var path = Path.Combine(UserSettings.Default.TextureDirectory,
                        UserSettings.Default.KeepDirectoryStructure ? TabControl.SelectedTab.Entry.Directory : "", fileName!).Replace('\\', '/');

                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));

                    using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                    fs.Write(data, 0, data.Length);
                    if (File.Exists(path))
                    {
                        Log.Information("{FileName} successfully saved", fileName);
                        if (updateUi)
                        {
                            FLogger.Append(ELog.Information, () =>
                            {
                                FLogger.Text("Successfully saved ", Constants.WHITE);
                                FLogger.Link(fileName, path, true);
                            });
                        }
                    }
                    else
                    {
                        Log.Error("{FileName} could not be saved", fileName);
                        if (updateUi)
                            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{fileName}'", Constants.WHITE, true));
                    }
                }

                TabControl.SelectedTab.AddImage(sourceFile.SubstringAfterLast('/'), false, bitmap, false, updateUi);
                return false;
            }
            // Supermassive Games (for example - The Dark Pictures Anthology: House of Ashes etc.)
            case UExternalSource when (isNone || saveAudio) && pointer.Object.Value is UExternalSource externalSource:
            {
                var audioName = Path.GetFileNameWithoutExtension(externalSource.ExternalSourcePath);
                var outputPath = Path.Combine(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/').SubstringBeforeLast('/'), audioName);
                SaveAndPlaySound(cancellationToken, outputPath, "wem", externalSource.Data?.WemFile?.GetData() ?? [], saveAudio, updateUi);
                return false;
            }
            case UAkAudioBank when (isNone || saveAudio) && pointer.Object.Value is UAkAudioBank soundBank:
            {
                var extractedSounds = WwiseProvider.ExtractBankSounds(soundBank);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            case UAkAudioEvent when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEvent audioEvent:
            {
                var extractedSounds = WwiseProvider.ExtractAudioEventSounds(audioEvent);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            case UFMODEvent when (isNone || saveAudio) && pointer.Object.Value is UFMODEvent fmodEvent:
            {
                var extractedSounds = FmodProvider.ExtractEventSounds(fmodEvent);
                var directory = Path.GetDirectoryName(Provider.FixPath(fmodEvent.Owner?.Name ?? "/FMOD/Desktop/"));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case UFMODBank when (isNone || saveAudio) && pointer.Object.Value is UFMODBank fmodBank:
            {
                var extractedSounds = FmodProvider.ExtractBankSounds(fmodBank);
                var directory = Path.GetDirectoryName(Provider.FixPath(fmodBank.Owner?.Name ?? "/FMOD/Desktop/"));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case USoundAtomCueSheet or UAtomCueSheet or USoundAtomCue or UAtomWaveBank when (isNone || saveAudio) && pointer.Object.Value is UObject atomObject:
            {
                var extractedSounds = atomObject switch
                {
                    USoundAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    UAtomCueSheet cueSheet => CriWareProvider.ExtractCriWareSounds(cueSheet),
                    USoundAtomCue cue => CriWareProvider.ExtractCriWareSounds(cue),
                    UAtomWaveBank awb => CriWareProvider.ExtractCriWareSounds(awb),
                    _ => []
                };

                var directory = Path.GetDirectoryName(atomObject.Owner?.Name) ?? "/Criware/";
                directory = Path.GetDirectoryName(atomObject.Owner.Provider.FixPath(directory));
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, Path.Combine(directory, sound.Name).Replace("\\", "/"), sound.Extension, sound.Data, saveAudio, updateUi);
                }
                return false;
            }
            case USQEXSEADSoundBank or USQEXSEADSound when (isNone || saveAudio) && pointer.Object.Value is UObject squareEnixObject:
            {
                var data = squareEnixObject switch
                {
                    USQEXSEADSoundBank sqexSoundBank => sqexSoundBank.SQEXSoundBankData?.Data ?? [],
                    USQEXSEADSound sqexSound => sqexSound.SQEXSoundData?.Data ?? [],
                    _ => [],
                };
                var sabPath = Path.Combine(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/').SubstringBeforeLast('/'), squareEnixObject.Name);
                var extractedSounds = AudioPlayerViewModel.ExtractSquareEnixAudio(sabPath, data);
                foreach (var soundPath in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, soundPath, "wav", File.ReadAllBytes(soundPath), saveAudio, updateUi);
                }
                return false;
            }
            case UAkMediaAssetData when isNone || saveAudio:
            case USoundWave when isNone || saveAudio:
            {
                // If UAkMediaAsset exists in the same package it should be used to handle the audio instead (because it contains actual audio name)
                if (pointer.Object.Value is UAkMediaAssetData dataObj && dataObj.Outer.Object.Value is UAkMediaAsset)
                    return false;

                var shouldDecompress = UserSettings.Default.CompressedAudioMode == ECompressedAudio.PlayDecompressed;
                pointer.Object.Value.Decode(shouldDecompress, out var audioFormat, out var data);
                var hasAf = !string.IsNullOrEmpty(audioFormat);
                if (data == null || !hasAf)
                {
                    if (hasAf) FLogger.Append(ELog.Warning, () => FLogger.Text($"Unsupported audio format '{audioFormat}'", Constants.WHITE, true));
                    return false;
                }

                SaveAndPlaySound(cancellationToken, TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/'), audioFormat, data, saveAudio, updateUi);
                return false;
            }
            case UAkMediaAsset when (isNone || saveAudio) && pointer.Object.Value is UAkMediaAsset akMediaAsset:
            {
                var audioName = akMediaAsset.MediaName ?? akMediaAsset.Name;
                var outputPath = Path.Combine(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/').SubstringBeforeLast('/'), audioName);
                if (akMediaAsset.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData akMediaAssetData)
                {
                    var shouldDecompress = UserSettings.Default.CompressedAudioMode is ECompressedAudio.PlayDecompressed;
                    akMediaAssetData.Decode(shouldDecompress, out var audioFormat, out var data);

                    SaveAndPlaySound(cancellationToken, outputPath, audioFormat, data, saveAudio, updateUi);
                }
                return false;
            }
            case UAkAudioEventData when (isNone || saveAudio) && pointer.Object.Value is UAkAudioEventData akAudioEventData:
            {
                var shouldDecompress = UserSettings.Default.CompressedAudioMode is ECompressedAudio.PlayDecompressed;
                foreach (var mediaIndex in akAudioEventData.MediaList)
                {
                    if (mediaIndex?.Object?.Value is UAkMediaAsset akMediaAsset)
                    {
                        if (akMediaAsset.CurrentMediaAssetData?.ResolvedObject?.Object?.Value is UAkMediaAssetData akMediaAssetData)
                        {
                            var audioName = akMediaAsset.MediaName ?? $"{akAudioEventData.Outer.Name} ({akMediaAsset.ID})";
                            var outputPath = Path.Combine(TabControl.SelectedTab.Entry.PathWithoutExtension.Replace('\\', '/').SubstringBeforeLast('/'), audioName);
                            akMediaAssetData.Decode(shouldDecompress, out var audioFormat, out var data);

                            SaveAndPlaySound(cancellationToken, outputPath, audioFormat, data, saveAudio, updateUi);
                        }
                    }
                }
                return false;
            }
            // Borderlands 3
            case UDialogPerformanceData when (isNone || saveAudio) && pointer.Object.Value is UDialogPerformanceData dialogPerformanceData:
            {
                var extractedSounds = WwiseProvider.ExtractDialogBorderlands3(dialogPerformanceData);
                foreach (var sound in extractedSounds)
                {
                    SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                }
                return false;
            }
            // Borderlands 4
            case UFaceFXAnimSet when (isNone || saveAudio) && pointer.Object.Value is UFaceFXAnimSet faceFXAnimSet:
            {
                if (Provider.Versions.Game is not EGame.GAME_Borderlands4)
                    return false;

                var ownerDirectory = WwiseProvider.GetOwnerDirectory(faceFXAnimSet);
                foreach (var faceFXAnimData in faceFXAnimSet.FaceFXAnimDataList)
                {
                    var extractedSounds = WwiseProvider.ExtractAudioEventBorderlands4(ownerDirectory, faceFXAnimData.ID.Name, false);
                    foreach (var sound in extractedSounds)
                    {
                        SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                    }
                }

                return false;
            }
            // Borderlands 4
            case UGbxGraphAsset when (isNone || saveAudio) && pointer.Object.Value is UGbxGraphAsset gbxGraphAsset:
            {
                var ownerDirectory = WwiseProvider.GetOwnerDirectory(gbxGraphAsset);
                foreach (var (eventName, useSoundTag) in GbxAudioUtil.GetAndClearEvents())
                {
                    var extractedSounds = WwiseProvider.ExtractAudioEventBorderlands4(ownerDirectory, eventName, useSoundTag);
                    foreach (var sound in extractedSounds)
                    {
                        SaveAndPlaySound(cancellationToken, sound.OutputPath, sound.Extension, sound.Data?.GetData() ?? [], saveAudio, updateUi);
                    }
                }

                return false;
            }
            case UWorld when isNone && UserSettings.Default.PreviewWorlds:
            case UBlueprintGeneratedClass when isNone && UserSettings.Default.PreviewWorlds && TabControl.SelectedTab.ParentExportType switch
            {
                "JunoBuildInstructionsItemDefinition" => true,
                "JunoBuildingSetAccountItemDefinition" => true,
                "JunoBuildingPropAccountItemDefinition" => true,
                _ => false
            }:
            case UPaperSprite when isNone && UserSettings.Default.PreviewMaterials:
            case UStaticMesh when isNone && UserSettings.Default.PreviewStaticMeshes:
            case USkeletalMesh when isNone && UserSettings.Default.PreviewSkeletalMeshes:
            case USkeleton when isNone && UserSettings.Default.SaveSkeletonAsMesh:
            case UMaterialInstance when isNone && UserSettings.Default.PreviewMaterials && !ModelIsOverwritingMaterial &&
                                        !(Provider.ProjectName.Equals("FortniteGame", StringComparison.OrdinalIgnoreCase) &&
                                          (pkg.Name.Contains("/MI_OfferImages/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/RenderSwitch_Materials/", StringComparison.OrdinalIgnoreCase) ||
                                           pkg.Name.Contains("/MI_BPTile/", StringComparison.OrdinalIgnoreCase))):
            {
                if (SnooperViewer.TryLoadExport(cancellationToken, dummy, pointer.Object))
                    SnooperViewer.Run();
                return true;
            }
            case UMaterialInstance when isNone && ModelIsOverwritingMaterial && pointer.Object.Value is UMaterialInstance m:
            {
                SnooperViewer.Renderer.Swap(m);
                SnooperViewer.Run();
                return true;
            }
            case UAnimSequenceBase when isNone && UserSettings.Default.PreviewAnimations || ModelIsWaitingAnimation:
            {
                // animate all animations using their specified skeleton or when we explicitly asked for a loaded model to be animated (ignoring whether we wanted to preview animations)
                SnooperViewer.Renderer.Animate(pointer.Object.Value);
                SnooperViewer.Run();
                return true;
            }
            case UStaticMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeletalMesh when HasFlag(bulk, EBulkType.Meshes):
            case USkeleton when UserSettings.Default.SaveSkeletonAsMesh && HasFlag(bulk, EBulkType.Meshes):
            // case UMaterialInstance when HasFlag(bulk, EBulkType.Materials): // read the fucking json
            case UAnimSequenceBase when HasFlag(bulk, EBulkType.Animations):
            {
                SaveExport(pointer.Object.Value, updateUi);
                return true;
            }
            default:
            {
                if (!isNone && !saveTextures) return false;

                using var cPackage = new CreatorPackage(pkg.Name, dummy.ExportType, pointer.Object, UserSettings.Default.CosmeticStyle);
                if (!cPackage.TryConstructCreator(out var creator))
                    return false;

                creator.ParseForInfo();
                TabControl.SelectedTab.AddImage(pointer.Object.Value.Name, false, creator.Draw(), saveTextures, updateUi);
                return true;

            }
        }
    }

    public void ShowMetadata(GameFile entry)
    {
        ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;

        var package = Provider.LoadPackage(entry);

        if (TabControl.CanAddTabs) TabControl.AddTab(entry);
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Metadata";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("");

        TabControl.SelectedTab.SetDocumentText(JsonConvert.SerializeObject(package, Formatting.Indented), false, false);
    }

    public void FindReferences(GameFile entry)
    {
        var refs = Provider.ScanForPackageRefs(entry);
        Application.Current.Dispatcher.Invoke(delegate
        {
            var refView = DockPanels.ShowSearch(ESearchViewTab.RefView);
            refView.ChangeCollection(ESearchViewTab.RefView, refs, entry);
        });
    }


    /// <summary>
    /// Opens the blueprint node graph viewer for a package. Must be called on the UI thread.
    /// </summary>
    public void OpenBlueprintViewer(GameFile entry) => OpenBlueprintViewer(entry, null, null);

    /// <summary>Opens (or brings forward) the viewer of a blueprint, optionally at a function or with a Find search.</summary>
    public void OpenBlueprintViewer(GameFile entry, string functionName, string findTerm)
    {
        var viewModel = new BlueprintViewerViewModel(Provider, entry, JmapPath, this);
        var window = DockPanels.ShowBlueprintViewer(viewModel);
        if (functionName != null || findTerm != null)
            window.NavigateTo(functionName, findTerm);
    }

    /// <summary>Opens a referenced asset: blueprints in the viewer, anything else in a tab like a double-click in the explorer.</summary>
    public async void OpenReferencedAsset(GameFile entry)
    {
        if (IsBlueprintPackage(entry))
        {
            OpenBlueprintViewer(entry);
            return;
        }

        // like opening from the explorer: the asset grid would cover the new tab, and the viewer document would stay in front
        ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
        DockWorkspace.Activate("Workspace");
        await ApplicationService.ThreadWorkerView.Begin(cancellationToken => Extract(cancellationToken, entry, true));
    }

    /// <summary>
    /// Loads a package with UAssetAPI for the blueprint viewer's editing. The staging directory (with the dependencies it
    /// needed) stays alive for the edit session; delete it with <see cref="DeleteBlueprintEditDirectory"/>.
    /// </summary>
    public UAssetApiAsset LoadBlueprintAssetForEditing(GameFile entry, out string stagedUassetPath, out string workDirectory)
    {
        if (!TryStagePackageToTemporaryDirectory(entry, out stagedUassetPath, out workDirectory, out var error))
            throw new InvalidOperationException(error);

        try
        {
            var package = Provider.LoadPackage(entry);
            var objectVersion = ResolveObjectVersion(package);
            var objectVersionUE5 = ResolveObjectVersionUE5(package);
            var customVersions = ResolveCustomVersions(package, objectVersion, objectVersionUE5);
            var mappings = ResolveUsmapFromCurrentSettings();
            if (package.HasFlags(EPackageFlags.PKG_UnversionedProperties) && mappings == null)
            {
                throw new InvalidOperationException(
                    "This package uses unversioned properties, but no readable .usmap could be loaded. " +
                    "Please configure a readable mapping file path in Settings and try again.");
            }

            return LoadBlueprintAssetWithDependencyPreload(entry, stagedUassetPath, workDirectory, objectVersion, objectVersionUE5, customVersions, mappings);
        }
        catch
        {
            TryDeleteDirectory(workDirectory);
            throw;
        }
    }

    /// <summary>Reads a package written by <paramref name="template"/>'s edit session with the same versions and mappings.</summary>
    public static UAssetApiAsset ReadBlueprintAssetLike(UAssetApiAsset template, string uassetPath) =>
        ReadBlueprintAsset(uassetPath, template.ObjectVersion, template.ObjectVersionUE5, template.CustomVersionContainer, template.Mappings,
            ResolveMapStructTypeOverrides(), UAssetAPI.CustomSerializationFlags.None, template.AssetResolver);

    /// <summary>Reads a legacy (.uasset/.uexp) package from disk with the provider's versions, e.g. to verify a written asset.</summary>
    public IPackage ReadLegacyPackageFromDisk(GameFile entry, string uassetPath) => ReadLegacyPackageFromDisk(entry.PathWithoutExtension, uassetPath);

    /// <inheritdoc cref="ReadLegacyPackageFromDisk(GameFile, string)"/>
    public IPackage ReadLegacyPackageFromDisk(string name, string uassetPath)
    {
        var uexpPath = Path.ChangeExtension(uassetPath, ".uexp");
        var uasset = new FByteArchive(name + ".uasset", File.ReadAllBytes(uassetPath), (VersionContainer) Provider.Versions.Clone());
        var uexp = File.Exists(uexpPath) ? new FByteArchive(name + ".uexp", File.ReadAllBytes(uexpPath), (VersionContainer) Provider.Versions.Clone()) : null;
        var ubulkPath = Path.ChangeExtension(uassetPath, ".ubulk");
        var ubulk = File.Exists(ubulkPath) ? new FByteArchive(name + ".ubulk", File.ReadAllBytes(ubulkPath), (VersionContainer) Provider.Versions.Clone()) : null;
        return new Package(uasset, uexp, ubulk, null, Provider, false);
    }

    /// <summary>Writes an edited blueprint to the exports folder (same place as the JSON write-back). Returns the .uasset path.</summary>
    public string WriteEditedBlueprint(GameFile entry, UAssetApiAsset asset)
    {
        var extension = string.Equals(entry.Extension, "umap", StringComparison.OrdinalIgnoreCase) ? ".umap" : ".uasset";
        var path = GetBlueprintWriteBackPath(entry, extension);
        PrepareBlueprintAssetForWriteBack(asset);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? UserSettings.Default.RawDataDirectory);
        asset.Write(path);
        return path;
    }

    /// <summary>Adds an edited blueprint to the pak packer.</summary>
    public bool AddEditedBlueprintToPack(GameFile entry, UAssetApiAsset asset, out string addedPaths, out string error) =>
        TryAddBlueprintToPack(entry, asset, out addedPaths, out error);

    public static void DeleteBlueprintEditDirectory(string directory) => TryDeleteDirectory(directory);

    public bool Decompile(GameFile entry, bool AddTab = true)
    {
        if (TabControl.CanAddTabs && AddTab)
        {
            ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
            TabControl.AddTab(entry);
        }
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Decompiled C++";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");

        try
        {
            var package = Provider.LoadPackage(entry);
            var mappings = Provider.MappingsForGame ?? new TypeMappings();
            var decompilation = TryDecompileBlueprintToPseudo(package, mappings);
            if (string.IsNullOrWhiteSpace(decompilation))
            {
                const string error = "Could not find a class export to decompile in this package.";
                FLogger.Append(ELog.Warning, () => FLogger.Text(error, Constants.WHITE, true));
                TabControl.SelectedTab.SetDocumentText(error, false, false);
                return false;
            }

            decompilation = TryAugmentPseudoWithUAssetApi(entry, package, decompilation);
            TabControl.SelectedTab.SetDocumentText(decompilation, false, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to decompile blueprint to pseudo C++: {Path}", entry.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to decompile blueprint: {ex.Message}", Constants.WHITE, true));
            TabControl.SelectedTab.SetDocumentText(ex.ToString(), false, false);
            return false;
        }
    }

    public void OpenLinkedBlueprintJson(TabItem sourceTab)
    {
        if (sourceTab?.Entry == null)
            return;

        if (!string.Equals(sourceTab.TitleExtra, "Decompiled C++", StringComparison.Ordinal))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Current tab is not a Decompiled C++ tab", Constants.WHITE, true));
            return;
        }

        TryGetOrOpenLinkedTab(sourceTab.Entry, "Blueprint JSON", DecompileToBlueprintJson, out _);
    }

    public void OpenLinkedBlueprintCpp(TabItem sourceTab)
    {
        if (sourceTab?.Entry == null)
            return;

        if (!string.Equals(sourceTab.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Current tab is not a Blueprint JSON tab", Constants.WHITE, true));
            return;
        }

        TryGetOrOpenLinkedTab(sourceTab.Entry, "Decompiled C++", Decompile, out _);
    }

    public void QueueLivePseudoPreviewFromJson(TabItem sourceTab)
    {
        if (sourceTab?.Entry == null || sourceTab.Document == null)
            return;
        if (sourceTab.IsReadOnly || !string.Equals(sourceTab.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
            return;

        var key = sourceTab.Entry.Path;
        var version = _livePseudoPreviewVersion.AddOrUpdate(key, 1, (_, old) => old + 1);
        var cts = new CancellationTokenSource();
        _livePseudoPreviewCts.AddOrUpdate(key,
            cts,
            (_, previous) =>
            {
                try
                {
                    previous.Cancel();
                    previous.Dispose();
                }
                catch
                {
                    // ignored
                }

                return cts;
            });

        var jsonText = sourceTab.Document.Text;
        var entry = sourceTab.Entry;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(LivePseudoPreviewDebounceMs, cts.Token);
                if (cts.Token.IsCancellationRequested)
                    return;
                if (!_livePseudoPreviewVersion.TryGetValue(key, out var latestVersion) || latestVersion != version)
                    return;

                if (!TryDecompilePseudoFromEditedJson(entry, jsonText, out var pseudo, out var error))
                {
                    UpdateLinkedPseudoPreviewTab(entry, $"// Live preview unavailable\n// {error}", createIfMissing: false, sourceTab);
                    return;
                }

                UpdateLinkedPseudoPreviewTab(entry, pseudo, createIfMissing: true, sourceTab);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Live pseudo preview update failed for {Path}", entry.Path);
            }
        }, cts.Token);
    }

    public void LocateCurrentStatementInLinkedView(TabItem sourceTab)
    {
        if (sourceTab?.Entry == null || sourceTab.Document == null)
            return;

        if (!TryGetCurrentEditorLine(sourceTab, out var lineText))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Unable to read current editor line. Select the source tab and place caret on a statement line.", Constants.WHITE, true));
            return;
        }

        if (string.Equals(sourceTab.TitleExtra, "Decompiled C++", StringComparison.Ordinal))
        {
            if (!TryExtractStatementIndex(lineText, PseudoStatementRegex, out var statementIndex))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("No stmt marker found on current C++ line.", Constants.WHITE, true));
                return;
            }

            if (!TryGetOrOpenLinkedTab(sourceTab.Entry, "Blueprint JSON", DecompileToBlueprintJson, out var targetTab))
                return;

            var token = $"\"StatementIndex\": {statementIndex}";
            if (!FocusTokenInTab(targetTab, token))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"Could not locate StatementIndex {statementIndex} in Blueprint JSON.", Constants.WHITE, true));
            }
            return;
        }

        if (string.Equals(sourceTab.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
        {
            if (!TryExtractStatementIndex(lineText, JsonStatementRegex, out var statementIndex))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text("No StatementIndex field found on current JSON line.", Constants.WHITE, true));
                return;
            }

            if (!TryGetOrOpenLinkedTab(sourceTab.Entry, "Decompiled C++", Decompile, out var targetTab))
                return;

            var token = $"stmt:{statementIndex}";
            if (!FocusTokenInTab(targetTab, token))
            {
                FLogger.Append(ELog.Warning, () => FLogger.Text($"Could not locate stmt:{statementIndex} in Decompiled C++.", Constants.WHITE, true));
            }
            return;
        }

        FLogger.Append(ELog.Warning, () => FLogger.Text("Current tab does not support statement mapping.", Constants.WHITE, true));
    }

    public bool DecompileToBlueprintJson(GameFile entry, bool AddTab = true)
    {
        if (TabControl.CanAddTabs && AddTab)
        {
            ApplicationService.ApplicationView.IsAssetsExplorerVisible = false;
            TabControl.AddTab(entry);
        }
        else TabControl.SelectedTab.SoftReset(entry);

        TabControl.SelectedTab.TitleExtra = "Blueprint JSON";
        TabControl.SelectedTab.Highlighter = AvalonExtensions.HighlighterSelector("json");
        TabControl.SelectedTab.SecondaryHighlighter = AvalonExtensions.HighlighterSelector("cpp");
        TabControl.SelectedTab.SetSecondaryDocumentText("// Live pseudo preview will appear here.");

        if (!TryStagePackageToTemporaryDirectory(entry, out var stagedUassetPath, out var tempDirectory, out var error))
        {
            FLogger.Append(ELog.Error, () => FLogger.Text(error, Constants.WHITE, true));
            TabControl.SelectedTab.SetDocumentText(error, false, false);
            return false;
        }

        try
        {
            var package = Provider.LoadPackage(entry);
            var objectVersion = ResolveObjectVersion(package);
            var objectVersionUE5 = ResolveObjectVersionUE5(package);
            var customVersions = ResolveCustomVersions(package, objectVersion, objectVersionUE5);
            var mappings = ResolveUsmapFromCurrentSettings();
            if (package.HasFlags(EPackageFlags.PKG_UnversionedProperties) && mappings == null)
            {
                throw new InvalidOperationException(
                    "This package uses unversioned properties, but no readable .usmap could be loaded. " +
                    "Please configure a readable mapping file path in Settings and try again.");
            }

            var asset = LoadBlueprintAssetWithDependencyPreload(
                entry,
                stagedUassetPath,
                tempDirectory,
                objectVersion,
                objectVersionUE5,
                customVersions,
                mappings);
            var json = SerializeBlueprintJsonWithOffsets(asset);
            TabControl.SelectedTab.SetDocumentText(json, false, false, isReadOnly: false);

            var initialPseudo = TryDecompileBlueprintToPseudo(package, Provider.MappingsForGame ?? new TypeMappings());
            if (!string.IsNullOrWhiteSpace(initialPseudo))
            {
                initialPseudo = TryAugmentPseudoWithUAssetApi(entry, package, initialPseudo);
                TabControl.SelectedTab.SetSecondaryDocumentText(initialPseudo);
            }
            else
            {
                TabControl.SelectedTab.SetSecondaryDocumentText("// Live pseudo preview unavailable\n// No class export found in blueprint package.");
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to serialize blueprint with UAssetAPI: {Path}", entry.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to parse blueprint with UAssetAPI: {ex.Message}", Constants.WHITE, true));
            TabControl.SelectedTab.SetDocumentText(ex.ToString(), false, false);
            TabControl.SelectedTab.SetSecondaryDocumentText($"// Live pseudo preview unavailable\n// {ex.Message}");
            return false;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static string SerializeBlueprintJsonWithOffsets(UAssetApiAsset asset)
    {
        var rawJson = asset.SerializeJson(true);

        try
        {
            var root = JObject.Parse(rawJson);
            if (!TryInjectBytecodeOffsets(root, asset))
                return rawJson;

            return root.ToString(Formatting.Indented);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to inject bytecode offsets/sizes into Blueprint JSON. Falling back to raw serialization.");
            return rawJson;
        }
    }

    private UAssetApiAsset LoadBlueprintAssetWithDependencyPreload(
        GameFile rootEntry,
        string stagedUassetPath,
        string tempDirectory,
        UAssetAPI.UnrealTypes.ObjectVersion objectVersion,
        UAssetAPI.UnrealTypes.ObjectVersionUE5 objectVersionUE5,
        List<UAssetAPI.CustomVersion> customVersions,
        UAssetApiUsmap mappings)
    {
        var mapStructOverrides = ResolveMapStructTypeOverrides();
        var stagedDependencyKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // dependencies are read straight from the provider; staging them to disk is only the fallback for packages it can't find
        var resolver = new ProviderAssetResolver(this);

        // Mimic UAssetGUI's pre-load pass with SkipLoadingExports, then do a full parse.
        for (var iteration = 0; iteration < 3; iteration++)
        {
            // UAssetAPI marks dependency packages as processed even when they failed to parse (e.g. their own dependencies
            // weren't staged yet) and never retries them; forget that so newly staged packages are read again.
            // Matters most with .jmap mappings, which carry no blueprint class schemas.
            mappings?.PathsAlreadyProcessedForSchemas.Clear();
            var preloadAsset = ReadBlueprintAsset(
                stagedUassetPath,
                objectVersion,
                objectVersionUE5,
                customVersions,
                mappings,
                mapStructOverrides,
                UAssetAPI.CustomSerializationFlags.SkipLoadingExports,
                resolver);
            var missingPackages = preloadAsset.OtherAssetsFailedToAccess?
                .Where(x => x != null)
                .Select(x => x.ToString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (missingPackages == null || missingPackages.Count == 0)
                break;

            var stagedAny = false;
            foreach (var packageName in missingPackages)
            {
                if (TryStageDependencyPackageFromProvider(packageName, tempDirectory, stagedDependencyKeys))
                    stagedAny = true;
            }

            if (!stagedAny)
                break;

        }

        mappings?.PathsAlreadyProcessedForSchemas.Clear();
        var asset = ReadBlueprintAsset(
            stagedUassetPath,
            objectVersion,
            objectVersionUE5,
            customVersions,
            mappings,
            mapStructOverrides,
            UAssetAPI.CustomSerializationFlags.None,
            resolver);

        var remaining = asset.OtherAssetsFailedToAccess?
            .Where(x => x != null)
            .Select(x => x.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (remaining is { Count: > 0 })
        {
            var preview = string.Join(", ", remaining.Take(12));
            var suffix = remaining.Count > 12 ? $" (+{remaining.Count - 12} more)" : string.Empty;
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"UAssetAPI still could not access {remaining.Count} dependent package(s): {preview}{suffix}",
                Constants.WHITE,
                true));
        }

        return asset;
    }

    private static UAssetApiAsset ReadBlueprintAsset(
        string stagedUassetPath,
        UAssetAPI.UnrealTypes.ObjectVersion objectVersion,
        UAssetAPI.UnrealTypes.ObjectVersionUE5 objectVersionUE5,
        List<UAssetAPI.CustomVersion> customVersions,
        UAssetApiUsmap mappings,
        IDictionary<string, Tuple<UAssetAPI.UnrealTypes.FString, UAssetAPI.UnrealTypes.FString>> mapStructOverrides,
        UAssetAPI.CustomSerializationFlags customFlags,
        UAssetAPI.IAssetResolver resolver = null)
    {
        var asset = new UAssetApiAsset(objectVersion, objectVersionUE5, customVersions, mappings, customFlags)
        {
            FilePath = stagedUassetPath,
            AssetResolver = resolver
        };

        if (mapStructOverrides != null)
        {
            foreach (var kv in mapStructOverrides)
                asset.MapStructTypeOverride[kv.Key] = kv.Value;
        }

        using var reader = asset.PathToReader(stagedUassetPath);
        asset.Read(reader);
        return asset;
    }

    private static IDictionary<string, Tuple<UAssetAPI.UnrealTypes.FString, UAssetAPI.UnrealTypes.FString>> ResolveMapStructTypeOverrides()
    {
        var result = new Dictionary<string, Tuple<UAssetAPI.UnrealTypes.FString, UAssetAPI.UnrealTypes.FString>>(StringComparer.OrdinalIgnoreCase);
        var source = UserSettings.Default?.CurrentDir?.Versioning?.MapStructTypes;
        if (source == null)
            return result;

        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            var keyType = kv.Value.Key;
            var valueType = kv.Value.Value;
            if (string.IsNullOrWhiteSpace(keyType) || string.IsNullOrWhiteSpace(valueType))
                continue;

            result[kv.Key] = new Tuple<UAssetAPI.UnrealTypes.FString, UAssetAPI.UnrealTypes.FString>(
                UAssetAPI.UnrealTypes.FString.FromString(keyType),
                UAssetAPI.UnrealTypes.FString.FromString(valueType));
        }

        return result;
    }

    /// <summary>Hands UAssetAPI the packages a blueprint depends on, extracted from the provider on demand.</summary>
    private sealed class ProviderAssetResolver(CUE4ParseViewModel owner) : UAssetAPI.IAssetResolver
    {
        private readonly ConcurrentDictionary<string, (byte[] Uasset, byte[] Uexp)?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public bool TryResolve(string packagePath, out byte[] uasset, out byte[] uexp)
        {
            var found = _cache.GetOrAdd(packagePath, owner.ExtractPackageFiles);
            uasset = found?.Uasset;
            uexp = found?.Uexp;
            return found != null;
        }
    }

    private (byte[] Uasset, byte[] Uexp)? ExtractPackageFiles(string packagePath)
    {
        try
        {
            foreach (var candidate in BuildDependencyKeyCandidates(packagePath))
            {
                if (!Provider.Files.TryGetValue(candidate, out var file) || !Provider.TrySavePackage(file, out var assets))
                    continue;

                byte[] uasset = null, uexp = null;
                foreach (var (path, bytes) in assets)
                {
                    var extension = Path.GetExtension(path);
                    if (extension.Equals(".uasset", StringComparison.OrdinalIgnoreCase) || extension.Equals(".umap", StringComparison.OrdinalIgnoreCase))
                        uasset = bytes;
                    else if (extension.Equals(".uexp", StringComparison.OrdinalIgnoreCase))
                        uexp = bytes;
                }
                if (uasset != null)
                    return (uasset, uexp);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not extract {Package} for UAssetAPI", packagePath);
        }
        return null;
    }

    private bool TryStageDependencyPackageFromProvider(
        string packageName,
        string tempDirectory,
        HashSet<string> stagedDependencyKeys)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return false;

        foreach (var candidate in BuildDependencyKeyCandidates(packageName))
        {
            if (stagedDependencyKeys.Contains(candidate))
                continue;

            if (!Provider.Files.TryGetValue(candidate, out var dependencyEntry))
                continue;

            if (!Provider.TrySavePackage(dependencyEntry, out var assets) || assets.Count == 0)
                continue;

            foreach (var kvp in assets)
            {
                var relativePath = kvp.Key.Replace('\\', '/').TrimStart('/');
                var absolutePath = Path.Combine(tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? tempDirectory);
                File.WriteAllBytes(absolutePath, kvp.Value);
            }

            stagedDependencyKeys.Add(candidate);
            return true;
        }

        return false;
    }

    private IEnumerable<string> BuildDependencyKeyCandidates(string packageName)
    {
        var normalized = packageName.Trim().Trim('"').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;
        if (normalized.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            yield break;

        var basePath = normalized;
        if (basePath.Contains('.', StringComparison.Ordinal))
            basePath = basePath.Substring(0, basePath.IndexOf('.', StringComparison.Ordinal));
        basePath = basePath.Trim('/');
        if (string.IsNullOrWhiteSpace(basePath))
            yield break;

        var logicalCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            basePath
        };
        if (basePath.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
        {
            var rest = basePath.Substring("Game/".Length);
            logicalCandidates.Add($"Content/{rest}");
            if (!string.IsNullOrWhiteSpace(Provider.ProjectName))
                logicalCandidates.Add($"{Provider.ProjectName}/Content/{rest}");
        }

        foreach (var logical in logicalCandidates)
        {
            foreach (var ext in new[] { ".uasset", ".umap" })
            {
                var expectedSuffix = $"{logical}{ext}";
                foreach (var key in Provider.Files.Keys)
                {
                    if (key.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase))
                        yield return key;
                }
            }
        }
    }

    private static bool TryInjectBytecodeOffsets(JObject root, UAssetApiAsset asset)
    {
        if (root["Exports"] is not JArray exportNodes || asset?.Exports == null || asset.Exports.Count == 0)
            return false;

        var max = Math.Min(exportNodes.Count, asset.Exports.Count);
        var changed = false;
        for (var i = 0; i < max; i++)
        {
            if (asset.Exports[i] is not UAssetAPI.ExportTypes.StructExport structExport || structExport.ScriptBytecode == null)
                continue;
            if (exportNodes[i] is not JObject exportObject || exportObject["ScriptBytecode"] is not JArray scriptArray)
                continue;

            changed |= InjectOffsetsForScript(scriptArray, structExport.ScriptBytecode, asset, i);
        }

        return changed;
    }

    private static bool InjectOffsetsForScript(JArray scriptArray, KismetExpression[] expressions, UAssetApiAsset asset, int exportArrayIndex)
    {
        if (scriptArray.Count == 0 || expressions.Length == 0)
            return false;

        var count = Math.Min(scriptArray.Count, expressions.Length);
        var changed = false;
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            var size = GetSerializedExpressionSize(expressions[i], asset);

            if (scriptArray[i] is JObject expressionObject)
            {
                var rebuilt = new JObject
                {
                    // Keep StatementIndex aligned with actual bytecode offsets used by CUE4Parse stmt markers.
                    ["StatementIndex"] = offset,
                    ["ExportArrayIndex"] = exportArrayIndex,
                    ["offset"] = offset,
                    ["size"] = size
                };

                foreach (var property in expressionObject.Properties())
                {
                    if (string.Equals(property.Name, "StatementIndex", StringComparison.Ordinal) ||
                        string.Equals(property.Name, "ExportArrayIndex", StringComparison.Ordinal))
                        continue;
                    rebuilt[property.Name] = property.Value;
                }

                scriptArray[i] = rebuilt;
                changed = true;
            }

            offset += size;
        }

        return changed;
    }

    private static int GetSerializedExpressionSize(KismetExpression expression, UAssetApiAsset asset)
    {
        if (expression == null || asset == null)
            return 0;

        using var stream = new MemoryStream();
        using var writer = new UAssetAPI.AssetBinaryWriter(stream, asset);
        return ExpressionSerializer.WriteExpression(expression, writer);
    }

    private bool TryGetOrOpenLinkedTab(GameFile entry, string titleExtra, Func<GameFile, bool, bool> opener, out TabItem tab)
    {
        tab = TabControl.TabsItems.FirstOrDefault(x =>
            x?.Entry != null &&
            string.Equals(x.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TitleExtra, titleExtra, StringComparison.Ordinal));
        if (tab != null)
        {
            TabControl.SelectedTab = tab;
            return true;
        }

        if (!opener(entry, true))
            return false;

        tab = TabControl.SelectedTab;
        return tab != null && string.Equals(tab.TitleExtra, titleExtra, StringComparison.Ordinal);
    }

    private static bool TryGetCurrentEditorLine(TabItem sourceTab, out string lineText)
    {
        lineText = null;
        var editor = AvalonEditor.YesWeEditor;
        if (editor?.Document == null || sourceTab.Document == null)
            return false;
        if (!ReferenceEquals(editor.Document, sourceTab.Document))
            return false;

        var offset = Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength);
        var line = editor.Document.GetLineByOffset(offset);
        lineText = editor.Document.GetText(line.Offset, line.Length);
        return !string.IsNullOrWhiteSpace(lineText);
    }

    private static bool TryExtractStatementIndex(string lineText, Regex regex, out int statementIndex)
    {
        statementIndex = -1;
        var match = regex.Match(lineText ?? string.Empty);
        if (!match.Success || !match.Groups.TryGetValue("idx", out var group))
            return false;

        return int.TryParse(group.Value, out statementIndex);
    }

    private bool FocusTokenInTab(TabItem targetTab, string token)
    {
        if (targetTab?.Document == null || string.IsNullOrWhiteSpace(token))
            return false;

        var lineNumber = targetTab.Document.Text.GetNameLineNumber(token);
        if (lineNumber < 1)
            return false;

        targetTab.TextToFind = token;
        targetTab.ScrollTrigger = token;
        TabControl.SelectedTab = targetTab;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var editor = AvalonEditor.YesWeEditor;
            if (editor?.Document == null)
                return;
            if (!ReferenceEquals(editor.Document, targetTab.Document))
                return;

            var line = editor.Document.GetLineByNumber(lineNumber);
            editor.Select(line.Offset, line.Length);
            editor.ScrollToLine(lineNumber);
            editor.TextArea.Caret.BringCaretToView();
        });

        return true;
    }

    private bool TryDecompilePseudoFromEditedJson(GameFile entry, string jsonText, out string pseudo, out string error)
    {
        pseudo = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(jsonText))
        {
            error = "Blueprint JSON is empty.";
            return false;
        }

        var tempDirectory = Path.Combine(Path.GetTempPath(), "FModel", "LivePseudoPreview", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            var normalizedPath = entry.Path.Replace('\\', '/').TrimStart('/');
            var tempAssetPath = Path.Combine(tempDirectory, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(tempAssetPath) ?? tempDirectory);

            UAssetApiAsset editedAsset;
            try
            {
                editedAsset = UAssetApiAsset.DeserializeJson(jsonText);
            }
            catch (Exception ex)
            {
                error = $"JSON parse error: {ex.Message}";
                return false;
            }

            editedAsset.Write(tempAssetPath);

            var tempProvider = new DefaultFileProvider(tempDirectory, SearchOption.AllDirectories, Provider.Versions, StringComparer.OrdinalIgnoreCase)
            {
                MappingsContainer = Provider.MappingsContainer,
                ReadScriptData = Provider.ReadScriptData,
                ReadShaderMaps = Provider.ReadShaderMaps,
                ReadNaniteData = Provider.ReadNaniteData
            };

            tempProvider.Initialize();
            var package = tempProvider.LoadPackage(entry.Path);
            var mappings = tempProvider.MappingsForGame ?? Provider.MappingsForGame ?? new TypeMappings();
            pseudo = TryDecompileBlueprintToPseudo(package, mappings);
            if (string.IsNullOrWhiteSpace(pseudo))
            {
                error = "No class export found in edited blueprint.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private void UpdateLinkedPseudoPreviewTab(GameFile entry, string text, bool createIfMissing, TabItem sourceTab = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateInlinePseudoPreviewPane(entry, text, sourceTab);

            var previousSelection = TabControl.SelectedTab;
            var linkedTab = TabControl.TabsItems.FirstOrDefault(x =>
                x?.Entry != null &&
                string.Equals(x.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.TitleExtra, "Decompiled C++", StringComparison.Ordinal));

            if (linkedTab == null && createIfMissing)
            {
                if (!TabControl.CanAddTabs)
                    return;

                TabControl.AddTab(entry);
                linkedTab = TabControl.SelectedTab;
                if (linkedTab == null)
                    return;
                linkedTab.TitleExtra = "Decompiled C++";
                linkedTab.Highlighter = AvalonExtensions.HighlighterSelector("cpp");
            }

            if (linkedTab == null)
                return;

            linkedTab.SetDocumentText(text, false, false, isReadOnly: true);

            if (previousSelection != null && !ReferenceEquals(previousSelection, linkedTab))
                TabControl.SelectedTab = previousSelection;
        });
    }

    private void UpdateInlinePseudoPreviewPane(GameFile entry, string text, TabItem sourceTab = null)
    {
        if (sourceTab != null &&
            sourceTab.Entry != null &&
            string.Equals(sourceTab.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sourceTab.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
        {
            sourceTab.SetSecondaryDocumentText(text);
        }

        foreach (var tab in TabControl.TabsItems)
        {
            if (tab == null || ReferenceEquals(tab, sourceTab) || tab.Entry == null)
                continue;
            if (!string.Equals(tab.Entry.Path, entry.Path, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(tab.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
                continue;

            tab.SetSecondaryDocumentText(text);
        }
    }

    /// <summary>The blueprint's classes as pseudo C++ (with the UAssetAPI statement annotations), or null when it has no class export.</summary>
    public string DecompileBlueprintPseudo(GameFile entry)
    {
        var package = Provider.LoadPackage(entry);
        var pseudo = TryDecompileBlueprintToPseudo(package, Provider.MappingsForGame ?? new TypeMappings());
        if (string.IsNullOrWhiteSpace(pseudo))
            return null;
        try
        {
            return TryAugmentPseudoWithUAssetApi(entry, package, pseudo);
        }
        catch (Exception e)
        {
            // the annotations are a bonus: the text without them is still worth reading
            Log.Warning(e, "Could not annotate the pseudo code of {Path} with UAssetAPI", entry.Path);
            return pseudo + $"\n// UAssetAPI annotations failed: {e.GetType().Name}: {e.Message}";
        }
    }

    private static string TryDecompileBlueprintToPseudo(IPackage package, TypeMappings mappings)
    {
        var classExports = new List<UClass>();
        UClassCookedMetaData cookedMetaData = null;
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            var pointer = new FPackageIndex(package, i + 1).ResolvedObject;
            if (pointer?.Object is null)
                continue;

            var dummy = ((AbstractUePackage) package).ConstructObject(pointer.Class, package);
            if (dummy is UClass && pointer.Object.Value is UClass uc)
            {
                if (!classExports.Contains(uc))
                    classExports.Add(uc);
            }
            else if (dummy is UClassCookedMetaData && pointer.Object.Value is UClassCookedMetaData cmd)
            {
                cookedMetaData ??= cmd;
            }
        }

        if (classExports.Count == 0)
            return null;

        // Prefer blueprint-generated class first, then keep deterministic naming order.
        var orderedClasses = classExports
            .OrderByDescending(x => x is UBlueprintGeneratedClass)
            .ThenBy(x => x.Name)
            .ToList();

        var classesWithParents = ExpandWithParentBlueprintClasses(orderedClasses);

        var builder = new StringBuilder(8192);
        builder.AppendLine($"/* Decompiled {classesWithParents.Count} class export(s) including parent blueprint chain */");
        for (var idx = 0; idx < classesWithParents.Count; idx++)
        {
            var cls = classesWithParents[idx];
            string pseudo;
            try
            {
                pseudo = cls.DecompileBlueprintToPseudo(mappings, cookedMetaData);
            }
            catch (Exception e)
            {
                // the other classes of the chain are still worth reading
                Log.Warning(e, "Could not decompile class {Class} to pseudo code", cls.Name);
                var frame = e.StackTrace?.Split('\n').Select(o => o.Trim()).FirstOrDefault(o => o.StartsWith("at ", StringComparison.Ordinal));
                pseudo = $"/* CLASS DECOMPILE FAILED ({cls.Name}): {e.GetType().Name}: {e.Message} */\n// {frame}";
            }
            if (string.IsNullOrWhiteSpace(pseudo))
                continue;

            if (builder.Length > 0)
                builder.AppendLine();
            builder.AppendLine($"// ===== Export {idx + 1}/{classesWithParents.Count}: {cls.Name} ({cls.GetType().Name}) =====");
            builder.AppendLine(pseudo.TrimEnd());
        }

        return builder.ToString().TrimEnd();
    }

    private static List<UClass> ExpandWithParentBlueprintClasses(List<UClass> classes)
    {
        var result = new List<UClass>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (classes == null || classes.Count == 0)
            return result;

        foreach (var cls in classes)
        {
            if (cls == null)
                continue;

            var chain = new List<UClass>();
            var current = cls;
            var guard = 0;
            while (current != null && guard++ < 32)
            {
                chain.Add(current);
                var super = current.SuperStruct?.Load<UStruct>();
                if (super is not UClass superClass)
                    break;

                var generatedBy = superClass.ClassGeneratedBy;
                var isBlueprintLikeParent = superClass is UBlueprintGeneratedClass || (generatedBy != null && !generatedBy.IsNull);
                if (!isBlueprintLikeParent)
                    break; // native/non-blueprint parent
                current = superClass;
            }

            chain.Reverse(); // parent first, then child
            foreach (var entry in chain)
            {
                var key = GetClassDecompileKey(entry);
                if (seen.Add(key))
                    result.Add(entry);
            }
        }

        return result;
    }

    private static string GetClassDecompileKey(UClass cls)
    {
        if (cls == null)
            return string.Empty;
        return $"{cls.Name}|{cls.GetType().FullName}|{cls.ClassGeneratedBy}";
    }

    private string TryAugmentPseudoWithUAssetApi(GameFile entry, IPackage package, string pseudo)
    {
        if (string.IsNullOrWhiteSpace(pseudo))
            return pseudo;

        var pseudoFunctionNames = ExtractPseudoFunctionNames(pseudo);

        if (!TryStagePackageToTemporaryDirectory(entry, out var stagedUassetPath, out var tempDirectory, out _))
            return pseudo;

        try
        {
            var objectVersion = ResolveObjectVersion(package);
            var objectVersionUE5 = ResolveObjectVersionUE5(package);
            var customVersions = ResolveCustomVersions(package, objectVersion, objectVersionUE5);
            var mappings = ResolveUsmapFromCurrentSettings();
            if (package.HasFlags(EPackageFlags.PKG_UnversionedProperties) && mappings == null)
                return pseudo;

            var asset = LoadBlueprintAssetWithDependencyPreload(
                entry,
                stagedUassetPath,
                tempDirectory,
                objectVersion,
                objectVersionUE5,
                customVersions,
                mappings);
            var fallbackBlocks = BuildUAssetApiFallbackBlocks(asset, pseudoFunctionNames);
            if (fallbackBlocks.Count == 0)
                return pseudo;

            var builder = new StringBuilder(pseudo.Length + 2048);
            builder.AppendLine(pseudo.TrimEnd());
            builder.AppendLine();
            builder.AppendLine("/*");
            builder.AppendLine(" * UAssetAPI Fallback");
            builder.AppendLine(" * The following functions were not fully emitted by CUE4Parse pseudo decompiler.");
            builder.AppendLine(" * Generated from UAssetAPI function export metadata for manual reconstruction.");
            builder.AppendLine(" */");
            foreach (var block in fallbackBlocks)
            {
                builder.AppendLine();
                builder.AppendLine(block);
            }

            return builder.ToString();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to augment pseudo decompilation with UAssetAPI fallback for {Path}", entry.Path);
            return pseudo;
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static HashSet<string> ExtractPseudoFunctionNames(string pseudo)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(pseudo))
            return names;

        foreach (Match match in PseudoFunctionDeclarationRegex.Matches(pseudo))
        {
            if (!match.Success || !match.Groups.TryGetValue("name", out var group))
                continue;
            if (!string.IsNullOrWhiteSpace(group.Value))
                names.Add(group.Value);
        }

        return names;
    }

    private static List<string> BuildUAssetApiFallbackBlocks(UAssetApiAsset asset, HashSet<string> existingFunctionNames)
    {
        var result = new List<string>();
        if (asset?.Exports == null || asset.Exports.Count == 0)
            return result;

        foreach (var function in asset.Exports.OfType<UAssetAPI.ExportTypes.FunctionExport>())
        {
            var functionName = function.ObjectName.Value?.Value;
            if (string.IsNullOrWhiteSpace(functionName) || existingFunctionNames.Contains(functionName))
                continue;

            var loadedPropertyCount = function.LoadedProperties?.Length ?? 0;
            var scriptCount = function.ScriptBytecode?.Length ?? 0;
            var scriptSize = function.ScriptBytecodeSize;
            var rawLength = function.ScriptBytecodeRaw?.Length ?? 0;
            var tokenPreview = function.ScriptBytecode == null
                ? "none"
                : string.Join(", ", function.ScriptBytecode.Take(32).Select(x => x?.Token.ToString() ?? "null"));
            if (function.ScriptBytecode is { Length: > 32 })
                tokenPreview += ", ...";

            var block = new StringBuilder(512);
            block.AppendLine($"// [UAssetAPI fallback] function {functionName}");
            block.AppendLine($"// Flags: {function.FunctionFlags}");
            block.AppendLine($"// LoadedProperties: {loadedPropertyCount}");
            block.AppendLine($"// ScriptBytecodeCount: {scriptCount}");
            block.AppendLine($"// ScriptBytecodeSize: {scriptSize}");
            block.AppendLine($"// ScriptBytecodeRawLength: {rawLength}");
            block.AppendLine($"// ScriptTokenPreview: {tokenPreview}");
            block.AppendLine($"void {functionName}()");
            block.AppendLine("{");
            block.AppendLine($"    // FunctionFlags: {function.FunctionFlags}");
            block.AppendLine("    // TODO: reconstruct body from ScriptTokenPreview + Blueprint JSON bytecode.");
            block.AppendLine("}");
            result.Add(block.ToString().TrimEnd());
        }

        return result;
    }

    public void SaveBlueprintJsonToAsset(TabItem tabViewModel)
    {
        try
        {
            if (!TryDeserializeBlueprintJson(tabViewModel, out var asset))
                return;

            var outputUassetPath = GetBlueprintWriteBackPath(tabViewModel.Entry, ".uasset");
            Directory.CreateDirectory(Path.GetDirectoryName(outputUassetPath) ?? UserSettings.Default.RawDataDirectory);
            asset.Write(outputUassetPath);

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully wrote blueprint asset to {FilePath}", outputUassetPath);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully wrote ", Constants.WHITE);
                FLogger.Link(Path.GetFileName(outputUassetPath), outputUassetPath, true);
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error(ex, "Failed to write blueprint JSON back to uasset for {Path}", tabViewModel.Entry?.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to serialize/write blueprint: {ex.Message}", Constants.WHITE, true));
        }
    }

    public void SaveBlueprintJsonAsJson(TabItem tabViewModel)
    {
        if (!ValidateBlueprintJsonTab(tabViewModel))
            return;

        var savePath = PromptSaveFilePath(
            "Save Blueprint JSON As",
            "JSON files (*.json)|*.json|All files (*.*)|*.*",
            Path.ChangeExtension(tabViewModel.Entry?.Name ?? "blueprint", ".json"),
            UserSettings.Default.PropertiesDirectory);
        if (string.IsNullOrWhiteSpace(savePath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? UserSettings.Default.PropertiesDirectory);
            File.WriteAllText(savePath, tabViewModel.Document.Text, new UTF8Encoding(false));

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully wrote blueprint json to {FilePath}", savePath);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully wrote ", Constants.WHITE);
                FLogger.Link(Path.GetFileName(savePath), savePath, true);
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error(ex, "Failed to save blueprint json for {Path}", tabViewModel.Entry?.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to save blueprint json: {ex.Message}", Constants.WHITE, true));
        }
    }

    public void SaveBlueprintJsonAsAsset(TabItem tabViewModel)
    {
        if (!TryDeserializeBlueprintJson(tabViewModel, out var asset))
            return;

        var extension = tabViewModel.Entry?.Extension?.Equals("umap", StringComparison.OrdinalIgnoreCase) == true ? ".umap" : ".uasset";
        var savePath = PromptSaveFilePath(
            "Save Blueprint As UAsset",
            "Unreal assets (*.uasset;*.umap)|*.uasset;*.umap|All files (*.*)|*.*",
            Path.ChangeExtension(tabViewModel.Entry?.Name ?? "blueprint", extension),
            UserSettings.Default.RawDataDirectory);
        if (string.IsNullOrWhiteSpace(savePath))
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(savePath) ?? UserSettings.Default.RawDataDirectory);
            asset.Write(savePath);

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully wrote blueprint asset to {FilePath}", savePath);
            FLogger.Append(ELog.Information, () =>
            {
                FLogger.Text("Successfully wrote ", Constants.WHITE);
                FLogger.Link(Path.GetFileName(savePath), savePath, true);
            });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error(ex, "Failed to save blueprint asset for {Path}", tabViewModel.Entry?.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to save blueprint uasset: {ex.Message}", Constants.WHITE, true));
        }
    }

    public void ApplyRestrictedPseudoEditsToJson(TabItem tabViewModel)
    {
        if (!ValidateBlueprintJsonTab(tabViewModel))
            return;

        if (tabViewModel.SecondaryDocument == null)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Pseudo preview document is empty.", Constants.WHITE, true));
            return;
        }

        var pseudoText = tabViewModel.SecondaryDocument.Text;
        if (string.IsNullOrWhiteSpace(pseudoText))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Pseudo preview document is empty.", Constants.WHITE, true));
            return;
        }

        JObject root;
        try
        {
            root = JObject.Parse(tabViewModel.Document.Text);
        }
        catch (Exception ex)
        {
            FLogger.Append(ELog.Error, () => FLogger.Text($"Blueprint JSON parse failed: {ex.Message}", Constants.WHITE, true));
            return;
        }

        if (root["Exports"] is not JArray exports || exports.Count == 0)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Blueprint JSON has no Exports array.", Constants.WHITE, true));
            return;
        }

        var directiveEdits = ExtractPseudoDirectiveEdits(pseudoText);
        var jumpEdits = ExtractRestrictedPseudoJumpEdits(pseudoText);
        if (directiveEdits.Count == 0 && jumpEdits.Count == 0)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                "No pseudo edits found. Use @uapi:{...} for generic patch or edit goto Label_x for jump-style tokens.",
                Constants.WHITE,
                true));
            return;
        }

        var applied = 0;
        var failed = 0;
        foreach (var edit in directiveEdits)
        {
            if (!TryApplySinglePseudoDirectiveEdit(exports, edit))
            {
                failed++;
                continue;
            }

            applied++;
        }
        foreach (var edit in jumpEdits)
        {
            if (!TryApplySinglePseudoJumpEdit(exports, edit))
            {
                failed++;
                continue;
            }

            applied++;
        }

        if (applied == 0)
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Pseudo write-back made no changes. Parsed patches={directiveEdits.Count}, jumps={jumpEdits.Count}, failed={failed}.",
                Constants.WHITE,
                true));
            return;
        }

        var json = root.ToString(Formatting.Indented);
        tabViewModel.SetDocumentText(json, false, false, isReadOnly: false);
        QueueLivePseudoPreviewFromJson(tabViewModel);

        FLogger.Append(ELog.Information, () => FLogger.Text(
            $"Applied pseudo edits to Blueprint JSON: {applied} success, {failed} failed (patches={directiveEdits.Count}, jumps={jumpEdits.Count}).",
            Constants.WHITE,
            true));
    }

    private static string AnnotatePseudoWithEditablePatches(string pseudoText, string jsonText)
    {
        if (string.IsNullOrWhiteSpace(pseudoText) || string.IsNullOrWhiteSpace(jsonText))
            return pseudoText;

        if (!TryGetScriptExportsFromJson(jsonText, out _, out var exports))
            return pseudoText;

        var lines = pseudoText.Replace("\r\n", "\n").Split('\n');
        var contexts = ExtractPseudoStatementContexts(pseudoText);
        if (contexts.Count == 0)
            return pseudoText;

        var changed = false;
        foreach (var context in contexts)
        {
            var lineIndex = context.LineNumber - 1;
            if (lineIndex < 0 || lineIndex >= lines.Length)
                continue;
            if (lines[lineIndex].Contains(PseudoUapiPatchMarker, StringComparison.Ordinal))
                continue;
            if (!TryFindScriptNodeByFunctionAndStatement(exports, context.FunctionName, context.StatementIndex, out var scriptNode, out _))
                continue;

            var patch = BuildEditablePatchFromScriptNode(scriptNode);
            if (patch.Count == 0)
                continue;

            lines[lineIndex] += $" /*{PseudoUapiPatchMarker}{patch.ToString(Formatting.None)}*/";
            changed = true;
        }

        return changed ? string.Join("\n", lines) : pseudoText;
    }

    private static JObject BuildEditablePatchFromScriptNode(JObject scriptNode)
    {
        var patch = new JObject();
        if (scriptNode == null)
            return patch;

        foreach (var property in scriptNode.Properties())
        {
            if (NonEditableScriptMetadataKeys.Contains(property.Name))
                continue;
            patch[property.Name] = property.Value?.DeepClone();
        }

        return patch;
    }

    private static List<PseudoDirectiveEdit> ExtractPseudoDirectiveEdits(string pseudoText)
    {
        var edits = new List<PseudoDirectiveEdit>();
        foreach (var context in ExtractPseudoStatementContexts(pseudoText))
        {
            if (!TryExtractPatchObjectFromLine(context.LineText, out var patchObject))
                continue;

            edits.Add(new PseudoDirectiveEdit(
                context.FunctionName,
                context.StatementIndex,
                context.Token,
                context.LineNumber,
                context.LineText,
                patchObject));
        }

        return edits;
    }

    private static List<PseudoJumpEdit> ExtractRestrictedPseudoJumpEdits(string pseudoText)
    {
        var edits = new List<PseudoJumpEdit>();
        if (string.IsNullOrWhiteSpace(pseudoText))
            return edits;

        foreach (var context in ExtractPseudoStatementContexts(pseudoText))
        {
            var jumpMatch = PseudoEditableJumpRegex.Match(context.LineText);
            if (!jumpMatch.Success)
                continue;

            if (!jumpMatch.Groups.TryGetValue("target", out var targetGroup) ||
                !jumpMatch.Groups.TryGetValue("token", out var tokenGroup))
                continue;

            if (!int.TryParse(targetGroup.Value, out var targetOffset))
                continue;

            edits.Add(new PseudoJumpEdit(
                context.FunctionName,
                context.StatementIndex,
                targetOffset,
                tokenGroup.Value,
                context.LineNumber,
                context.LineText));
        }

        return edits;
    }

    private static List<PseudoStatementContext> ExtractPseudoStatementContexts(string pseudoText)
    {
        var contexts = new List<PseudoStatementContext>();
        if (string.IsNullOrWhiteSpace(pseudoText))
            return contexts;

        var lines = pseudoText.Replace("\r\n", "\n").Split('\n');
        var currentFunction = string.Empty;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i] ?? string.Empty;
            var signatureMatch = PseudoFunctionSignatureLineRegex.Match(line);
            if (signatureMatch.Success && signatureMatch.Groups.TryGetValue("name", out var fnGroup))
                currentFunction = fnGroup.Value;

            var stmtMatch = PseudoStatementRegex.Match(line);
            if (!stmtMatch.Success || !stmtMatch.Groups.TryGetValue("idx", out var stmtGroup))
                continue;
            if (!int.TryParse(stmtGroup.Value, out var statementIndex))
                continue;

            var token = string.Empty;
            var tokenMatch = PseudoTokenRegex.Match(line);
            if (tokenMatch.Success && tokenMatch.Groups.TryGetValue("token", out var tokenGroup))
                token = tokenGroup.Value;

            contexts.Add(new PseudoStatementContext(currentFunction, statementIndex, token, i + 1, line));
        }

        return contexts;
    }

    private static bool TryExtractPatchObjectFromLine(string lineText, out JObject patchObject)
    {
        patchObject = null;
        if (string.IsNullOrWhiteSpace(lineText))
            return false;

        var markerIndex = lineText.IndexOf(PseudoUapiPatchMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return false;

        var start = markerIndex + PseudoUapiPatchMarker.Length;
        if (!TryReadJsonObjectAt(lineText, start, out var patchJson))
            return false;

        try
        {
            patchObject = JObject.Parse(patchJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadJsonObjectAt(string text, int startIndex, out string jsonObject)
    {
        jsonObject = string.Empty;
        if (string.IsNullOrEmpty(text) || startIndex < 0 || startIndex >= text.Length)
            return false;

        var i = startIndex;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length || text[i] != '{')
            return false;

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var pos = i; pos < text.Length; pos++)
        {
            var c = text[pos];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                depth++;
                continue;
            }

            if (c != '}')
                continue;

            depth--;
            if (depth != 0)
                continue;

            jsonObject = text.Substring(i, pos - i + 1);
            return true;
        }

        return false;
    }

    private static bool TryApplySinglePseudoDirectiveEdit(JArray exports, PseudoDirectiveEdit edit)
    {
        if (edit == null || edit.PatchObject == null || edit.PatchObject.Count == 0)
            return false;

        if (!TryFindScriptNodeByFunctionAndStatement(exports, edit.FunctionName, edit.StatementIndex, out var scriptNode, out var objectName))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Pseudo @uapi patch target not found: stmt={edit.StatementIndex}, function={edit.FunctionName ?? "<unknown>"} (line {edit.LineNumber}).",
                Constants.WHITE,
                true));
            return false;
        }

        ApplyPatchToScriptNode(scriptNode, edit.PatchObject);
        return true;
    }

    private static void ApplyPatchToScriptNode(JObject scriptNode, JObject patchObject)
    {
        if (scriptNode == null || patchObject == null)
            return;

        foreach (var property in patchObject.Properties())
        {
            if (NonEditableScriptMetadataKeys.Contains(property.Name))
                continue;
            scriptNode[property.Name] = property.Value?.DeepClone();
        }
    }

    private static bool TryApplySinglePseudoJumpEdit(JArray exports, PseudoJumpEdit edit)
    {
        if (edit == null)
            return false;

        if (!IsSupportedEditableJumpToken(edit.Token))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Pseudo write-back skipped unsupported token {edit.Token} at line {edit.LineNumber}.",
                Constants.WHITE,
                true));
            return false;
        }

        if (!TryFindScriptNodeByFunctionAndStatement(exports, edit.FunctionName, edit.StatementIndex, out var scriptNode, out var objectName))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Pseudo write-back target not found: stmt={edit.StatementIndex}, function={edit.FunctionName ?? "<unknown>"} (line {edit.LineNumber}).",
                Constants.WHITE,
                true));
            return false;
        }

        if (!TrySetJumpTargetOffset(scriptNode, edit.TargetOffset))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text(
                $"Pseudo write-back could not set jump target for stmt={edit.StatementIndex} in {objectName} (line {edit.LineNumber}).",
                Constants.WHITE,
                true));
            return false;
        }

        return true;
    }

    private static bool IsSupportedEditableJumpToken(string token) =>
        string.Equals(token, "EX_Jump", StringComparison.Ordinal) ||
        string.Equals(token, "EX_JumpIfNot", StringComparison.Ordinal) ||
        string.Equals(token, "EX_Skip", StringComparison.Ordinal) ||
        string.Equals(token, "EX_PushExecutionFlow", StringComparison.Ordinal);

    private static bool TryFindScriptNodeByFunctionAndStatement(
        JArray exports,
        string functionName,
        int statementIndex,
        out JObject scriptNode,
        out string objectName)
    {
        scriptNode = null;
        objectName = string.Empty;

        if (!string.IsNullOrWhiteSpace(functionName))
        {
            foreach (var exportToken in exports)
            {
                if (exportToken is not JObject exportObject)
                    continue;
                if (!string.Equals(exportObject["ObjectName"]?.Value<string>(), functionName, StringComparison.Ordinal))
                    continue;
                if (TryFindStatementInExportScript(exportObject, statementIndex, out scriptNode))
                {
                    objectName = functionName;
                    return true;
                }
            }
        }

        foreach (var exportToken in exports)
        {
            if (exportToken is not JObject exportObject)
                continue;
            if (!TryFindStatementInExportScript(exportObject, statementIndex, out scriptNode))
                continue;

            objectName = exportObject["ObjectName"]?.Value<string>() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryFindStatementInExportScript(JObject exportObject, int statementIndex, out JObject scriptNode)
    {
        scriptNode = null;
        if (exportObject?["ScriptBytecode"] is not JArray scriptArray)
            return false;

        foreach (var node in scriptArray)
        {
            if (node is not JObject expressionObject)
                continue;

            var stmtToken = expressionObject["StatementIndex"];
            if (stmtToken == null)
                continue;
            if (!int.TryParse(stmtToken.ToString(), out var stmtValue))
                continue;
            if (stmtValue != statementIndex)
                continue;

            scriptNode = expressionObject;
            return true;
        }

        return false;
    }

    private static bool TrySetJumpTargetOffset(JObject scriptNode, int targetOffset)
    {
        if (scriptNode == null)
            return false;

        if (scriptNode["Offset"] != null)
        {
            scriptNode["Offset"] = targetOffset;
            return true;
        }

        if (scriptNode["CodeOffset"] != null)
        {
            scriptNode["CodeOffset"] = targetOffset;
            return true;
        }

        if (scriptNode["PushingAddress"] != null)
        {
            scriptNode["PushingAddress"] = targetOffset;
            return true;
        }

        return false;
    }

    private sealed class PseudoJumpEdit
    {
        public string FunctionName { get; }
        public int StatementIndex { get; }
        public int TargetOffset { get; }
        public string Token { get; }
        public int LineNumber { get; }
        public string LineText { get; }

        public PseudoJumpEdit(string functionName, int statementIndex, int targetOffset, string token, int lineNumber, string lineText)
        {
            FunctionName = functionName;
            StatementIndex = statementIndex;
            TargetOffset = targetOffset;
            Token = token;
            LineNumber = lineNumber;
            LineText = lineText;
        }
    }

    private sealed class PseudoDirectiveEdit
    {
        public string FunctionName { get; }
        public int StatementIndex { get; }
        public string Token { get; }
        public int LineNumber { get; }
        public string LineText { get; }
        public JObject PatchObject { get; }

        public PseudoDirectiveEdit(string functionName, int statementIndex, string token, int lineNumber, string lineText, JObject patchObject)
        {
            FunctionName = functionName;
            StatementIndex = statementIndex;
            Token = token;
            LineNumber = lineNumber;
            LineText = lineText;
            PatchObject = patchObject;
        }
    }

    private sealed class PseudoStatementContext
    {
        public string FunctionName { get; }
        public int StatementIndex { get; }
        public string Token { get; }
        public int LineNumber { get; }
        public string LineText { get; }

        public PseudoStatementContext(string functionName, int statementIndex, string token, int lineNumber, string lineText)
        {
            FunctionName = functionName;
            StatementIndex = statementIndex;
            Token = token;
            LineNumber = lineNumber;
            LineText = lineText;
        }
    }

    private static bool TryGetScriptExportsFromJson(string jsonText, out JObject root, out JArray exports)
    {
        root = null;
        exports = null;
        if (string.IsNullOrWhiteSpace(jsonText))
            return false;

        try
        {
            root = JObject.Parse(jsonText);
            exports = root["Exports"] as JArray;
            return exports != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool ValidateBlueprintJsonTab(TabItem tabViewModel)
    {
        if (tabViewModel?.Document == null)
            return false;

        if (tabViewModel.IsReadOnly || !string.Equals(tabViewModel.TitleExtra, "Blueprint JSON", StringComparison.Ordinal))
        {
            FLogger.Append(ELog.Warning, () => FLogger.Text("Current tab is not an editable Blueprint JSON tab", Constants.WHITE, true));
            return false;
        }

        return true;
    }

    private static bool TryDeserializeBlueprintJson(TabItem tabViewModel, out UAssetApiAsset asset)
    {
        asset = null;
        if (!ValidateBlueprintJsonTab(tabViewModel))
            return false;

        try
        {
            asset = UAssetApiAsset.DeserializeJson(tabViewModel.Document.Text);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse blueprint json for {Path}", tabViewModel.Entry?.Path);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Failed to parse blueprint json: {ex.Message}", Constants.WHITE, true));
            return false;
        }
    }

    private static string PromptSaveFilePath(string title, string filter, string fileName, string initialDirectory)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = fileName,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Directory.Exists(initialDirectory) ? initialDirectory : null
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    /// <summary>
    /// Writes the edited blueprint (and its .uexp/.ubulk) and adds the files, unchanged otherwise, to the pak packer at the path of the original asset.
    /// </summary>
    private bool TryAddBlueprintToPack(GameFile entry, UAssetApiAsset asset, out string addedPaths, out string error)
    {
        addedPaths = string.Empty;
        error = string.Empty;
        if (entry == null || asset == null)
        {
            error = "Nothing to add: the entry or the blueprint asset is missing.";
            return false;
        }

        var stage = "init";
        try
        {
            stage = "prepare_paths";
            var extension = string.Equals(entry.Extension, "umap", StringComparison.OrdinalIgnoreCase) ? ".umap" : ".uasset";
            var directory = Path.Combine(Path.GetTempPath(), "FloraPack", "edited", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var written = Path.Combine(directory, Path.GetFileNameWithoutExtension(entry.Path) + extension);

            stage = "serialize_asset";
            PrepareBlueprintAssetForWriteBack(asset);
            asset.Write(written);

            stage = "collect_written_files";
            var parts = CollectWrittenAssetPaths(written);
            if (parts.Count == 0)
            {
                error = $"No asset files were written for {entry.Path}.";
                return false;
            }

            // pak paths are relative to the mount point: GameName/Content/...
            var basePath = entry.Path.Replace('\\', '/').TrimStart('/');
            var files = parts.Select(o => (PakPath: Path.ChangeExtension(basePath, Path.GetExtension(o)).Replace('\\', '/'), Data: File.ReadAllBytes(o))).ToList();

            stage = "add_to_pack";
            var origin = $"{FModel.Localization.LocalizationManager.Instance.Get("Pack_OriginBlueprint")}: {entry.Path}";
            Application.Current.Dispatcher.Invoke(() => DockPanels.ShowPackWorkspace().AddEditedFiles(files, origin));
            addedPaths = string.Join(", ", files.Select(o => o.PakPath));
            TryDeleteDirectory(directory);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to add blueprint to the pak packer for {Path} at stage {Stage}", entry?.Path, stage);
            error = $"Stage={stage}; {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static List<string> CollectWrittenAssetPaths(string baseAssetPath)
    {
        var result = new List<string>();
        var candidateExtensions = new[] { ".uasset", ".umap", ".uexp", ".ubulk", ".uptnl", ".m.ubulk" };
        foreach (var ext in candidateExtensions)
        {
            var path = Path.ChangeExtension(baseAssetPath, ext);
            if (File.Exists(path))
                result.Add(path);
        }

        // Keep a deterministic order so patch pak input is stable.
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private bool TryStagePackageToTemporaryDirectory(GameFile entry, out string stagedUassetPath, out string tempDirectory, out string error)
    {
        stagedUassetPath = string.Empty;
        error = string.Empty;
        tempDirectory = Path.Combine(Path.GetTempPath(), "FModel", "UAssetApi", Guid.NewGuid().ToString("N"));
        if (!Provider.TrySavePackage(entry, out var assets) || assets.Count == 0)
        {
            error = $"Could not extract package bytes for '{entry.Name}'.";
            return false;
        }

        var expectedPath = entry.Path.Replace('\\', '/').TrimStart('/');
        string fallbackUassetRelativePath = null;

        foreach (var kvp in assets)
        {
            var relativePath = kvp.Key.Replace('\\', '/').TrimStart('/');
            var absolutePath = Path.Combine(tempDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? tempDirectory);
            File.WriteAllBytes(absolutePath, kvp.Value);

            if (relativePath.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase))
            {
                fallbackUassetRelativePath ??= relativePath;
                if (string.Equals(relativePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    fallbackUassetRelativePath = relativePath;
                }
            }
        }

        if (string.IsNullOrEmpty(fallbackUassetRelativePath))
        {
            error = $"No .uasset file bytes were extracted for '{entry.Name}'.";
            TryDeleteDirectory(tempDirectory);
            return false;
        }

        stagedUassetPath = Path.Combine(tempDirectory, fallbackUassetRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // ignored
        }
    }

    private static string GetBlueprintWriteBackPath(GameFile entry, string extension)
    {
        if (entry == null)
            throw new ArgumentNullException(nameof(entry));

        var rawPath = entry.Path;
        if (string.IsNullOrWhiteSpace(rawPath))
            rawPath = entry.Name;
        if (string.IsNullOrWhiteSpace(rawPath))
            rawPath = "blueprint";

        var normalizedPath = rawPath.Replace('\\', '/').TrimStart('/');
        var targetRelativePath = UserSettings.Default.KeepDirectoryStructure ? normalizedPath : (entry.Name ?? Path.GetFileName(normalizedPath));
        if (string.IsNullOrWhiteSpace(targetRelativePath))
            targetRelativePath = "blueprint";

        var withExtension = Path.ChangeExtension(targetRelativePath, extension);
        return Path.Combine(UserSettings.Default.RawDataDirectory, withExtension).Replace('\\', '/');
    }

    private static void PrepareBlueprintAssetForWriteBack(UAssetApiAsset asset)
    {
        if (asset == null)
            return;

        EnsureFieldNonNull(asset, "Imports", static () => new List<UAssetAPI.Import>());
        EnsureFieldNonNull(asset, "Exports", static () => new List<UAssetAPI.ExportTypes.Export>());
        EnsureFieldNonNull(asset, "DependsMap", static () => new List<int[]>());
        EnsureFieldNonNull(asset, "SoftPackageReferenceList", static () => new List<UAssetAPI.UnrealTypes.FString>());
        EnsureFieldNonNull(asset, "AssetRegistryRecords", static () => new List<UAssetAPI.FAssetRegistryRecord>());
        EnsureFieldNonNull(asset, "ChunkIDs", static () => Array.Empty<int>());
        EnsureFieldNonNull(asset, "Generations", () => new List<UAssetAPI.FGenerationInfo>
        {
            new(asset.Exports?.Count ?? 0, asset.GetNameMapIndexList()?.Count ?? 0)
        });
        EnsureFieldNonNull(asset, "AdditionalPackagesToCook", static () => new List<UAssetAPI.UnrealTypes.FString>());

        asset.Imports ??= new List<UAssetAPI.Import>();
        asset.Exports ??= new List<UAssetAPI.ExportTypes.Export>();
        asset.DependsMap ??= new List<int[]>();
        asset.SoftPackageReferenceList ??= new List<UAssetAPI.UnrealTypes.FString>();
        asset.AssetRegistryRecords ??= new List<UAssetAPI.FAssetRegistryRecord>();
        asset.Exports = asset.Exports.Where(x => x != null).ToList();

        for (var i = 0; i < asset.DependsMap.Count; i++)
            asset.DependsMap[i] ??= Array.Empty<int>();

        foreach (var export in asset.Exports)
        {
            export.ObjectName ??= UAssetAPI.UnrealTypes.FName.FromString(asset, "None");
            export.OuterIndex ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
            export.ClassIndex ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
            export.SuperIndex ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
            export.TemplateIndex ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
            export.Extras ??= Array.Empty<byte>();

            export.SerializationBeforeSerializationDependencies ??= new List<UAssetAPI.UnrealTypes.FPackageIndex>();
            export.CreateBeforeSerializationDependencies ??= new List<UAssetAPI.UnrealTypes.FPackageIndex>();
            export.SerializationBeforeCreateDependencies ??= new List<UAssetAPI.UnrealTypes.FPackageIndex>();
            export.CreateBeforeCreateDependencies ??= new List<UAssetAPI.UnrealTypes.FPackageIndex>();

            if (export is UAssetAPI.ExportTypes.RawExport rawExport)
                rawExport.Data ??= Array.Empty<byte>();

            if (export is UAssetAPI.ExportTypes.NormalExport normalExport)
                normalExport.Data ??= new List<UAssetAPI.PropertyTypes.Objects.PropertyData>();

            if (export is UAssetAPI.ExportTypes.FieldExport fieldExport)
            {
                fieldExport.Field ??= new UAssetAPI.FieldTypes.UField();
                fieldExport.Field.Next ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
            }

            if (export is UAssetAPI.ExportTypes.StructExport structExport)
            {
                structExport.SuperStruct ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
                structExport.Children ??= Array.Empty<UAssetAPI.UnrealTypes.FPackageIndex>();
                structExport.LoadedProperties ??= Array.Empty<UAssetAPI.FieldTypes.FProperty>();
                if (structExport.ScriptBytecode == null && structExport.ScriptBytecodeRaw == null)
                {
                    structExport.ScriptBytecodeSize = 0;
                    structExport.ScriptBytecodeRaw = Array.Empty<byte>();
                }
            }

            if (export is UAssetAPI.ExportTypes.ClassExport classExport)
            {
                classExport.FuncMap ??= new UAssetAPI.UnrealTypes.TMap<UAssetAPI.UnrealTypes.FName, UAssetAPI.UnrealTypes.FPackageIndex>();
                classExport.Interfaces ??= Array.Empty<UAssetAPI.ExportTypes.SerializedInterfaceReference>();
                classExport.ClassWithin ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
                classExport.ClassGeneratedBy ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
                classExport.ClassDefaultObject ??= new UAssetAPI.UnrealTypes.FPackageIndex(0);
                classExport.ClassConfigName ??= UAssetAPI.UnrealTypes.FName.FromString(asset, "None");
            }
        }
    }

    private static void EnsureFieldNonNull<T>(object target, string fieldName, Func<T> factory)
        where T : class
    {
        if (target == null)
            return;

        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null)
            return;

        if (!typeof(T).IsAssignableFrom(field.FieldType))
            return;

        if (field.GetValue(target) == null)
            field.SetValue(target, factory());
    }

    /// <summary>The engine version UAssetAPI needs to read loose packages of the loaded game (the pak packer's texture editor).</summary>
    public UAssetAPI.UnrealTypes.EngineVersion ResolvePackEngineVersion() =>
        UAssetApiAsset.GetEngineVersion(
            (UAssetAPI.UnrealTypes.ObjectVersion) Provider.Versions.Ver.FileVersionUE4,
            (UAssetAPI.UnrealTypes.ObjectVersionUE5) Math.Max(Provider.Versions.Ver.FileVersionUE5, -1), null);

    /// <summary>A .usmap file for the loaded game (a jmap is converted), or null.</summary>
    public string ResolvePackUsmapPath() =>
        ResolveUsmapPathsFromCurrentSettings().FirstOrDefault(o => !string.IsNullOrWhiteSpace(o) && File.Exists(o));

    private UAssetAPI.UnrealTypes.ObjectVersion ResolveObjectVersion(IPackage package)
    {
        var ue4Version = package.Summary.FileVersionUE.FileVersionUE4;
        if (ue4Version <= 0)
            ue4Version = Provider.Versions.Ver.FileVersionUE4;

        return (UAssetAPI.UnrealTypes.ObjectVersion)ue4Version;
    }

    private UAssetAPI.UnrealTypes.ObjectVersionUE5 ResolveObjectVersionUE5(IPackage package)
    {
        var ue5Version = package.Summary.FileVersionUE.FileVersionUE5;
        if (ue5Version < 0)
            ue5Version = Provider.Versions.Ver.FileVersionUE5;

        return (UAssetAPI.UnrealTypes.ObjectVersionUE5)ue5Version;
    }

    private List<UAssetAPI.CustomVersion> ResolveCustomVersions(
        IPackage package,
        UAssetAPI.UnrealTypes.ObjectVersion objectVersion,
        UAssetAPI.UnrealTypes.ObjectVersionUE5 objectVersionUE5)
    {
        var merged = new Dictionary<Guid, UAssetAPI.CustomVersion>();

        // Seed with engine-default custom versions so struct/function layouts stay aligned
        // even when package/provider custom versions are missing.
        var guessedEngineVersion = UAssetApiAsset.GetEngineVersion(objectVersion, objectVersionUE5, null);
        if (guessedEngineVersion != UAssetAPI.UnrealTypes.EngineVersion.UNKNOWN)
        {
            foreach (var version in UAssetApiAsset.GetDefaultCustomVersionContainer(guessedEngineVersion))
                merged[version.Key] = version;
        }

        var source = package.Summary.CustomVersionContainer?.Versions;
        if (source == null || source.Length == 0)
            source = Provider.Versions.CustomVersions?.Versions;

        if (source == null || source.Length == 0)
            return merged.Values.ToList();

        foreach (var version in source)
        {
            var guid = UAssetAPI.UAPUtils.GUID(version.Key.A, version.Key.B, version.Key.C, version.Key.D);
            merged[guid] = new UAssetAPI.CustomVersion(guid, version.Version);
        }

        return merged.Values.ToList();
    }

    private UAssetApiUsmap ResolveUsmapFromCurrentSettings()
    {
        foreach (var usmapPath in ResolveUsmapPathsFromCurrentSettings())
        {
            if (string.IsNullOrWhiteSpace(usmapPath) || !File.Exists(usmapPath))
                continue;

            try
            {
                return new UAssetApiUsmap(usmapPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load usmap for UAssetAPI from {Path}; trying next candidate", usmapPath);
            }
        }

        return null;
    }

    private static readonly object _jmapUsmapCacheLock = new();

    /// <summary>
    /// UAssetAPI only reads .usmap, so a .usmap copy of the loaded .jmap mappings is written next to downloaded mappings.
    /// The file name carries the source size and timestamp, so a changed .jmap produces a new cache file.
    /// </summary>
    private string EnsureJmapUsmapCache(FileJmapTypeMappingsProvider jmap)
    {
        if (jmap.MappingsForGame is not { } mappings || !File.Exists(jmap.FilePath))
            return null;

        var source = new FileInfo(jmap.FilePath);
        var cachePath = Path.Combine(UserSettings.Default.OutputDirectory, ".data",
            $"{source.Name}.{source.Length:x}{source.LastWriteTimeUtc.Ticks:x}.usmap");

        lock (_jmapUsmapCacheLock)
        {
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length > 0)
                return cachePath;

            try
            {
                UsmapWriter.Write(mappings, cachePath);
                Log.Information("Converted jmap mappings '{Jmap}' to usmap cache '{Usmap}'", source.Name, cachePath);
                return cachePath;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to convert jmap mappings '{Jmap}' to usmap", source.Name);
                return null;
            }
        }
    }

    private IEnumerable<string> ResolveUsmapPathsFromCurrentSettings()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Provider.MappingsContainer is FileJmapTypeMappingsProvider jmap &&
            EnsureJmapUsmapCache(jmap) is { } jmapUsmapPath)
        {
            yielded.Add(jmapUsmapPath);
            yield return jmapUsmapPath;
        }

        if (UserSettings.IsEndpointValid(EEndpointType.Mapping, out var endpoint) &&
            endpoint.Overwrite &&
            !string.IsNullOrWhiteSpace(endpoint.FilePath) &&
            !FileJmapTypeMappingsProvider.IsJmapFile(endpoint.FilePath) &&
            File.Exists(endpoint.FilePath))
        {
            if (yielded.Add(endpoint.FilePath))
                yield return endpoint.FilePath;
        }

        if (Provider.MappingsContainer is FileUsmapTypeMappingsProvider m && File.Exists(m.FilePath))
        {
            if (yielded.Add(m.FilePath))
                yield return m.FilePath;
        }

        var mappingsFolder = Path.Combine(UserSettings.Default.OutputDirectory, ".data");
        if (!Directory.Exists(mappingsFolder))
            yield break;

        var mappedFiles = new DirectoryInfo(mappingsFolder)
            .GetFiles("*.usmap")
            .OrderByDescending(f => f.LastWriteTimeUtc);
        foreach (var mapped in mappedFiles)
        {
            if (yielded.Add(mapped.FullName))
                yield return mapped.FullName;
        }
    }

    private void SaveAndPlaySound(CancellationToken cancellationToken, string fullPath, string ext, byte[] data, bool saveAudio, bool updateUi)
    {
        if (fullPath.StartsWith('/')) fullPath = fullPath[1..];
        var extLower = ext.ToLowerInvariant();
        var baseFilePath = UserSettings.Default.KeepDirectoryStructure ? fullPath : fullPath.SubstringAfterLast('/');
        var combinedPath = Path.Combine(UserSettings.Default.AudioDirectory, baseFilePath);
        var savedAudioPath = Path.ChangeExtension(combinedPath, extLower).Replace('\\', '/');

        if (saveAudio)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(savedAudioPath);
            Directory.CreateDirectory(directory);

            bool conversionSuccess = true;
            if (UserSettings.Default.ConvertAudioOnBulkExport && extLower is not "wav")
            {
                if (AudioPlayerViewModel.TryConvert(savedAudioPath, data, out string wavFilePath))
                    savedAudioPath = wavFilePath;
                else
                {
                    Interlocked.Increment(ref FailedExportCount);
                    return;
                }
            }
            else
            {
                using var stream = new FileStream(savedAudioPath, FileMode.Create, FileAccess.Write);
                stream.Write(data);
            }

            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully saved {FilePath}", savedAudioPath);
            if (updateUi && conversionSuccess)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(Path.GetFileName(savedAudioPath), savedAudioPath, true);
                });
            }

            return;
        }

        if (!updateUi)
            return;

        // TODO
        // since we are currently in a thread, the audio player's lifetime (memory-wise) will keep the current thread up and running until fmodel itself closes
        // the solution would be to kill the current thread at this line and then open the audio player without "Application.Current.Dispatcher.Invoke"
        // but the ThreadWorkerViewModel is an idiot and doesn't understand we want to kill the current thread inside the current thread and continue the code
        Application.Current.Dispatcher.Invoke(delegate
        {
            var audioPlayer = DockPanels.ShowAudioPlayer();
            audioPlayer.Load(data, savedAudioPath);
        });
    }

    private void SaveExport(UObject export, bool updateUi = true)
    {
        var toSave = new Exporter(export, UserSettings.Default.ExportOptions);
        var toSaveDirectory = new DirectoryInfo(UserSettings.Default.ModelDirectory);
        if (toSave.TryWriteToDir(toSaveDirectory, out var label, out var savedFilePath))
        {
            Interlocked.Increment(ref ExportedCount);
            Log.Information("Successfully saved {FilePath}", savedFilePath);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully saved ", Constants.WHITE);
                    FLogger.Link(label, savedFilePath, true);
                });
            }
        }
        else
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error("{FileName} could not be saved", export.Name);
            FLogger.Append(ELog.Error, () => FLogger.Text($"Could not save '{export.Name}'", Constants.WHITE, true));
        }
    }

    private readonly object _rawData = new ();
    public void ExportData(GameFile entry, bool updateUi = true)
    {
        if (Provider.TrySavePackage(entry, out var assets))
        {
            string path = UserSettings.Default.RawDataDirectory;
            Parallel.ForEach(assets, kvp =>
            {
                lock (_rawData)
                {
                    path = Path.Combine(UserSettings.Default.RawDataDirectory, UserSettings.Default.KeepDirectoryStructure ? kvp.Key : kvp.Key.SubstringAfterLast('/')).Replace('\\', '/');
                    Directory.CreateDirectory(path.SubstringBeforeLast('/'));
                    File.WriteAllBytes(path, kvp.Value);
                }
            });

            Interlocked.Increment(ref ExportedCount);
            Log.Information("{FileName} successfully exported", entry.Name);
            if (updateUi)
            {
                FLogger.Append(ELog.Information, () =>
                {
                    FLogger.Text("Successfully exported ", Constants.WHITE);
                    FLogger.Link(entry.Name, path, true);
                });
            }
        }
        else
        {
            Interlocked.Increment(ref FailedExportCount);
            Log.Error("{FileName} could not be exported", entry.Name);
            if (updateUi)
                FLogger.Append(ELog.Error, () => FLogger.Text($"Could not export '{entry.Name}'", Constants.WHITE, true));
        }
    }

    private static bool HasFlag(EBulkType a, EBulkType b)
    {
        return (a & b) == b;
    }
}