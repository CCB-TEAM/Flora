using FModel.Framework.BlueprintEditing;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;
using Xunit;

namespace FModel.BlueprintGraph.Tests;

public class DataTableEditorTests
{
    private static (UAsset Asset, DataTableEditor Editor) CreateTable()
    {
        var asset = new UAsset(EngineVersion.VER_UE5_1);
        asset.ClearNameIndexList();
        asset.Imports = [];
        asset.Exports = [];

        FName Name(string text)
        {
            asset.AddNameReference(new FString(text));
            return FName.FromString(asset, text);
        }

        StructPropertyData Row(string name, int cost, float attack, string title, bool unique, params string[] tags) => new(Name(name))
        {
            StructType = Name("CardRow"),
            Value =
            [
                new IntPropertyData(Name("Cost")) { Value = cost },
                new FloatPropertyData(Name("Attack")) { Value = attack },
                new StrPropertyData(Name("Title")) { Value = FString.FromString(title) },
                new NamePropertyData(Name("Faction")) { Value = Name("Allies") },
                new BoolPropertyData(Name("Unique")) { Value = unique },
                new ArrayPropertyData(Name("Tags")) { ArrayType = Name("NameProperty"), Value = tags.Select(t => (PropertyData) new NamePropertyData(Name("Tags")) { Value = Name(t) }).ToArray() },
                new StructPropertyData(Name("Stats")) { StructType = Name("CardStats"), Value = [new IntPropertyData(Name("Armor")) { Value = 2 }] },
            ],
        };

        var export = new DataTableExport(new UDataTable([Row("Rifleman", 3, 1.5f, "Rifleman", false, "Infantry", "Basic"), Row("Sniper", 5, 4f, "Sniper", true, "Infantry")]), asset, []);
        export.ObjectName = Name("DT_Cards");
        export.Data = [];
        asset.Exports.Add(export);
        return (asset, new DataTableEditor(asset));
    }

    private static DataTableOperation Op(string op, string row, string? values = null, string? copyFrom = null, string? newName = null) =>
        new() { Op = op, Row = row, Values = values == null ? null : JObject.Parse(values), CopyFrom = copyFrom, NewName = newName };

    [Fact]
    public void Rows_AreReadAsPlainJson()
    {
        var (_, editor) = CreateTable();
        var row = editor.ReadRow(editor.Find("Rifleman")!);
        Assert.Equal(3, row["Cost"]!.Value<int>());
        Assert.Equal(1.5, row["Attack"]!.Value<double>());
        Assert.Equal("Rifleman", row["Title"]!.Value<string>());
        Assert.Equal("Allies", row["Faction"]!.Value<string>());
        Assert.False(row["Unique"]!.Value<bool>());
        Assert.Equal(new[] { "Infantry", "Basic" }, row["Tags"]!.Select(o => o.Value<string>()));
        Assert.Equal(2, row["Stats"]!["Armor"]!.Value<int>());
        Assert.Equal(["Cost:int", "Attack:float", "Title:str", "Faction:name", "Unique:bool", "Tags:array<name>", "Stats:struct:CardStats"],
            editor.Columns().Select(o => $"{o.Name}:{o.Type}"));
    }

    [Fact]
    public void Set_ChangesCellsAndTheDiffNamesThemExactly()
    {
        var (_, editor) = CreateTable();
        Assert.False(editor.IsModified);
        var errors = editor.Apply([Op("set", "Rifleman", """{"Cost": 4, "Stats": {"Armor": 3}, "Tags": ["Infantry", "Basic", "Veteran"]}""")]);
        Assert.Empty(errors);

        var change = Assert.Single(editor.Diff());
        Assert.Equal(("Rifleman", "changed"), (change.Row, change.Kind));
        // an array that changed length is one cell (before and after as arrays)
        Assert.Equal(["Cost", "Stats.Armor", "Tags"], change.Cells.Select(o => o.Path).Order(StringComparer.Ordinal));
        Assert.Equal("[\"Infantry\",\"Basic\",\"Veteran\"]", change.Cells.First(o => o.Path == "Tags").After);
        Assert.Equal(("3", "4"), (change.Cells.First(o => o.Path == "Cost").Before, change.Cells.First(o => o.Path == "Cost").After));
        Assert.Equal(3, editor.ReadRow(editor.Find("Rifleman")!)["Tags"]!.Count());
    }

