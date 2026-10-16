using System.Collections.Generic;

namespace FModel.Framework.Packing;

/// <summary>What is dragged into the packing workspace: files of a source (null: the loaded game).</summary>
public sealed record PackDragData(PackSource? Source, IReadOnlyList<string> Paths);
