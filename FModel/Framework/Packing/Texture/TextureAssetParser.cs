#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.UnrealTypes;
using UAssetAPI.Unversioned;

namespace FModel.Framework.Packing.Texture;

public static class TextureAssetParser
{
    public const int MaxTextureDimension = 32768;
    public const long MaxTexturePixels = 268_435_456;
    private const uint BulkDataPayloadAtEndOfFile = 1u << 0;
    private const uint BulkDataForceInlinePayload = 1u << 6;
    private const uint BulkDataPayloadInSeparateFile = 1u << 8;
    private const uint BulkDataOptionalPayload = 1u << 11;
    private const uint BulkDataSize64Bit = 1u << 13;
    private const uint BulkDataBadDataVersion = 1u << 15;
    private const uint BulkDataDuplicateNonOptionalPayload = 1u << 14;
    private const uint BulkDataNoOffsetFixUp = 1u << 16;
    private const uint BulkDataLazyLoadable = 1u << 18;
    private const uint KnownBulkDataFlagsMask =
        (1u << 0) |
        (1u << 1) |
        (1u << 2) |
        (1u << 3) |
        (1u << 4) |
        (1u << 5) |
        (1u << 6) |
        (1u << 7) |
        (1u << 8) |
        (1u << 9) |
        (1u << 10) |
        (1u << 11) |
        (1u << 12) |
        (1u << 13) |
        (1u << 14) |
        (1u << 15) |
        (1u << 16) |
        (1u << 17) |
        (1u << 18) |
        (1u << 28) |
        (1u << 29) |
        (1u << 30);

    /// <summary>
    /// 加载纹理资产。注意 <paramref name="usmapPath"/> 同时支持 .usmap 与 .jmap：
    /// <see cref="UAssetAPI.Unversioned.Usmap"/> 会按文件扩展名自动选择解析器。
    /// </summary>
    public static TextureAssetInfo Load(string assetPath, EngineVersion engineVersion, string? usmapPath, string? formatHint = null)
    {
        var fullAssetPath = Path.GetFullPath(assetPath);
        var uexpPath = Path.ChangeExtension(fullAssetPath, ".uexp");
        var ubulkPath = Path.ChangeExtension(fullAssetPath, ".ubulk");

        if (!File.Exists(uexpPath))
            throw new FileNotFoundException("Matching .uexp was not found.", uexpPath);

        var mappings = string.IsNullOrWhiteSpace(usmapPath) ? null : new Usmap(usmapPath);
        UAsset asset = new(fullAssetPath, engineVersion, mappings, CustomSerializationFlags.SkipParsingExports);

        var rawExport = asset.Exports.OfType<RawExport>().FirstOrDefault()
            ?? throw new InvalidOperationException("No raw export was found. This tool currently expects cooked texture exports.");

        var exportData = rawExport.Data;
        var uexpBytes = File.ReadAllBytes(uexpPath);
        if (uexpBytes.Length < exportData.Length)
            throw new InvalidOperationException("UEXP is shorter than the export data length.");

        var footer = uexpBytes[exportData.Length..];
        var ubulkBytes = File.Exists(ubulkPath) ? File.ReadAllBytes(ubulkPath) : [];
        var format = TextureFormats.DetectFormat(exportData, asset.GetNameMapIndexList().Select(name => name.Value), formatHint);

        var layout = ResolveTextureLayout(exportData, uexpBytes, ubulkBytes.Length, format);
        var width = layout.Width;
        var height = layout.Height;
        var externalMipCount = layout.Placements.Count(placement => placement.Storage == TextureMipStorage.Ubulk);
        var inlineMips = layout.Mips
            .Where(mip => layout.Placements.Any(placement =>
                placement.Index == mip.Index && placement.Storage != TextureMipStorage.Ubulk))
            .ToList();
        var markers = layout.Markers;

        var sentinelOffset = markers.Count > 0 ? markers[^1] : -1;

        return new TextureAssetInfo(
            fullAssetPath,
            uexpPath,
            ubulkPath,
            exportData,
            footer,
            ubulkBytes,
            format,
            width,
            height,
            layout.Mips,
            externalMipCount,
            inlineMips,
            markers,
            sentinelOffset,
            layout.Placements);
    }