    [Fact]
    public void Edits_AreAllOrNothing()
    {
        var (_, editor) = CreateTable();
        var errors = editor.Apply([
            Op("set", "Rifleman", """{"Cost": 9}"""),
            Op("add", "Medic", """{"Cost": 2}""", copyFrom: "Rifleman"),
            Op("set", "Sniper", """{"Nope": 1}"""),
        ]);
        var error = Assert.Single(errors);
        Assert.Contains("operation 3", error);
        Assert.Contains("no column 'Nope'", error);
        Assert.Contains("the columns are Cost, Attack", error);
        // the first two operations were undone
        Assert.False(editor.IsModified);
        Assert.Equal(3, editor.ReadRow(editor.Find("Rifleman")!)["Cost"]!.Value<int>());
        Assert.Null(editor.Find("Medic"));
        Assert.Equal(2, editor.Rows.Count);
    }

    [Fact]
    public void Add_CopiesTheTemplateWithoutSharingItsCells()
    {
        var (_, editor) = CreateTable();
        Assert.Empty(editor.Apply([Op("add", "Medic", """{"Cost": 1, "Title": "Medic", "Tags": ["Support"]}""", copyFrom: "Rifleman")]));
        var medic = editor.ReadRow(editor.Find("Medic")!);
        Assert.Equal(1, medic["Cost"]!.Value<int>());
        Assert.Equal(1.5, medic["Attack"]!.Value<double>()); // not given: from the template
        Assert.Equal(["Support"], medic["Tags"]!.Select(o => o.Value<string>()));
        // the template is untouched
        var rifleman = editor.ReadRow(editor.Find("Rifleman")!);
        Assert.Equal(3, rifleman["Cost"]!.Value<int>());
        Assert.Equal(2, rifleman["Tags"]!.Count());

        var change = editor.Diff().Single();
        Assert.Equal(("Medic", "added"), (change.Row, change.Kind));
    }

    [Fact]
    public void Add_RefusesDuplicateNamesIgnoringCase()
    {
        var (_, editor) = CreateTable();
        Assert.Contains("already exists", Assert.Single(editor.Apply([Op("add", "Rifleman")])));
        // upsert of an existing row is a set
        Assert.Empty(editor.Apply([Op("upsert", "rifleman", """{"Cost": 7}""")]));
        Assert.Equal(7, editor.ReadRow(editor.Find("Rifleman")!)["Cost"]!.Value<int>());
        Assert.Contains("empty", Assert.Single(editor.Apply([Op("add", " ")])));
    }

