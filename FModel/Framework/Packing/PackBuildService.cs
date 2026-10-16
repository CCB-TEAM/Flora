using System;
using System.IO;
using System.Linq;

namespace FModel.Framework.Packing;

public sealed record PackBuildResult(string PakPath, int FileCount, long PakSize);

/// <summary>Writes the files of a project into a .pak with the bundled repak writer (no UnrealPak needed).</summary>
public static class PackBuildService
{
    public static PackBuildResult Build(PackProject project, string outputPath)
    {
        if (project.Items.Count == 0)
            throw new InvalidOperationException("The project has no files.");

        var problems = project.Items.Where(o => !PackPaths.IsValid(o.PakPath)).Select(o => o.PakPath).ToList();
        if (problems.Count > 0)
            throw new InvalidOperationException($"Invalid paths inside the pak: {string.Join(", ", problems.Take(5))}");

        var version = (UAssetAPI.PakVersion) project.Settings.PakVersion;
        var mountPoint = project.Settings.MountPoint.Replace('\\', '/');
        if (!mountPoint.EndsWith('/')) mountPoint += "/";

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var temp = outputPath + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
            {
                using var builder = new UAssetAPI.PakBuilder();
                if (project.Settings.Compress)
                    builder.Compression([UAssetAPI.PakCompression.Zlib]);
                using var writer = builder.Writer(stream, version, mountPoint);
                foreach (var item in project.Items.OrderBy(o => o.PakPath, StringComparer.Ordinal))
                {
                    if (!File.Exists(item.DiskPath))
                        throw new FileNotFoundException($"The workspace copy of {item.PakPath} is missing.", item.DiskPath);
                    writer.WriteFile(item.PakPath, File.ReadAllBytes(item.DiskPath));
                }
                writer.WriteIndex();
            }

            File.Move(temp, outputPath, true);
        }
        finally
        {
            if (File.Exists(temp))
                try { File.Delete(temp); } catch { /* left behind in the output folder */ }
        }

        return new PackBuildResult(outputPath, project.Items.Count, new FileInfo(outputPath).Length);
    }
}
