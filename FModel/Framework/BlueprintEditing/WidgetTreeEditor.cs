using System;
using System.Collections.Generic;
using System.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.CustomVersions;
using UAssetAPI.UnrealTypes;

namespace FModel.Framework.BlueprintEditing;

/// <summary>Conservative structural edits to a cooked WidgetTree.</summary>
public static class WidgetTreeEditor
{
    private static NormalExport Normal(UAsset asset, int index) =>
        index > 0 && index <= asset.Exports.Count && asset.Exports[index - 1] is NormalExport e
            ? e : throw new InvalidOperationException($"Export #{index} is missing or not parsed.");

    private static T Property<T>(NormalExport e, string name) where T : PropertyData =>
        e.Data?.OfType<T>().SingleOrDefault(x => x.Name.ToString() == name)
        ?? throw new InvalidOperationException($"{e.ObjectName}.{name} is missing or has an unexpected type.");

    private static NormalExport Copy(NormalExport source)
    {
        var copy = (NormalExport)source.Clone();
        copy.Data = source.Data.Select(p => (PropertyData)p.Clone()).ToList();
        return copy;
    }

    /// <summary>
    /// Duplicates a native Image or TextBlock and its slot immediately after the original in the same panel.
    /// Restricting this operation to leaf widgets avoids silently copying event handlers, nested trees, or
    /// generated-class variables. Indices are Unreal's 1-based export indices.
    /// </summary>
    public static (int Widget, int Slot) DuplicateLeaf(UAsset asset, int index, string name) => Duplicate(asset, index, name, false);

    private static readonly HashSet<string> NativeWidgetClasses = new(StringComparer.Ordinal)
    {
        "CanvasPanel", "VerticalBox", "HorizontalBox", "Overlay", "SizeBox", "Border", "Button", "Image", "TextBlock", "Spacer"
    };