    private static TextureLayout ResolveTextureLayout(byte[] exportData, byte[] uexpData, int ubulkLength, TextureFormatInfo format)
    {
        var candidates = FindTextureSizeCandidates(exportData).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException("Failed to find texture dimensions in the export data.");

        Exception? lastError = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var fullMipChain = BuildMipChain(candidate.Width, candidate.Height, format).ToList();
                var headerLayout = ResolveBulkHeaderLayout(uexpData, exportData.Length, ubulkLength, fullMipChain);
                if (headerLayout is not null)
                    return headerLayout;

                foreach (var externalRun in ResolveExternalMipRuns(fullMipChain, ubulkLength))
                {
                    var externalIndexes = externalRun.Mips.Select(mip => mip.Index).ToHashSet();
                    var candidateInlineMips = fullMipChain
                        .Where(mip => !externalIndexes.Contains(mip.Index))
                        .ToList();
                    var (inlineMips, markers, inlinePayloadOffsets) = ResolveInlineMipLayout(exportData, candidateInlineMips);
                    if (externalRun.Mips.Count == 0 && inlineMips.Count == 0)
                        continue;

                    var placements = new List<TextureMipPlacement>();
                    placements.AddRange(externalRun.Mips.Select(mip => new TextureMipPlacement(
                        mip.Index,
                        mip.Width,
                        mip.Height,
                        mip.ByteLength,
                        TextureMipStorage.Ubulk,
                        mip.StorageOffset)));
                    for (var i = 0; i < inlineMips.Count; i++)
                    {
                        var mip = inlineMips[i];

                        // 载荷偏移由"相邻尺寸标记"推得，而不是 marker - ByteLength。
                        //
                        // 每级 mip 的排布是：[载荷][该级尺寸标记/尾部元数据]，而这段元数据
                        // 的长度逐级不同（实测 48/52/28/16 交错）。marker - ByteLength 隐含了
                        // "元数据长度恒定"的假设，只有第一级恰好成立，从第二级起就会把载荷
                        // 起点算到前一级的数据里，产出静默损坏的贴图。
                        // 相邻标记之差才是真实的"载荷 + 本级元数据"步长。
                        int payloadOffset = i == 0
                            ? markers[0] - inlineMips[0].ByteLength
                            : markers[i - 1] + 16;

                        placements.Add(new TextureMipPlacement(
                            mip.Index,
                            mip.Width,
                            mip.Height,
                            mip.ByteLength,
                            TextureMipStorage.UexpInline,
                            payloadOffset));
                    }

                    return new TextureLayout(
                        candidate.Width,
                        candidate.Height,
                        externalRun.Mips.Concat(inlineMips).OrderBy(mip => mip.Index).ToArray(),
                        markers,
                        placements.OrderBy(placement => placement.Index).ToArray());
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var candidateSummary = string.Join(", ", candidates.Take(8).Select(candidate => $"{candidate.Width}x{candidate.Height}@{candidate.Offset}"));
        throw new InvalidOperationException(
            "Failed to resolve a supported texture mip layout from the export data. " +
            $"format={format.Name}, export={exportData.Length} bytes, uexp={uexpData.Length} bytes, ubulk={ubulkLength} bytes, " +
            $"candidates=[{candidateSummary}], last={lastError?.Message ?? "<none>"}",
            lastError);
    }

    private static IEnumerable<TextureSizeCandidate> FindTextureSizeCandidates(byte[] exportData)
    {
        var seen = new HashSet<(int Width, int Height)>();

        if (TryReadSaneDimensionPair(exportData, 4, out var fixedWidth, out var fixedHeight) &&
            seen.Add((fixedWidth, fixedHeight)))
        {
            yield return new TextureSizeCandidate(fixedWidth, fixedHeight, int.MaxValue);
        }

        var candidates = new List<TextureSizeCandidate>();
        for (var offset = 0; offset <= exportData.Length - 12; offset++)
        {
            var width = BitConverter.ToInt32(exportData, offset);
            var height = BitConverter.ToInt32(exportData, offset + 4);
            var depth = BitConverter.ToInt32(exportData, offset + 8);

            if (depth != 1 || !IsSaneTextureSize(width, height))
                continue;

            if (seen.Add((width, height)))
                candidates.Add(new TextureSizeCandidate(width, height, offset));
        }

        foreach (var candidate in candidates
                     .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
                     .ThenByDescending(candidate => Math.Max(candidate.Width, candidate.Height))
                     .ThenBy(candidate => candidate.Offset))
        {
            yield return candidate;
        }
    }

    private static bool TryReadSaneDimensionPair(byte[] exportData, int offset, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (offset < 0 || offset > exportData.Length - 8)
            return false;

        width = BitConverter.ToInt32(exportData, offset);
        height = BitConverter.ToInt32(exportData, offset + 4);
        return IsSaneDimension(width) && IsSaneDimension(height);
    }

    private static bool IsSaneDimension(int value)
    {
        return value > 0 && value <= MaxTextureDimension;
    }

    private static bool IsSaneTextureSize(int width, int height)
    {
        return IsSaneDimension(width) &&
               IsSaneDimension(height) &&
               (long)width * height <= MaxTexturePixels;
    }

    private static IEnumerable<TextureMip> BuildMipChain(int width, int height, TextureFormatInfo format)
    {
        var mipIndex = 0;
        var currentWidth = width;
        var currentHeight = height;
        while (true)
        {
            yield return new TextureMip(mipIndex, currentWidth, currentHeight, format.GetMipByteSize(currentWidth, currentHeight), 0);
            if (currentWidth == 1 && currentHeight == 1)
                break;

            currentWidth = Math.Max(1, currentWidth / 2);
            currentHeight = Math.Max(1, currentHeight / 2);
            mipIndex++;
        }
    }

    private static IEnumerable<ExternalMipRun> ResolveExternalMipRuns(IReadOnlyList<TextureMip> mips, int ubulkLength)
    {
        if (ubulkLength == 0)
        {
            yield return new ExternalMipRun(0, []);
            yield break;
        }

        var matches = new List<ExternalMipRun>();
        for (var start = 0; start < mips.Count; start++)
        {
            long total = 0;
            for (var end = start; end < mips.Count; end++)
            {
                total += mips[end].ByteLength;
                if (total == ubulkLength)
                {
                    matches.Add(new ExternalMipRun(start, WithStorageOffsets(mips, start, end)));
                    break;
                }

                if (total > ubulkLength)
                    break;
            }
        }

        foreach (var match in matches
                     .OrderBy(match => match.StartIndex == 0 ? 0 : 1)
                     .ThenBy(match => match.StartIndex)
                     .ThenByDescending(match => match.Mips.Count))
        {
            yield return match;
        }

        if (matches.Count == 0)
            throw new InvalidOperationException($"Could not match UBULK length {ubulkLength} to a contiguous section of the mip chain.");
    }

    private static List<TextureMip> WithStorageOffsets(IReadOnlyList<TextureMip> mips, int start, int end)
    {
        var result = new List<TextureMip>(end - start + 1);
        var offset = 0;
        for (var i = start; i <= end; i++)
        {
            var mip = mips[i];
            result.Add(mip with { StorageOffset = offset });
            offset += mip.ByteLength;
        }

        return result;
    }

    private static TextureLayout? ResolveBulkHeaderLayout(byte[] uexpData, int exportDataLength, int ubulkLength, IReadOnlyList<TextureMip> fullMipChain)
    {
        var records = FindBulkMipRecords(uexpData, exportDataLength, fullMipChain, ubulkLength)
            .GroupBy(record => record.Mip.Index)
            .Select(group => group
                .OrderBy(record => record.HeaderOffset)
                .ThenBy(record => record.Storage == TextureMipStorage.Ubulk ? 0 : 1)
                .First())
            .OrderBy(record => record.Mip.Index)
            .ToArray();

        if (records.Length == 0)
            return null;

        var firstMip = records[0].Mip.Index;
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i].Mip.Index != firstMip + i)
                return null;
        }

        var externalOffsets = ResolveExternalRecordOffsets(
            records.Where(record => record.Storage == TextureMipStorage.Ubulk).OrderBy(record => record.HeaderOffset).ToArray(),
            ubulkLength);

        var placements = new List<TextureMipPlacement>();
        foreach (var record in records)
        {
            var offset = record.Storage == TextureMipStorage.Ubulk
                ? externalOffsets[record]
                : record.PayloadOffset;
            placements.Add(new TextureMipPlacement(
                record.Mip.Index,
                record.Mip.Width,
                record.Mip.Height,
                record.Mip.ByteLength,
                record.Storage,
                offset));
        }

        var markers = records
            .Where(record => record.Storage != TextureMipStorage.Ubulk)
            .Select(record => record.DimensionOffset)
            .ToList();

        // 校验"外部"声明是否成立：声明为 Ubulk 的 mip 必须真的落在 .ubulk 里。
        //
        // 否则这个布局是假的、必须放弃，让内联路径接手。KARDS 这类资产没有 .ubulk
        // 文件（ubulkLength = 0），mip 载荷内联在 .uexp 里，但载荷之后的 FByteBulkData
        // 头会被误认成"外部 mip"记录 —— 于是这里只登记出一级 mip（4x4），
        // 而真实布局是 11 级内联。此前它会抢先返回这个残缺布局，内联路径再无机会执行，
        // 最终在 TextureReplacer.WriteReplacement 里以 Buffer.BlockCopy 越界告终。
        foreach (var placement in placements.Where(p => p.Storage == TextureMipStorage.Ubulk))
        {
            if (ubulkLength == 0)
                return null;
            if (placement.Offset < 0 || (long)placement.Offset + placement.ByteLength > ubulkLength)
                return null;
        }

        return new TextureLayout(
            fullMipChain[0].Width,
            fullMipChain[0].Height,
            records.Select(record => record.Mip).OrderBy(mip => mip.Index).ToArray(),
            markers,
            placements.OrderBy(placement => placement.Index).ToArray());
    }

    private static Dictionary<BulkMipRecord, int> ResolveExternalRecordOffsets(IReadOnlyList<BulkMipRecord> records, int ubulkLength)
    {
        var offsets = new Dictionary<BulkMipRecord, int>();
        var nextOffset = 0;
        foreach (var record in records)
        {
            var explicitOffset = record.BulkOffset;
            if (explicitOffset >= 0 && explicitOffset <= int.MaxValue &&
                explicitOffset + record.Mip.ByteLength <= ubulkLength)
            {
                offsets[record] = (int)explicitOffset;
            }
            else if (ubulkLength == record.Mip.ByteLength)
            {
                offsets[record] = 0;
            }
            else
            {
                offsets[record] = nextOffset;
            }

            nextOffset = offsets[record] + record.Mip.ByteLength;
        }

        return offsets;
    }

    private static IEnumerable<BulkMipRecord> FindBulkMipRecords(byte[] uexpData, int exportDataLength, IReadOnlyList<TextureMip> fullMipChain, int ubulkLength)
    {
        var byDimensions = fullMipChain.ToDictionary(mip => (mip.Width, mip.Height), mip => mip);
        for (var offset = 0; offset <= exportDataLength - 20; offset += 4)
        {
            foreach (var header in TryReadBulkHeaders(uexpData, offset))
            {
                if (header.SizeOnDisk <= 0 || header.SizeOnDisk > int.MaxValue)
                    continue;

                var payloadLength = (int)header.SizeOnDisk;
                foreach (var location in CandidateBulkPayloadLocations(header, payloadLength, uexpData.Length, exportDataLength))
                {
                    if (!TryReadMipDimensions(uexpData, location.DimensionOffset, byDimensions, out var mip))
                        continue;
                    if (payloadLength != mip.ByteLength)
                        continue;
                    if (location.Storage == TextureMipStorage.Ubulk && ubulkLength > 0 && payloadLength > ubulkLength)
                        continue;

                    yield return new BulkMipRecord(
                        offset,
                        location.DimensionOffset,
                        location.PayloadOffset,
                        header.BulkOffset,
                        location.Storage,
                        mip with { StorageOffset = location.PayloadOffset });
                }
            }
        }
    }

    private static IEnumerable<BulkHeaderCandidate> TryReadBulkHeaders(byte[] data, int offset)
    {
        if (offset > data.Length - 20)
            yield break;

        var flags = BitConverter.ToUInt32(data, offset);
        if ((flags & ~KnownBulkDataFlagsMask) != 0)
            yield break;

        foreach (var offsetSize in new[] { 8, 4 })
        {
            var cursor = offset + 4;
            var uses64BitSize = (flags & BulkDataSize64Bit) != 0;
            if (!TryReadCountAndSize(data, ref cursor, uses64BitSize, out var elementCount, out var sizeOnDisk))
                continue;
            if (!TryReadOffset(data, ref cursor, offsetSize, out var bulkOffset))
                continue;
            if ((flags & BulkDataBadDataVersion) != 0)
            {
                if (cursor > data.Length - 2)
                    continue;
                cursor += 2;
            }
            if ((flags & BulkDataDuplicateNonOptionalPayload) != 0)
            {
                var duplicateCursor = cursor + 4 + (uses64BitSize ? 8 : 4) + offsetSize;
                if (duplicateCursor > data.Length)
                    continue;
                cursor = duplicateCursor;
            }

            if (elementCount <= 0 || sizeOnDisk <= 0)
                continue;

            yield return new BulkHeaderCandidate(flags, elementCount, sizeOnDisk, bulkOffset, cursor);
        }
    }

    private static bool TryReadCountAndSize(byte[] data, ref int cursor, bool uses64BitSize, out long elementCount, out long sizeOnDisk)
    {
        elementCount = 0;
        sizeOnDisk = 0;
        if (uses64BitSize)
        {
            if (cursor > data.Length - 16)
                return false;
            elementCount = BitConverter.ToInt64(data, cursor);
            cursor += 8;
            sizeOnDisk = BitConverter.ToInt64(data, cursor);
            cursor += 8;
            return true;
        }

        if (cursor > data.Length - 8)
            return false;
        elementCount = BitConverter.ToInt32(data, cursor);
        cursor += 4;
        sizeOnDisk = BitConverter.ToUInt32(data, cursor);
        cursor += 4;
        return true;
    }

    private static bool TryReadOffset(byte[] data, ref int cursor, int offsetSize, out long bulkOffset)
    {
        bulkOffset = 0;
        if (offsetSize == 8)
        {
            if (cursor > data.Length - 8)
                return false;
            bulkOffset = BitConverter.ToInt64(data, cursor);
            cursor += 8;
            return true;
        }

        if (cursor > data.Length - 4)
            return false;
        bulkOffset = BitConverter.ToInt32(data, cursor);
        cursor += 4;
        return true;
    }

    private static IEnumerable<BulkPayloadLocation> CandidateBulkPayloadLocations(
        BulkHeaderCandidate header,
        int payloadLength,
        int uexpLength,
        int exportDataLength)
    {
        var flags = header.Flags;
        if ((flags & (BulkDataPayloadInSeparateFile | BulkDataOptionalPayload)) != 0)
        {
            yield return new BulkPayloadLocation(TextureMipStorage.Ubulk, 0, header.PayloadOffset);
            yield break;
        }

        if ((flags & BulkDataPayloadAtEndOfFile) != 0)
        {
            foreach (var absoluteOffset in CandidateUexpEndPayloadOffsets(header.BulkOffset, payloadLength, uexpLength))
            {
                yield return absoluteOffset >= exportDataLength
                    ? new BulkPayloadLocation(TextureMipStorage.UexpFooter, absoluteOffset - exportDataLength, header.PayloadOffset)
                    : new BulkPayloadLocation(TextureMipStorage.UexpInline, absoluteOffset, header.PayloadOffset);
            }
            yield break;
        }

        if ((flags & (BulkDataForceInlinePayload | BulkDataLazyLoadable)) != 0 || flags == 0)
        {
            yield return new BulkPayloadLocation(TextureMipStorage.UexpInline, header.PayloadOffset, header.PayloadOffset + payloadLength);
            yield break;
        }

        yield return new BulkPayloadLocation(TextureMipStorage.Ubulk, 0, header.PayloadOffset);
        yield return new BulkPayloadLocation(TextureMipStorage.UexpInline, header.PayloadOffset, header.PayloadOffset + payloadLength);
    }

    private static IEnumerable<int> CandidateUexpEndPayloadOffsets(long bulkOffset, int payloadLength, int uexpLength)
    {
        if (bulkOffset >= 0 && bulkOffset <= int.MaxValue &&
            bulkOffset + payloadLength <= uexpLength)
        {
            yield return (int)bulkOffset;
        }

        var tailOffset = uexpLength - payloadLength;
        if (tailOffset >= 0 && tailOffset != bulkOffset)
            yield return tailOffset;
    }

    private static bool TryReadMipDimensions(
        byte[] data,
        int offset,
        IReadOnlyDictionary<(int Width, int Height), TextureMip> mips,
        out TextureMip mip)
    {
        mip = default!;
        if (offset < 0 || offset > data.Length - 12)
            return false;

        var width = BitConverter.ToInt32(data, offset);
        var height = BitConverter.ToInt32(data, offset + 4);
        var depth = BitConverter.ToInt32(data, offset + 8);
        return depth == 1 && mips.TryGetValue((width, height), out mip!);
    }

    private static (List<TextureMip> Mips, List<int> Markers, List<int> PayloadOffsets) ResolveInlineMipLayout(byte[] exportData, IReadOnlyList<TextureMip> candidateInlineMips)
    {
        if (candidateInlineMips.Count == 0)
            return ([], [], []);

        var markers = new List<int>();
        var payloadOffsets = new List<int>();
        var inlineMips = new List<TextureMip>();

        // 推进基准必须是"上一级载荷的末尾"，不能是"上一级标记的位置"。
        //
        // KARDS 这类资产的排布是：每级 mip 的尺寸标记位于【该级载荷之后】——
        //   [头部][mip0 载荷][mip0 标记][mip1 载荷][mip1 标记]...
        // 若把 previousEnd 推进到上一级标记之后，下一级的标记就在它前面，
        // 会被 hit > previousEnd 直接排除，mip 链从第二级起全部丢失。
        var previousEnd = 0;
        foreach (var mip in candidateInlineMips)
        {
            var hits = FindDimensionMarkers(exportData, mip.Width, mip.Height);
            var match = hits
                .Where(hit => hit >= mip.ByteLength)
                .Select(hit => new { Marker = hit, PayloadStart = hit - mip.ByteLength })
                .Where(hit => hit.PayloadStart >= previousEnd)
                .Select(hit => (int?)hit.PayloadStart)
                .FirstOrDefault();

            if (match is null)
                break;

            inlineMips.Add(mip);
            markers.Add(match.Value + mip.ByteLength);
            payloadOffsets.Add(match.Value);
            previousEnd = match.Value + mip.ByteLength;
        }

        if (inlineMips.Count == 0)
            return ([], [], []);

        for (var i = 1; i < markers.Count; i++)
        {
            if (markers[i] <= markers[i - 1])
                throw new InvalidOperationException("Inline mip markers were not strictly increasing.");
        }

        for (var i = 0; i < inlineMips.Count; i++)
        {
            if (payloadOffsets[i] < 0)
                throw new InvalidOperationException($"Inline mip {inlineMips[i].Index} would start before the export data begins.");
            if (payloadOffsets[i] + inlineMips[i].ByteLength > exportData.Length)
                throw new InvalidOperationException($"Inline mip {inlineMips[i].Index} would end after the export data.");
        }

        return (inlineMips, markers, payloadOffsets);
    }

    private static List<int> FindDimensionMarkers(byte[] exportData, int width, int height)
    {
        var widthBytes = BitConverter.GetBytes(width);
        var heightBytes = BitConverter.GetBytes(height);
        var depthBytes = BitConverter.GetBytes(1);
        var hits = new List<int>();

        for (var i = 0; i <= exportData.Length - 16; i++)
        {
            if (Matches(exportData, i, widthBytes) &&
                Matches(exportData, i + 4, heightBytes) &&
                Matches(exportData, i + 8, depthBytes))
            {
                hits.Add(i);
            }
        }

        return hits;
    }

    private static bool Matches(byte[] source, int offset, byte[] pattern)
    {
        for (var i = 0; i < pattern.Length; i++)
        {
            if (source[offset + i] != pattern[i])
                return false;
        }

        return true;
    }

    private sealed record TextureSizeCandidate(int Width, int Height, int Offset);

    private sealed record ExternalMipRun(int StartIndex, IReadOnlyList<TextureMip> Mips);

    private sealed record BulkHeaderCandidate(
        uint Flags,
        long ElementCount,
        long SizeOnDisk,
        long BulkOffset,
        int PayloadOffset);

    private sealed record BulkMipRecord(
        int HeaderOffset,
        int DimensionOffset,
        int PayloadOffset,
        long BulkOffset,
        TextureMipStorage Storage,
        TextureMip Mip);

    private sealed record BulkPayloadLocation(
        TextureMipStorage Storage,
        int PayloadOffset,
        int DimensionOffset);

    private sealed record TextureLayout(
        int Width,
        int Height,
        IReadOnlyList<TextureMip> Mips,
        IReadOnlyList<int> Markers,
        IReadOnlyList<TextureMipPlacement> Placements);
}
