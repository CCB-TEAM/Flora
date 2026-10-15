using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes.PSK;

namespace CUE4Parse_Conversion.Meshes.Text;

public static class TextMeshExporter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static byte[] SaveObj(string name, CStaticMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
        => SaveObj(name, lod, lod.Verts ?? [], materialExports, options);

    public static byte[] SaveObj(string name, CSkelMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
        => SaveObj(name, lod, lod.Verts ?? [], materialExports, options);

    public static byte[] SaveFbx(string name, CStaticMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
        => SaveFbx(name, lod, lod.Verts ?? [], materialExports, options);

    public static byte[] SaveFbx(string name, CSkelMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
        => SaveFbx(name, lod, lod.Verts ?? [], materialExports, options);

    private static byte[] SaveObj(string name, CBaseMeshLod lod, IReadOnlyList<CMeshVertex> verts, List<MaterialExporter2>? materialExports, ExporterOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Exported by CUE4Parse");
        sb.Append("o ").AppendLine(SanitizeObjName(name));

        foreach (var vertex in verts)
        {
            var position = ToExportPosition(vertex.Position);
            sb.Append("v ")
                .Append(Float(position.X)).Append(' ')
                .Append(Float(position.Y)).Append(' ')
                .Append(Float(position.Z)).AppendLine();
        }

        foreach (var vertex in verts)
        {
            sb.Append("vt ")
                .Append(Float(vertex.UV.U)).Append(' ')
                .Append(Float(vertex.UV.V)).AppendLine();
        }

        foreach (var vertex in verts)
        {
            var normal = ToExportNormal((FVector) vertex.Normal);
            sb.Append("vn ")
                .Append(Float(normal.X)).Append(' ')
                .Append(Float(normal.Y)).Append(' ')
                .Append(Float(normal.Z)).AppendLine();
        }

        var indices = lod.Indices?.Value ?? [];
        foreach (var (_, section, materialName) in EnumerateSections(lod, materialExports, options))
        {
            sb.Append("usemtl ").AppendLine(SanitizeObjName(materialName));
            sb.AppendLine("s 1");

            for (var faceIndex = 0; faceIndex < section.NumFaces; faceIndex++)
            {
                sb.Append('f');
                for (var corner = 0; corner < 3; corner++)
                {
                    var index = indices[section.FirstIndex + faceIndex * 3 + corner] + 1;
                    sb.Append(' ').Append(index).Append('/').Append(index).Append('/').Append(index);
                }
                sb.AppendLine();
            }
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] SaveFbx(string name, CBaseMeshLod lod, IReadOnlyList<CMeshVertex> verts, List<MaterialExporter2>? materialExports, ExporterOptions options)
    {
        var indices = lod.Indices?.Value ?? [];
        var positions = verts.Select(vertex => ToExportPosition(vertex.Position)).ToArray();
        var polygonVertexIndices = new List<int>();
        var normals = new List<FVector>();
        var uvs = new List<(float U, float V)>();
        var faceMaterials = new List<int>();
        var materialNames = new List<string>();

        foreach (var (_, section, materialName) in EnumerateSections(lod, materialExports, options))
        {
            var materialIndex = materialNames.Count;
            materialNames.Add(materialName);

            for (var faceIndex = 0; faceIndex < section.NumFaces; faceIndex++)
            {
                for (var corner = 0; corner < 3; corner++)
                {
                    var vertexIndex = (int) indices[section.FirstIndex + faceIndex * 3 + corner];
                    polygonVertexIndices.Add(corner == 2 ? ~vertexIndex : vertexIndex);

                    var vertex = verts[vertexIndex];
                    normals.Add(ToExportNormal((FVector) vertex.Normal));
                    uvs.Add((vertex.UV.U, vertex.UV.V));
                }

                faceMaterials.Add(materialIndex);
            }
        }

        var sb = new StringBuilder();
        var modelName = EscapeFbx(name);
        const long geometryId = 100000;
        const long modelId = 100001;
        const long firstMaterialId = 200000;

        sb.AppendLine("; FBX 7.4.0 project file");
        sb.AppendLine("; Exported by CUE4Parse");
        sb.AppendLine("FBXHeaderExtension:  {");
        sb.AppendLine("\tFBXHeaderVersion: 1003");
        sb.AppendLine("\tFBXVersion: 7400");
        sb.AppendLine("\tCreator: \"CUE4Parse\"");
        sb.AppendLine("}");
        sb.AppendLine("GlobalSettings:  {");
        sb.AppendLine("\tVersion: 1000");
        sb.AppendLine("\tProperties70:  {");
        sb.AppendLine("\t\tP: \"UpAxis\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UpAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"FrontAxis\", \"int\", \"Integer\", \"\",2");
        sb.AppendLine("\t\tP: \"FrontAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"CoordAxis\", \"int\", \"Integer\", \"\",0");
        sb.AppendLine("\t\tP: \"CoordAxisSign\", \"int\", \"Integer\", \"\",1");
        sb.AppendLine("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",1");
        sb.AppendLine("\t}");
        sb.AppendLine("}");
        sb.AppendLine("Definitions:  {");
        sb.Append("\tCount: ").Append(2 + materialNames.Count).AppendLine();
        sb.AppendLine("\tObjectType: \"Geometry\" { Count: 1 }");
        sb.AppendLine("\tObjectType: \"Model\" { Count: 1 }");
        sb.Append("\tObjectType: \"Material\" { Count: ").Append(materialNames.Count).AppendLine(" }");
        sb.AppendLine("}");
        sb.AppendLine("Objects:  {");
        sb.Append("\tGeometry: ").Append(geometryId).Append(", \"Geometry::").Append(modelName).Append("\", \"Mesh\" {").AppendLine();
        WriteFbxVectorArray(sb, "Vertices", positions.SelectMany(v => new[] { v.X, v.Y, v.Z }));
        WriteFbxIntArray(sb, "PolygonVertexIndex", polygonVertexIndices);
        sb.AppendLine("\t\tGeometryVersion: 124");
        sb.AppendLine("\t\tLayerElementNormal: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByPolygonVertex\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        WriteFbxVectorArray(sb, "Normals", normals.SelectMany(v => new[] { v.X, v.Y, v.Z }), 3);
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tLayerElementUV: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"UVChannel_0\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByPolygonVertex\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"Direct\"");
        WriteFbxVectorArray(sb, "UV", uvs.SelectMany(uv => new[] { uv.U, uv.V }), 3);
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tLayerElementMaterial: 0 {");
        sb.AppendLine("\t\t\tVersion: 101");
        sb.AppendLine("\t\t\tName: \"\"");
        sb.AppendLine("\t\t\tMappingInformationType: \"ByPolygon\"");
        sb.AppendLine("\t\t\tReferenceInformationType: \"IndexToDirect\"");
        WriteFbxIntArray(sb, "Materials", faceMaterials, 3);
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tLayer: 0 {");
        sb.AppendLine("\t\t\tVersion: 100");
        WriteFbxLayerElement(sb, "LayerElementNormal");
        WriteFbxLayerElement(sb, "LayerElementUV");
        WriteFbxLayerElement(sb, "LayerElementMaterial");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t}");
        sb.Append("\tModel: ").Append(modelId).Append(", \"Model::").Append(modelName).Append("\", \"Mesh\" {").AppendLine();
        sb.AppendLine("\t\tVersion: 232");
        sb.AppendLine("\t\tProperties70:  {");
        sb.AppendLine("\t\t\tP: \"InheritType\", \"enum\", \"\", \"\",1");
        sb.AppendLine("\t\t}");
        sb.AppendLine("\t\tShading: T");
        sb.AppendLine("\t\tCulling: \"CullingOff\"");
        sb.AppendLine("\t}");

        for (var i = 0; i < materialNames.Count; i++)
        {
            sb.Append("\tMaterial: ").Append(firstMaterialId + i).Append(", \"Material::").Append(EscapeFbx(materialNames[i])).Append("\", \"\" {").AppendLine();
            sb.AppendLine("\t\tVersion: 102");
            sb.AppendLine("\t\tShadingModel: \"lambert\"");
            sb.AppendLine("\t\tProperties70:  {");
            sb.AppendLine("\t\t\tP: \"DiffuseColor\", \"Color\", \"\", \"A\",1,1,1");
            sb.AppendLine("\t\t}");
            sb.AppendLine("\t}");
        }

        sb.AppendLine("}");
        sb.AppendLine("Connections:  {");
        sb.Append("\tC: \"OO\",").Append(geometryId).Append(',').Append(modelId).AppendLine();
        sb.Append("\tC: \"OO\",").Append(modelId).Append(",0").AppendLine();
        for (var i = 0; i < materialNames.Count; i++)
        {
            sb.Append("\tC: \"OO\",").Append(firstMaterialId + i).Append(',').Append(modelId).AppendLine();
        }
        sb.AppendLine("}");

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static IEnumerable<(int Index, CMeshSection Section, string MaterialName)> EnumerateSections(CBaseMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
    {
        var sections = lod.Sections?.Value ?? [];
        for (var i = 0; i < sections.Length; i++)
        {
            yield return (i, sections[i], GetMaterialName(i, sections[i], materialExports, options));
        }
    }

    private static string GetMaterialName(int index, CMeshSection section, List<MaterialExporter2>? materialExports, ExporterOptions options)
    {
        if (section.Material?.Load<UMaterialInterface>() is { } material)
        {
            materialExports?.Add(new MaterialExporter2(material, options));
            return material.Name;
        }

        return section.MaterialName ?? $"material_{index}";
    }

    private static FVector ToExportPosition(FVector position) => new(position.X * 0.01f, position.Z * 0.01f, position.Y * 0.01f);

    private static FVector ToExportNormal(FVector normal)
    {
        var result = new FVector(normal.X, normal.Z, normal.Y);
        var sizeSquared = result | result;
        if (sizeSquared > 0)
        {
            result /= MathF.Sqrt(sizeSquared);
        }

        return result;
    }

    private static void WriteFbxVectorArray(StringBuilder sb, string name, IEnumerable<float> values, int indent = 2)
    {
        var array = values.ToArray();
        WriteIndent(sb, indent).Append(name).Append(": *").Append(array.Length).AppendLine(" {");
        WriteIndent(sb, indent + 1).Append("a: ");
        for (var i = 0; i < array.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Float(array[i]));
        }
        sb.AppendLine();
        WriteIndent(sb, indent).AppendLine("}");
    }

    private static void WriteFbxIntArray(StringBuilder sb, string name, IEnumerable<int> values, int indent = 2)
    {
        var array = values.ToArray();
        WriteIndent(sb, indent).Append(name).Append(": *").Append(array.Length).AppendLine(" {");
        WriteIndent(sb, indent + 1).Append("a: ");
        for (var i = 0; i < array.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(array[i]);
        }
        sb.AppendLine();
        WriteIndent(sb, indent).AppendLine("}");
    }

    private static StringBuilder WriteIndent(StringBuilder sb, int indent)
    {
        for (var i = 0; i < indent; i++) sb.Append('\t');
        return sb;
    }

    private static void WriteFbxLayerElement(StringBuilder sb, string type)
    {
        sb.AppendLine("\t\t\tLayerElement:  {");
        sb.Append("\t\t\t\tType: \"").Append(type).AppendLine("\"");
        sb.AppendLine("\t\t\t\tTypedIndex: 0");
        sb.AppendLine("\t\t\t}");
    }

    private static string Float(float value) => value.ToString("G9", Invariant);

    private static string EscapeFbx(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string SanitizeObjName(string value)
        => string.IsNullOrWhiteSpace(value) ? "mesh" : value.Replace(' ', '_').Replace('\t', '_');
}