    [Fact]
    public void DeleteRenameAndRevert()
    {
        var (_, editor) = CreateTable();
        Assert.Empty(editor.Apply([Op("rename", "Sniper", newName: "Marksman"), Op("delete", "Rifleman")]));
        Assert.Equal(["Marksman"], editor.Rows.Select(o => o.Name.ToString()));
        // a rename is a removed and an added row
        Assert.Equal(["Rifleman:removed", "Sniper:removed", "Marksman:added"], editor.Diff().Select(o => $"{o.Row}:{o.Kind}").OrderBy(o => o.EndsWith("added")).ThenBy(o => o, StringComparer.Ordinal));

        editor.Revert("Rifleman");
        editor.Revert("Marksman"); // an added row (as far as the diff knows) goes away again
        editor.Revert("Sniper"); // and the row it was renamed from comes back
        Assert.Equal(["Rifleman", "Sniper"], editor.Rows.Select(o => o.Name.ToString()).Order(StringComparer.Ordinal));
        Assert.Empty(editor.Apply([Op("rename", "Rifleman", newName: "Sniper")]).Where(o => !o.Contains("exists already")));
        Assert.Contains("exists already", Assert.Single(editor.Apply([Op("rename", "Rifleman", newName: "Sniper")])));

        editor.Apply([Op("set", "Rifleman", """{"Cost": 99}""")]);
        Assert.True(editor.IsModified);
        editor.Revert(null);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void WrongTypesSayWhereAndWhat()
    {
        var (_, editor) = CreateTable();
        var error = Assert.Single(editor.Apply([Op("set", "Rifleman", """{"Cost": "many"}""")]));
        Assert.Contains("Cost", error);
        Assert.Contains("expected a whole number for a int column", error);

        Assert.Contains("does not fit", Assert.Single(editor.Apply([Op("set", "Rifleman", """{"Stats": {"Armor": 99999999999}}""")])));
        Assert.Contains("no such column", Assert.Single(editor.Apply([Op("set", "Rifleman", """{"Stats": {"Shield": 1}}""")])));
        Assert.Contains("expected an array", Assert.Single(editor.Apply([Op("set", "Rifleman", """{"Tags": "Infantry"}""")])));
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void Validate_FindsDuplicatesAndInconsistentRows()
    {
        var (_, editor) = CreateTable();
        Assert.Empty(editor.Validate());

        // two rows that differ only in case, and a row with another column set
        var clone = (StructPropertyData) editor.Rows[0].Clone();
        clone.Name = FName.FromString(editor.Asset, "RIFLEMAN");
        editor.Rows.Add(clone);
        var odd = (StructPropertyData) editor.Rows[1].Clone();
        odd.Name = FName.FromString(editor.Asset, "Odd");
        odd.Value.RemoveAt(0);
        editor.Rows.Add(odd);

        var problems = editor.Validate();
        Assert.Contains(problems, o => o.Contains("duplicate row name") && o.Contains("not case sensitive"));
        Assert.Contains(problems, o => o.Contains("'Odd'") && o.Contains("missing Cost:int"));
    }

    [Fact]
    public void ExportAndImport_RoundTripThroughJsonAndCsv()
    {
        var (_, editor) = CreateTable();
        var json = editor.ExportJson();
        Assert.Equal(["Rifleman", "Sniper"], ((JObject) json["rows"]!).Properties().Select(o => o.Name));
        Assert.Equal("int", json["columns"]!["Cost"]!.Value<string>());

        var (_, other) = CreateTable();
        other.Apply([Op("set", "Sniper", """{"Cost": 1}""")]);
        Assert.Empty(other.Apply(other.ParseImport(json.ToString(), csv: false)));
        Assert.Equal(5, other.ReadRow(other.Find("Sniper")!)["Cost"]!.Value<int>());

        var csv = editor.ExportCsv();
        Assert.StartsWith("Name,Cost,Attack,Title,Faction,Unique,Tags,Stats", csv);
        Assert.Contains("Rifleman,3,1.5,Rifleman,Allies,False,", csv);

        var (_, fromCsv) = CreateTable();
        fromCsv.Apply([Op("set", "Rifleman", """{"Cost": 1, "Title": "x"}"""), Op("set", "Sniper", """{"Unique": false}""")]);
        Assert.Empty(fromCsv.Apply(fromCsv.ParseImport(csv, csv: true)));
        Assert.False(fromCsv.Diff().Any(o => o.Kind == "changed" && o.Cells.Any()));

        // a new row from a csv line
        var added = fromCsv.ParseImport("Name,Cost,Title\nMedic,2,\"Medic, field\"\n", csv: true);
        Assert.Empty(fromCsv.Apply(added));
        Assert.Equal("Medic, field", fromCsv.ReadRow(fromCsv.Find("Medic")!)["Title"]!.Value<string>());
        Assert.Contains("not a column", Assert.Throws<FormatException>(() => fromCsv.ParseImport("Name,Nope\nA,1", csv: true)).Message);
    }

    [Fact]
    public void ObjectCellsUseSymbolicReferences()
    {
        var (asset, _) = CreateTable();
        var package = asset.AddImport(new Import("/Script/CoreUObject", "Package", FPackageIndex.FromRawIndex(0), "/Game/UI/T_Card", false, asset));
        var texture = asset.AddImport(new Import("/Script/CoreUObject", "Texture2D", package, "T_Card", false, asset));
        var row = ((DataTableExport) asset.Exports[0]).Table.Data[0];
        row.Value.Add(new ObjectPropertyData(FName.FromString(asset, "Image")) { Value = texture });
        var editor = new DataTableEditor(asset);

        Assert.Equal("import:/Game/UI/T_Card.T_Card", editor.ReadRow(editor.Find("Rifleman")!)["Image"]!.Value<string>());
        Assert.Contains("T_Card", editor.Columns().Single(o => o.Name == "Image").Type == "object" ? "T_Card" : "");
    }
}