    /// <summary>Moves an existing widget to a compatible panel without changing its widget variable or bindings.</summary>
    public static void MoveWidget(UAsset asset, int widgetIndex, int targetIndex, int position)
    {
        var widget = Normal(asset, widgetIndex);
        var target = Normal(asset, targetIndex);
        if (widget.OuterIndex?.Index != target.OuterIndex?.Index || widgetIndex == targetIndex)
            throw new InvalidOperationException("The widget and destination must be distinct widgets in the same tree.");
        var slotIndex = Property<ObjectPropertyData>(widget, "Slot").Value.Index;
        var slot = Normal(asset, slotIndex);
        var oldParentIndex = Property<ObjectPropertyData>(slot, "Parent").Value.Index;
        var oldParent = Normal(asset, oldParentIndex);
        if (slot.OuterIndex?.Index != oldParentIndex || Property<ObjectPropertyData>(slot, "Content").Value.Index != widgetIndex)
            throw new InvalidOperationException("The widget's slot and old parent references disagree.");
        var targetClass = target.GetClassTypeForAncestry(asset, out _)?.ToString();
        var requiredSlot = targetClass switch
        {
            "VerticalBox" => "VerticalBoxSlot", "HorizontalBox" => "HorizontalBoxSlot",
            "CanvasPanel" => "CanvasPanelSlot", "Overlay" => "OverlaySlot",
            _ => throw new InvalidOperationException("The destination must be a supported multi-child panel.")
        };
        if (slot.GetClassTypeForAncestry(asset, out _)?.ToString() != requiredSlot)
            throw new InvalidOperationException($"The existing slot is incompatible with {targetClass}; expected {requiredSlot}.");
        // Walk upwards from the destination to prevent parenting a widget to its own descendant.
        var visited = new HashSet<int>();
        var current = targetIndex;
        while (current > 0 && visited.Add(current))
        {
            if (current == widgetIndex) throw new InvalidOperationException("The destination is inside the widget being moved.");
            var ancestor = Normal(asset, current);
            var parentSlot = ancestor.Data.OfType<ObjectPropertyData>().SingleOrDefault(p => p.Name.ToString() == "Slot")?.Value?.Index ?? 0;
            if (parentSlot <= 0) { current = 0; break; }
            current = Property<ObjectPropertyData>(Normal(asset, parentSlot), "Parent").Value.Index;
        }
        if (current > 0) throw new InvalidOperationException("The destination hierarchy contains a cycle.");
        var from = Property<ArrayPropertyData>(oldParent, "Slots");
        var to = Property<ArrayPropertyData>(target, "Slots");
        var matches = from.Value.OfType<ObjectPropertyData>().Where(p => p.Value?.Index == slotIndex).ToList();
        if (matches.Count != 1) throw new InvalidOperationException("The source parent must reference the slot exactly once.");
        var oldValues = from.Value.ToList();
        oldValues.Remove(matches[0]);
        var newValues = ReferenceEquals(from, to) ? oldValues : to.Value.ToList();
        if (!ReferenceEquals(from, to) && newValues.OfType<ObjectPropertyData>().Any(p => p.Value?.Index == slotIndex))
            throw new InvalidOperationException("The destination already references this slot.");
        if (position < 0 || position > newValues.Count)
            throw new ArgumentOutOfRangeException(nameof(position), $"Position must be between 0 and {newValues.Count}.");
        if (oldParentIndex != targetIndex && asset.Exports.Any(e => e != slot && e.OuterIndex?.Index == targetIndex &&
                e.ObjectName.ToString().Equals(slot.ObjectName.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The destination already has an object with the slot's name.");
        newValues.Insert(position, matches[0]);
        if (!ReferenceEquals(from, to)) from.Value = oldValues.ToArray();
        to.Value = newValues.ToArray();
        from.IsZero = false;
        to.IsZero = false;
        slot.OuterIndex = FPackageIndex.FromRawIndex(targetIndex);
        Property<ObjectPropertyData>(slot, "Parent").Value = FPackageIndex.FromRawIndex(targetIndex);
        foreach (var list in new[] { slot.SerializationBeforeSerializationDependencies, slot.CreateBeforeSerializationDependencies,
                     slot.SerializationBeforeCreateDependencies, slot.CreateBeforeCreateDependencies })
            for (var i = 0; i < list.Count; i++)
                if (list[i].Index == oldParentIndex) list[i] = FPackageIndex.FromRawIndex(targetIndex);
        if (asset.DependsMap != null && slotIndex <= asset.DependsMap.Count && asset.DependsMap[slotIndex - 1] != null)
            asset.DependsMap[slotIndex - 1] = asset.DependsMap[slotIndex - 1].Select(i => i == oldParentIndex ? targetIndex : i).ToArray();
    }

    /// <summary>Duplicates a parsed widget subtree and its slots, without copying event bindings or external widget blueprints.</summary>
    public static (int Widget, int Slot, int Count) DuplicateWidgetSubtree(UAsset asset, int index, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new InvalidOperationException("The prefix must contain only letters, digits and underscores.");
        var root = Normal(asset, index);
        var treeIndex = root.OuterIndex?.Index ?? 0;
        var tree = Normal(asset, treeIndex);
        if (tree.GetClassTypeForAncestry(asset, out _)?.ToString() != "WidgetTree")
            throw new InvalidOperationException("The selected export is not owned directly by a WidgetTree.");
        var rootSlotIndex = Property<ObjectPropertyData>(root, "Slot").Value.Index;
        var rootSlot = Normal(asset, rootSlotIndex);
        var parentIndex = Property<ObjectPropertyData>(rootSlot, "Parent").Value.Index;
        var parent = Normal(asset, parentIndex);
        var parentType = parent.GetClassTypeForAncestry(asset, out _)?.ToString();
        if (parentType is not ("VerticalBox" or "HorizontalBox" or "CanvasPanel" or "Overlay"))
            throw new InvalidOperationException("The root parent must be a supported multi-child panel.");
        var parentSlots = Property<ArrayPropertyData>(parent, "Slots");
        var position = Array.FindIndex(parentSlots.Value, p => p is ObjectPropertyData o && o.Value?.Index == rootSlotIndex);
        if (position < 0 || parentSlots.Value.Count(p => p is ObjectPropertyData o && o.Value?.Index == rootSlotIndex) != 1)
            throw new InvalidOperationException("The root slot is not uniquely present in its parent.");
        if (asset.DependsMap != null && asset.DependsMap.Count != asset.Exports.Count)
            throw new InvalidOperationException("The dependency map does not match the export table.");

        var objects = new List<int>();
        var visited = new HashSet<int>();
        void Walk(int widgetIndex, int slotIndex, int expectedParent)
        {
            if (!visited.Add(widgetIndex) || !visited.Add(slotIndex))
                throw new InvalidOperationException("The widget hierarchy contains a cycle or shared object.");
            var widget = Normal(asset, widgetIndex);
            var slot = Normal(asset, slotIndex);
            if (widget.OuterIndex?.Index != treeIndex || slot.OuterIndex?.Index != expectedParent ||
                Property<ObjectPropertyData>(widget, "Slot").Value.Index != slotIndex ||
                Property<ObjectPropertyData>(slot, "Parent").Value.Index != expectedParent ||
                Property<ObjectPropertyData>(slot, "Content").Value.Index != widgetIndex)
                throw new InvalidOperationException("Widget, slot, tree and parent references disagree.");
            var type = widget.GetClassTypeForAncestry(asset, out _)?.ToString();
            // A cooked UserWidget instance references a separate generated class. Its internal tree belongs
            // to that class and is not part of this package's subtree; copy only the parsed instance.
            var isUserWidget = widget.ClassIndex?.IsImport() == true && type?.EndsWith("_C", StringComparison.Ordinal) == true;
            if (!NativeWidgetClasses.Contains(type ?? "") && !isUserWidget)
                throw new InvalidOperationException($"Widget #{widgetIndex} ({type}) is neither a supported native widget nor an imported user-widget instance.");
            if (widget.Data.Any(p => p is DelegatePropertyData or MulticastDelegatePropertyData))
                throw new InvalidOperationException("Serialized event delegates are not copied.");
            objects.Add(widgetIndex); objects.Add(slotIndex);
            var slots = widget.Data.OfType<ArrayPropertyData>().SingleOrDefault(p => p.Name.ToString() == "Slots");
            if (slots == null) return;
            foreach (var child in slots.Value)
            {
                if (child is not ObjectPropertyData reference || reference.Value?.Index <= 0)
                    throw new InvalidOperationException("A child slot is not a valid local export reference.");
                var childSlot = Normal(asset, reference.Value.Index);
                Walk(Property<ObjectPropertyData>(childSlot, "Content").Value.Index, reference.Value.Index, widgetIndex);
            }
        }
        Walk(index, rootSlotIndex, parentIndex);
        var map = objects.Select((old, n) => (old, next: asset.Exports.Count + n + 1)).ToDictionary(x => x.old, x => x.next);
        var generated = asset.Exports.OfType<ClassExport>().SingleOrDefault(c =>
            c.Data?.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "WidgetTree" && p.Value?.Index == treeIndex) == true)
            ?? throw new InvalidOperationException("No unique generated class owns this WidgetTree.");
        var fields = new List<UAssetAPI.FieldTypes.FProperty>();
        var names = new Dictionary<int, string>();
        foreach (var old in objects)
        {
            var source = Normal(asset, old);
            var name = prefix + "_" + source.ObjectName + (source.OuterIndex?.Index == treeIndex ? "" : "_" + old);
            var newOuter = map.GetValueOrDefault(source.OuterIndex?.Index ?? 0, source.OuterIndex?.Index ?? 0);
            if (asset.Exports.Any(e => e.OuterIndex?.Index == newOuter && e.ObjectName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"An object named '{name}' already exists in the target outer.");
            names.Add(old, name);
            if (source.OuterIndex?.Index != treeIndex) continue;
            var field = generated.LoadedProperties.SingleOrDefault(p => p.Name.ToString() == source.ObjectName.ToString());
            if (field == null) continue;
            if (field is not UAssetAPI.FieldTypes.FObjectProperty || generated.LoadedProperties.Any(p => p.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Widget variable '{name}' cannot be duplicated safely.");
            var copy = KismetLocals.Clone(field);
            fields.Add(copy);
        }
        if (names.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidOperationException("The generated names would collide.");
        var fieldIndex = 0;
        var copies = new List<NormalExport>();
        int Remap(int value) => map.GetValueOrDefault(value, value);
        PropertyData CloneProperty(PropertyData property)
        {
            var copy = (PropertyData)property.Clone();
            if (copy is ObjectPropertyData o && o.Value != null) o.Value = FPackageIndex.FromRawIndex(Remap(o.Value.Index));
            else if (copy is ArrayPropertyData a && a.Value != null) a.Value = a.Value.Select(CloneProperty).ToArray();
            else if (copy is StructPropertyData s && s.Value != null) s.Value = s.Value.Select(CloneProperty).ToList();
            else if (copy is MapPropertyData or DelegatePropertyData or MulticastDelegatePropertyData)
                throw new InvalidOperationException("Maps and serialized delegates inside a subtree are not supported.");
            return copy;
        }
        // Clone all properties before adding names or changing the asset. Unsupported nested properties
        // therefore fail before any package state is touched.
        var clonedData = objects.ToDictionary(i => i, i => Normal(asset, i).Data.Select(CloneProperty).ToList());
        foreach (var old in objects)
        {
            var source = Normal(asset, old);
            var copy = Copy(source);
            copy.Data = clonedData[old];
            asset.AddNameReference(new FString(names[old]));
            copy.ObjectName = FName.FromString(asset, names[old]);
            copy.OuterIndex = FPackageIndex.FromRawIndex(Remap(source.OuterIndex?.Index ?? 0));
            foreach (var list in new[] { copy.SerializationBeforeSerializationDependencies, copy.CreateBeforeSerializationDependencies,
                         copy.SerializationBeforeCreateDependencies, copy.CreateBeforeCreateDependencies })
                for (var i = 0; i < list.Count; i++) list[i] = FPackageIndex.FromRawIndex(Remap(list[i].Index));
            if (source.OuterIndex?.Index == treeIndex && generated.LoadedProperties.Any(p => p.Name.ToString() == source.ObjectName.ToString()))
                KismetLocals.Rename(asset, fields[fieldIndex++], names[old]);
            copies.Add(copy);
        }
        var referenceCopy = (ObjectPropertyData)parentSlots.Value[position].Clone();
        referenceCopy.Value = FPackageIndex.FromRawIndex(map[rootSlotIndex]);
        var newSlots = parentSlots.Value.ToList(); newSlots.Insert(position + 1, referenceCopy);
        var dependencies = asset.DependsMap == null ? null : objects.Select(i => (asset.DependsMap[i - 1] ?? Array.Empty<int>()).Select(Remap).ToArray()).ToList();
        asset.Exports.AddRange(copies);
        if (dependencies != null) asset.DependsMap.AddRange(dependencies);
        generated.LoadedProperties = [.. generated.LoadedProperties, .. fields];
        parentSlots.Value = newSlots.ToArray();
        return (map[index], map[rootSlotIndex], objects.Count);
    }

    /// <summary>Duplicates a variable-backed widget, including a matching generated-class property, but no event binding.</summary>
    public static (int Widget, int Slot) DuplicateBoundWidget(UAsset asset, int index, string name)
    {
        var widget = Normal(asset, index);
        var owner = asset.Exports.OfType<ClassExport>().SingleOrDefault(c =>
            c.LoadedProperties?.Any(p => p.Name.ToString() == widget.ObjectName.ToString()) == true)
            ?? throw new InvalidOperationException("No unique generated class declares the template widget variable.");
        var original = owner.LoadedProperties.Single(p => p.Name.ToString() == widget.ObjectName.ToString());
        if (original is not UAssetAPI.FieldTypes.FObjectProperty)
            throw new InvalidOperationException("The template widget variable is not an object property.");
        if (owner.LoadedProperties.Any(p => p.Name.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The generated class already has a variable with that name.");
        var field = KismetLocals.Clone(original);
        var result = Duplicate(asset, index, name, true);
        KismetLocals.Rename(asset, field, name);
        owner.LoadedProperties = [.. owner.LoadedProperties, field];
        return result;
    }

    /// <summary>Bind a widget variable to an existing handler proven compatible by an existing binding.</summary>
    public static void BindExistingHandler(UAsset asset, string widgetName, string templateWidget, string delegateName, string functionName) =>
        AddBinding(asset, widgetName, templateWidget, delegateName, functionName, functionName);

    private static void AddBinding(UAsset asset, string widgetName, string templateWidget, string delegateName, string functionName, string targetFunction)
    {
        var generated = asset.Exports.OfType<ClassExport>().SingleOrDefault(c =>
            c.LoadedProperties?.Any(p => p.Name.ToString() == widgetName) == true)
            ?? throw new InvalidOperationException("No unique generated class declares the target widget variable.");
        if (generated.LoadedProperties.Single(p => p.Name.ToString() == widgetName) is not UAssetAPI.FieldTypes.FObjectProperty targetField)
            throw new InvalidOperationException("Target widget variable is not an object property.");
        if (generated.LoadedProperties.SingleOrDefault(p => p.Name.ToString() == templateWidget) is not UAssetAPI.FieldTypes.FObjectProperty sourceField ||
            sourceField.PropertyClass?.Index != targetField.PropertyClass?.Index)
            throw new InvalidOperationException("Template and target must have the same widget property class.");
        var bindingObjects = Property<ArrayPropertyData>(generated, "DynamicBindingObjects");
        var bindings = bindingObjects.Value.OfType<ObjectPropertyData>()
            .Select(p => p.Value?.Index ?? 0).Where(i => i > 0 && i <= asset.Exports.Count)
            .Select(i => asset.Exports[i - 1]).OfType<NormalExport>()
            .SelectMany(e => e.Data.OfType<ArrayPropertyData>().Where(p => p.Name.ToString() == "ComponentDelegateBindings"))
            .ToList();
        string Value(StructPropertyData s, string key) => s.Value.OfType<NamePropertyData>().SingleOrDefault(p => p.Name.ToString() == key)?.Value?.ToString();
        var rows = bindings.SelectMany(a => a.Value.OfType<StructPropertyData>().Select(s => (Array: a, Row: s))).ToList();
        if (rows.Any(x => Value(x.Row, "ComponentPropertyName") == widgetName && Value(x.Row, "DelegatePropertyName") == delegateName))
            throw new InvalidOperationException("The target widget already has a binding for that delegate.");
        var candidates = rows.Where(x => Value(x.Row, "ComponentPropertyName") == templateWidget &&
            Value(x.Row, "DelegatePropertyName") == delegateName && Value(x.Row, "FunctionNameToBind") == functionName).ToList();
        if (candidates.Count != 1)
            throw new InvalidOperationException("The template must have exactly one binding with that delegate and function.");
        var row = (StructPropertyData)candidates[0].Row.Clone();
        var target = row.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "ComponentPropertyName");
        var targetHandler = row.Value.OfType<NamePropertyData>().Single(p => p.Name.ToString() == "FunctionNameToBind");
        asset.AddNameReference(new FString(widgetName));
        target.Value = FName.FromString(asset, widgetName);
        asset.AddNameReference(new FString(targetFunction));
        targetHandler.Value = FName.FromString(asset, targetFunction);
        candidates[0].Array.Value = [.. candidates[0].Array.Value, row];
    }

    /// <summary>
    /// Creates a separate no-op handler with the same signature as an existing bound handler. Its initial
    /// EX_Nothing is an insertion point for bytecode edits. It intentionally does not copy the original action.
    /// </summary>
    public static int CreateIndependentHandler(UAsset asset, string widgetName, string templateWidget, string delegateName, string templateHandler, string newHandler)
    {
        if (!KismetLocals.Supported(asset) || asset.GetCustomVersion<FFrameworkObjectVersion>() < FFrameworkObjectVersion.RemoveUField_Next)
            throw new InvalidOperationException("This operation needs the modern FProperty and array-based UField layouts.");
        if (string.IsNullOrWhiteSpace(newHandler) || newHandler.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
            throw new InvalidOperationException("Handler name must contain only letters, digits and underscores.");
        var generated = asset.Exports.OfType<ClassExport>().SingleOrDefault(c => c.LoadedProperties?.Any(p => p.Name.ToString() == widgetName) == true)
            ?? throw new InvalidOperationException("No unique generated class declares the target widget variable.");
        var ownerIndex = asset.Exports.IndexOf(generated) + 1;
        var source = asset.Exports.OfType<FunctionExport>().SingleOrDefault(f => f.OuterIndex?.Index == ownerIndex && f.ObjectName.ToString() == templateHandler)
            ?? throw new InvalidOperationException("The template handler does not exist on the generated class.");
        if (asset.Exports.Any(e => e.OuterIndex?.Index == ownerIndex && e.ObjectName.ToString().Equals(newHandler, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The generated class already has an object with that name.");
        if (source.ScriptBytecode == null || source.Children?.Length != 0 || source.LoadedProperties == null ||
            source.LoadedProperties.Any(p => p.PropertyFlags.HasFlag(EPropertyFlags.CPF_ReturnParm) || p.PropertyFlags.HasFlag(EPropertyFlags.CPF_OutParm)))
            throw new InvalidOperationException("Only parsed, void handlers without output parameters or child exports are supported.");
        if (asset.DependsMap != null && asset.DependsMap.Count != asset.Exports.Count)
            throw new InvalidOperationException("The dependency map does not match the export table.");
        var copy = (FunctionExport)source.Clone();
        copy.Data = source.Data.Select(p => (PropertyData)p.Clone()).ToList();
        copy.LoadedProperties = source.LoadedProperties.Select(KismetLocals.Clone).ToArray();
        copy.Children = [];
        copy.ScriptBytecode = [new EX_Nothing(), new EX_Return { ReturnExpression = new EX_Nothing() }, new EX_EndOfScript()];
        copy.ScriptBytecodeRaw = null;
        var index = asset.Exports.Count + 1;
        asset.AddNameReference(new FString(newHandler));
        copy.ObjectName = FName.FromString(asset, newHandler);
        // AddBinding performs all compatibility and duplicate checks before it changes the binding array.
        AddBinding(asset, widgetName, templateWidget, delegateName, templateHandler, newHandler);
        generated.FuncMap.Add(copy.ObjectName, FPackageIndex.FromRawIndex(index));
        generated.Children = [.. generated.Children, FPackageIndex.FromRawIndex(index)];
        asset.Exports.Add(copy);
        if (asset.DependsMap != null)
            asset.DependsMap.Add((asset.DependsMap[asset.Exports.IndexOf(source)] ?? Array.Empty<int>()).ToArray());
        return index;
    }

    private static (int Widget, int Slot) Duplicate(UAsset asset, int index, string name, bool bound)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('.') || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Widget name must be a non-empty object name.", nameof(name));
        var widget = Normal(asset, index);
        var className = widget.GetClassTypeForAncestry(asset, out _)?.ToString();
        if (!bound && className is not ("Image" or "TextBlock"))
            throw new InvalidOperationException("Only native Image and TextBlock leaves are supported.");
        if (bound && widget.Data.Any(p => p.Name.ToString() == "Slots" && p is ArrayPropertyData a && a.Value.Length != 0))
            throw new InvalidOperationException("A widget with inline children cannot be duplicated by this operation.");
        if (asset.Exports.Any(e => e.OuterIndex?.Index == widget.OuterIndex?.Index && e.ObjectName.ToString().Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An object with that name already exists in this WidgetTree.");
        var slotIndex = Property<ObjectPropertyData>(widget, "Slot").Value.Index;
        var slot = Normal(asset, slotIndex);
        var parentIndex = Property<ObjectPropertyData>(slot, "Parent").Value.Index;
        var parent = Normal(asset, parentIndex);
        var parentClass = parent.GetClassTypeForAncestry(asset, out _)?.ToString();
        if (parentClass is not ("VerticalBox" or "HorizontalBox" or "CanvasPanel" or "Overlay"))
            throw new InvalidOperationException("The parent must be a supported multi-child panel.");
        if (Property<ObjectPropertyData>(slot, "Content").Value.Index != index || slot.OuterIndex?.Index != parentIndex)
            throw new InvalidOperationException("Widget, slot and parent references do not agree.");
        var slots = Property<ArrayPropertyData>(parent, "Slots");
        var position = Array.FindIndex(slots.Value, p => p is ObjectPropertyData o && o.Value?.Index == slotIndex);
        if (position < 0 || slots.Value.Count(p => p is ObjectPropertyData o && o.Value?.Index == slotIndex) != 1)
            throw new InvalidOperationException("The parent must reference the original slot exactly once.");
        if (widget.Data.Any(p => p is DelegatePropertyData or MulticastDelegatePropertyData))
            throw new InvalidOperationException("Widgets with serialized event delegates are not supported.");

        var slotName = name + "_Slot";
        if (asset.Exports.Any(e => e.OuterIndex?.Index == parentIndex && e.ObjectName.ToString().Equals(slotName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("An object with the generated slot name already exists.");
        if (asset.DependsMap != null && asset.DependsMap.Count != asset.Exports.Count)
            throw new InvalidOperationException("The dependency map does not match the export table.");

        var newWidget = Copy(widget);
        var newSlot = Copy(slot);
        var widgetIndex = asset.Exports.Count + 1;
        var newSlotIndex = widgetIndex + 1;
        asset.AddNameReference(new FString(name));
        newWidget.ObjectName = FName.FromString(asset, name);
        // Slots are objects owned by the parent; use a distinct name in that outer.
        asset.AddNameReference(new FString(slotName));
        newSlot.ObjectName = FName.FromString(asset, slotName);
        Property<ObjectPropertyData>(newWidget, "Slot").Value = FPackageIndex.FromRawIndex(newSlotIndex);
        Property<ObjectPropertyData>(newSlot, "Content").Value = FPackageIndex.FromRawIndex(widgetIndex);
        if (newWidget.Data.OfType<BoolPropertyData>().FirstOrDefault(p => p.Name.ToString() == "bIsVariable") is { } isVariable)
        {
            isVariable.Value = bound;
            isVariable.IsZero = false;
        }
        foreach (var list in new[] { newWidget.SerializationBeforeSerializationDependencies, newWidget.CreateBeforeSerializationDependencies,
                     newWidget.SerializationBeforeCreateDependencies, newWidget.CreateBeforeCreateDependencies,
                     newSlot.SerializationBeforeSerializationDependencies, newSlot.CreateBeforeSerializationDependencies,
                     newSlot.SerializationBeforeCreateDependencies, newSlot.CreateBeforeCreateDependencies })
            for (var i = 0; i < list.Count; i++)
                if (list[i].Index == index) list[i] = FPackageIndex.FromRawIndex(widgetIndex);
                else if (list[i].Index == slotIndex) list[i] = FPackageIndex.FromRawIndex(newSlotIndex);
        var slotReference = (ObjectPropertyData)slots.Value[position].Clone();
        slotReference.Value = FPackageIndex.FromRawIndex(newSlotIndex);
        var values = slots.Value.ToList();
        values.Insert(position + 1, slotReference);
        slots.Value = values.ToArray();
        asset.Exports.Add(newWidget);
        asset.Exports.Add(newSlot);
        if (asset.DependsMap != null)
        {
            int Remap(int dep) => dep == index ? widgetIndex : dep == slotIndex ? newSlotIndex : dep;
            asset.DependsMap.Add((asset.DependsMap[index - 1] ?? Array.Empty<int>()).Select(Remap).ToArray());
            asset.DependsMap.Add((asset.DependsMap[slotIndex - 1] ?? Array.Empty<int>()).Select(Remap).ToArray());
        }
        return (widgetIndex, newSlotIndex);
    }
}
