#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace FModel.Framework.Packing.Texture;

public static class TextureReplacer
{
    public static void ExtractMipPayloads(TextureAssetInfo info, string outputDirectory)
    {
        foreach ((int index, byte[] payload) in ExtractMipPayloads(info))
        {
            File.WriteAllBytes(Path.Combine(Path.GetFullPath(outputDirectory), $"mip{index}.bin"), payload);
        }
    }

    /// <summary>
    /// 以内存形式取出各 mip 的<b>已压缩</b>载荷（不解码像素）。
    ///
    /// mip 数据可能分布在三个位置：.ubulk 文件、.uexp 内联、或导出数据尾部。
    /// 这里按 <see cref="TextureAssetInfo.MipPlacements"/> 逐个取出原始字节，
    /// 供"格式相同则直接搬运"的快速路径使用（无需映射文件、不损失画质）。
    ///
    /// 返回值按 mip 的 <c>Index</c> 升序排列。
    /// </summary>
    public static IReadOnlyList<(int Index, byte[] Payload)> ExtractMipPayloads(TextureAssetInfo info)
    {
        var result = new List<(int Index, byte[] Payload)>(info.Mips.Count);

        foreach (TextureMipPlacement placement in info.MipPlacements.OrderBy(p => p.Index))
        {
            byte[] source = placement.Storage switch
            {
                TextureMipStorage.Ubulk => info.UbulkData,
                TextureMipStorage.UexpInline => info.ExportData,
                _ => info.UexpFooter,
            };

            if (placement.Offset < 0 || placement.Offset + placement.ByteLength > source.Length)
            {
                throw new InvalidOperationException(
                    $"Mip {placement.Index} 的位置超出范围（offset={placement.Offset}, length={placement.ByteLength}, " +
                    $"来源={placement.Storage}，可用 {source.Length} 字节）。");
            }

            byte[] payload = new byte[placement.ByteLength];
            Buffer.BlockCopy(source, placement.Offset, payload, 0, payload.Length);
            result.Add((placement.Index, payload));
        }

        return result;
    }

    /// <summary>
    /// 检查两套纹理布局的 mip 结构是否兼容：格式、尺寸、mip 数量与各 mip 字节长度全部一致。
    /// 兼容时可以直接搬运压缩载荷（快速路径），不需要解码/重编码。
    /// </summary>
    public static bool IsLayoutCompatible(TextureAssetInfo source, TextureAssetInfo target)
    {
        if (!string.Equals(source.Format.Name, target.Format.Name, StringComparison.OrdinalIgnoreCase))
            return false;

        if (source.Mips.Count != target.Mips.Count)
            return false;

        for (int i = 0; i < source.Mips.Count; i++)
        {
            if (source.Mips[i].ByteLength != target.Mips[i].ByteLength)
                return false;
        }

        return true;
    }

    public static void WriteReplacement(TextureAssetInfo info, byte[][] mipPayloads, string outputAssetPath)
    {
        if (mipPayloads.Length != info.Mips.Count)
            throw new InvalidOperationException($"Expected {info.Mips.Count} mip payloads, got {mipPayloads.Length}.");

        var targetAssetPath = Path.GetFullPath(outputAssetPath);
        var targetUexpPath = Path.ChangeExtension(targetAssetPath, ".uexp");
        var targetUbulkPath = Path.ChangeExtension(targetAssetPath, ".ubulk");
        Directory.CreateDirectory(Path.GetDirectoryName(targetAssetPath)!);

        var export = (byte[])info.ExportData.Clone();
        var footer = (byte[])info.UexpFooter.Clone();
        var ubulk = (byte[])info.UbulkData.Clone();
        var payloadByMipIndex = info.Mips
            .Select((mip, payloadIndex) => new { mip.Index, Payload = mipPayloads[payloadIndex] })
            .ToDictionary(item => item.Index, item => item.Payload);

        foreach (var placement in info.MipPlacements.OrderBy(placement => placement.Offset))
        {
            if (!payloadByMipIndex.TryGetValue(placement.Index, out var payload))
                throw new InvalidOperationException($"No replacement payload was generated for mip {placement.Index}.");
            if (payload.Length != placement.ByteLength)
                throw new InvalidOperationException($"Replacement payload for mip {placement.Index} has {payload.Length} bytes, expected {placement.ByteLength}.");

            if (placement.Storage == TextureMipStorage.Ubulk)
            {
                Buffer.BlockCopy(payload, 0, ubulk, placement.Offset, payload.Length);
            }
            else if (placement.Storage == TextureMipStorage.UexpInline)
            {
                Buffer.BlockCopy(payload, 0, export, placement.Offset, payload.Length);
            }
            else
            {
                Buffer.BlockCopy(payload, 0, footer, placement.Offset, payload.Length);
            }
        }

        var uexp = new byte[export.Length + footer.Length];
        Buffer.BlockCopy(export, 0, uexp, 0, export.Length);
        Buffer.BlockCopy(footer, 0, uexp, export.Length, footer.Length);

        File.Copy(info.AssetPath, targetAssetPath, overwrite: true);
        File.WriteAllBytes(targetUexpPath, uexp);
        if (info.ExternalMipCount > 0 || File.Exists(info.UbulkPath))
            File.WriteAllBytes(targetUbulkPath, ubulk);
        else if (File.Exists(targetUbulkPath))
            File.Delete(targetUbulkPath);
    }
}
