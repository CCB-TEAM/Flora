using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;
using UAssetAPI.UnrealTypes;

namespace UAssetAPI.Kismet
{
    /// <summary>
    /// Keeps the code offsets of a function's bytecode consistent while it is edited.
    /// <para>
    /// <see cref="Bind"/> turns every absolute code offset into a reference to the instruction it points at: jumps, execution flow pushes,
    /// latent resume points (<see cref="EX_SkipOffsetConst"/> and pre-4.18 LatentActionInfo linkage ints) and, for the ubergraph,
    /// the entry points that event stubs of the other functions pass to it. Expressions can then be changed, replaced
    /// (<see cref="Replace(KismetExpression, KismetExpression)"/>) or statements inserted into and removed from <see cref="StructExport.ScriptBytecode"/>;
    /// <see cref="Commit"/> lays the code out again and writes every offset back, including the relative skips of
    /// <see cref="EX_Context"/>/<see cref="EX_Skip"/> and the case offsets of <see cref="EX_SwitchValue"/>.
    /// </para>
    /// All offsets are in-memory offsets (as reported by <see cref="KismetExpression.Visit"/>).
    /// </summary>
    public sealed class KismetCodeLayout
    {
        private sealed class Target
        {
            public KismetExpression Expression; // null: the end of the code
        }

        private sealed record Binding(Target Target, Action<uint> Write, string Description);

        private readonly List<Binding> _bindings = new List<Binding>();
        private readonly Dictionary<KismetExpression, Target> _targets = new Dictionary<KismetExpression, Target>(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<EX_IntConst, Binding> _entryBindings = new Dictionary<EX_IntConst, Binding>(ReferenceEqualityComparer.Instance);
        private readonly Target _endOfCode = new Target();
        private readonly Dictionary<KismetExpression, uint> _relativeSizes = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
        private Dictionary<KismetExpression, uint> _originalOffsets;
        private Dictionary<uint, List<KismetExpression>> _originalByOffset;

        public UAsset Asset { get; }
        public StructExport Function { get; }

        /// <summary>Offset problems found while binding (offsets that point inside an instruction); they are left untouched.</summary>
        public List<string> Warnings { get; } = new List<string>();

        private KismetCodeLayout(UAsset asset, StructExport function)
        {
            Asset = asset;
            Function = function;
        }

        /// <summary>
        /// Binds the code offsets of a function. Bind before changing anything.
        /// </summary>
        public static KismetCodeLayout Bind(UAsset asset, StructExport function)
        {
            if (function?.ScriptBytecode == null)
                throw new ArgumentException("The function has no parsed bytecode.", nameof(function));

            var layout = new KismetCodeLayout(asset, function);
            layout.BindAll();
            return layout;
        }

        /// <summary>The offset every expression had when the layout was bound (or last committed).</summary>
        public IReadOnlyDictionary<KismetExpression, uint> OriginalOffsets => _originalOffsets;

        /// <summary>Finds an expression by the offset it had when bound, optionally with a given token.</summary>
        public KismetExpression FindOriginal(uint offset, EExprToken? token = null)
        {
            if (!_originalByOffset.TryGetValue(offset, out var list)) return null;
            return token == null ? list[0] : list.FirstOrDefault(o => o.Token == token);
        }

        /// <summary>
        /// Puts <paramref name="replacement"/> where <paramref name="original"/> is referenced in the function (the statement list
        /// and any expression field, array or switch case). Offsets that pointed at the original now point at the replacement.
        /// </summary>
        public void Replace(KismetExpression original, KismetExpression replacement)
        {
            Replace(new Dictionary<KismetExpression, KismetExpression>(ReferenceEqualityComparer.Instance) { [original] = replacement });
        }

        /// <inheritdoc cref="Replace(KismetExpression, KismetExpression)"/>
        public void Replace(IReadOnlyDictionary<KismetExpression, KismetExpression> replacements)
        {
            if (replacements.Count == 0) return;
            KismetExpressionTree.Replace(Function, replacements);
            foreach (var (original, replacement) in replacements)
            {
                if (_targets.Remove(original, out var target))
                {
                    target.Expression = replacement;
                    _targets[replacement] = target;
                }
                if (_relativeSizes.Remove(original, out var size))
                    _relativeSizes[replacement] = size;
            }
        }

        /// <summary>
        /// Inserts statements right after <paramref name="statement"/> (a top-level statement of the function).
        /// Offsets that pointed at the statement that followed keep pointing at it; only the fall-through of <paramref name="statement"/> changes.
        /// </summary>
        public void InsertAfter(KismetExpression statement, params KismetExpression[] statements)
        {
            var list = new List<KismetExpression>(Function.ScriptBytecode);
            var index = list.FindIndex(o => ReferenceEquals(o, statement));
            if (index < 0)
                throw new ArgumentException($"{statement.Token} is not a statement of {Function.ObjectName}.", nameof(statement));
            list.InsertRange(index + 1, statements);
            Function.ScriptBytecode = list.ToArray();
        }

        /// <summary>Makes a new jump point at <paramref name="target"/> (a statement of the function); the offset is written by <see cref="Commit"/>.</summary>
        public void BindJump(EX_Jump jump, KismetExpression target)
        {
            if (!ReferenceEquals(target, Function.ScriptBytecode.FirstOrDefault(o => ReferenceEquals(o, target))))
                throw new ArgumentException($"{target.Token} is not a statement of {Function.ObjectName}.", nameof(target));
            _bindings.Add(new Binding(GetTarget(target), o => jump.CodeOffset = o, "inserted jump"));
        }

        /// <summary>
        /// Makes a code offset of a new expression (a conditional jump, an execution flow push, a latent resume point...) point at
        /// <paramref name="target"/>, a statement of the function by the time <see cref="Commit"/> runs; <paramref name="write"/> receives the offset.
        /// </summary>
        public void BindCodeOffset(KismetExpression target, Action<uint> write, string description)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            _bindings.Add(new Binding(GetTarget(target), write, description));
        }

        /// <summary>
        /// Makes the ubergraph entry point stored in <paramref name="entryPoint"/> (an int an event stub passes) start at <paramref name="target"/>
        /// (a statement of the ubergraph) instead; the value is written by <see cref="Commit"/>.
        /// </summary>
        public void RedirectEntryPoint(EX_IntConst entryPoint, KismetExpression target)
        {
            if (!IsStatement(target))
                throw new ArgumentException($"{target.Token} is not a statement of {Function.ObjectName}.", nameof(target));
            if (_entryBindings.Remove(entryPoint, out var old))
                _bindings.Remove(old);
            var binding = new Binding(GetTarget(target), o => entryPoint.Value = checked((int)o), "redirected entry point");
            _bindings.Add(binding);
            _entryBindings[entryPoint] = binding;
        }

        /// <summary>Whether the expression is a top-level statement of the function.</summary>
        public bool IsStatement(KismetExpression expression) => Function.ScriptBytecode.Any(o => ReferenceEquals(o, expression));

        /// <summary>
        /// Lays the code out again and writes every bound offset. Throws when an instruction an offset points at is no longer
        /// part of the function. The layout stays bound to the new offsets, so editing can go on.
        /// </summary>
        public void Commit()
        {
            var offsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
            uint end = 0;
            foreach (var statement in Function.ScriptBytecode)
                statement.Visit(Asset, ref end, (expression, offset) => offsets.TryAdd(expression, offset));

            foreach (var binding in _bindings)
            {
                uint value;
                if (binding.Target == _endOfCode)
                    value = end;
                else if (!offsets.TryGetValue(binding.Target.Expression, out value))
                    throw new InvalidOperationException($"{Function.ObjectName}: the {binding.Description} points at a {binding.Target.Expression.Token} that is no longer in the function.");
                binding.Write(value);
            }

            foreach (var (expression, offset) in offsets)
            {
                switch (expression)
                {
                    case EX_Context context when context.ContextExpression != null && _relativeSizes.TryGetValue(context, out var oldSize):
                    {
                        // bytes to skip when the object is null: the r-value pointer and the context expression
                        var newSize = context.ContextExpression.GetSize(Asset);
                        context.Offset = checked((uint)(context.Offset + ((long)newSize - oldSize)));
                        break;
                    }
                    case EX_Skip skip when skip.SkipExpression != null && _relativeSizes.TryGetValue(skip, out var oldSize):
                    {
                        var newSize = skip.SkipExpression.GetSize(Asset);
                        skip.CodeOffset = checked((uint)(skip.CodeOffset + ((long)newSize - oldSize)));
                        break;
                    }
                    case EX_SwitchValue switchValue:
                        switchValue.EndGotoOffset = offset + switchValue.GetSize(Asset);
                        for (int i = 0; i < switchValue.Cases.Length; i++)
                        {
                            var next = i + 1 < switchValue.Cases.Length ? switchValue.Cases[i + 1].CaseIndexValueTerm : switchValue.DefaultTerm;
                            if (next == null || !offsets.TryGetValue(next, out var nextOffset)) continue;
                            var @case = switchValue.Cases[i];
                            @case.NextOffset = nextOffset;
                            switchValue.Cases[i] = @case;
                        }
                        break;
                }
            }

            // stay bound: the next commit is relative to this layout
            RecordOffsets();
            RecordRelativeSizes();
        }

        /// <summary>
        /// Checks that every absolute code offset of the function lands on a statement boundary and that switches are consistent.
        /// </summary>
        public static List<string> Validate(UAsset asset, StructExport function)
        {
            var problems = new List<string>();
            var statementStarts = new HashSet<uint>();
            var offsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
            uint position = 0;
            foreach (var statement in function.ScriptBytecode)
            {
                statementStarts.Add(position);
                statement.Visit(asset, ref position, (expression, offset) => offsets.TryAdd(expression, offset));
            }
            statementStarts.Add(position);

            void Check(uint target, string what)
            {
                if (!statementStarts.Contains(target))
                    problems.Add($"{function.ObjectName}: {what} points to {target}, which is not a statement boundary");
            }

            foreach (var (expression, offset) in offsets)
            {
                switch (expression)
                {
                    case EX_Jump jump: Check(jump.CodeOffset, $"jump at {offset}"); break;
                    case EX_JumpIfNot jumpIfNot: Check(jumpIfNot.CodeOffset, $"conditional jump at {offset}"); break;
                    case EX_PushExecutionFlow push: Check(push.PushingAddress, $"execution flow push at {offset}"); break;
                    case EX_SkipOffsetConst skipConst: Check(skipConst.Value, $"latent resume point at {offset}"); break;
                    case EX_SwitchValue switchValue:
                    {
                        var end = offset + switchValue.GetSize(asset);
                        if (switchValue.EndGotoOffset != end)
                            problems.Add($"{function.ObjectName}: switch at {offset} ends at {end} but jumps to {switchValue.EndGotoOffset}");
                        for (int i = 0; i < switchValue.Cases.Length; i++)
                        {
                            var next = i + 1 < switchValue.Cases.Length ? switchValue.Cases[i + 1].CaseIndexValueTerm : switchValue.DefaultTerm;
                            if (next != null && offsets.TryGetValue(next, out var nextOffset) && switchValue.Cases[i].NextOffset != nextOffset)
                                problems.Add($"{function.ObjectName}: switch case {i} at {offset} points to {switchValue.Cases[i].NextOffset} instead of {nextOffset}");
                        }
                        break;
                    }
                }
            }

            if (IsUbergraph(function))
            {
                foreach (var (caller, entryPoint) in FindUbergraphEntryPoints(asset, function))
                {
                    if (!statementStarts.Contains((uint)entryPoint.Value))
                        problems.Add($"{caller.ObjectName}: ubergraph entry point {entryPoint.Value} is not a statement boundary of {function.ObjectName}");
                }
            }
            return problems;
        }

        /// <summary>Whether the function is the event graph that event stubs call with an entry point offset.</summary>
        public static bool IsUbergraph(StructExport function) =>
            function.ObjectName?.ToString().StartsWith("ExecuteUbergraph", StringComparison.Ordinal) == true;

        /// <summary>The entry point constants that the other functions of the asset pass to the ubergraph.</summary>
        public static IEnumerable<(StructExport Caller, EX_IntConst EntryPoint)> FindUbergraphEntryPoints(UAsset asset, StructExport ubergraph)
        {
            var name = ubergraph.ObjectName.ToString();
            var found = new List<(StructExport, EX_IntConst)>();
            foreach (var function in asset.Exports.OfType<StructExport>())
            {
                if (function == ubergraph || function.ScriptBytecode == null) continue;

                uint position = 0;
                foreach (var statement in function.ScriptBytecode)
                {
                    statement.Visit(asset, ref position, (expression, _) =>
                    {
                        var (calls, parameters) = expression switch
                        {
                            EX_FinalFunction final => (CallsExport(asset, final.StackNode, name), final.Parameters),
                            EX_VirtualFunction virtualCall => (virtualCall.VirtualFunctionName?.ToString() == name, virtualCall.Parameters),
                            _ => (false, null),
                        };
                        if (calls && parameters is { Length: > 0 } && parameters[0] is EX_IntConst { Value: >= 0 } entryPoint)
                            found.Add((function, entryPoint));
                    });
                }
            }
            return found;
        }

        private static bool CallsExport(UAsset asset, FPackageIndex index, string name) =>
            index != null && index.IsExport() && index.ToExport(asset).ObjectName.ToString() == name;

        private void BindAll()
        {
            RecordOffsets();

            var statementStarts = new Dictionary<uint, KismetExpression>();
            uint position = 0;
            foreach (var statement in Function.ScriptBytecode)
            {
                statementStarts.TryAdd(position, statement);
                position += statement.GetSize(Asset);
            }
            var end = position;

            void BindOffset(uint offset, Action<uint> write, string description)
            {
                Target target;
                if (offset == end)
                    target = _endOfCode;
                else if (statementStarts.TryGetValue(offset, out var statement) || _originalByOffset.TryGetValue(offset, out var nested) && (statement = nested[0]) != null)
                    target = GetTarget(statement);
                else
                {
                    Warnings.Add($"{Function.ObjectName}: the {description} points to {offset}, which is not the start of an instruction");
                    return;
                }
                _bindings.Add(new Binding(target, write, description));
            }

            foreach (var (expression, offset) in _originalOffsets)
            {
                switch (expression)
                {
                    case EX_Jump jump:
                        BindOffset(jump.CodeOffset, o => jump.CodeOffset = o, $"jump at {offset}");
                        break;
                    case EX_JumpIfNot jumpIfNot:
                        BindOffset(jumpIfNot.CodeOffset, o => jumpIfNot.CodeOffset = o, $"conditional jump at {offset}");
                        break;
                    case EX_PushExecutionFlow push:
                        BindOffset(push.PushingAddress, o => push.PushingAddress = o, $"execution flow push at {offset}");
                        break;
                    case EX_SkipOffsetConst skipConst:
                        BindOffset(skipConst.Value, o => skipConst.Value = o, $"latent resume point at {offset}");
                        break;
                    case EX_StructConst structConst when IsLatentActionInfo(structConst) &&
                                                          structConst.Value is { Length: > 0 } fields && fields[0] is EX_IntConst { Value: >= 0 } linkage:
                        // pre-4.18 cooks store the latent resume point as a plain int
                        BindOffset((uint)linkage.Value, o => linkage.Value = checked((int)o), $"latent action linkage at {offset}");
                        break;
                }
            }

            if (IsUbergraph(Function))
            {
                foreach (var (caller, entryPoint) in FindUbergraphEntryPoints(Asset, Function))
                {
                    var before = _bindings.Count;
                    BindOffset((uint)entryPoint.Value, o => entryPoint.Value = checked((int)o), $"entry point passed by {caller.ObjectName}");
                    if (_bindings.Count > before)
                        _entryBindings[entryPoint] = _bindings[^1];
                }
            }

            RecordRelativeSizes();
        }

        private Target GetTarget(KismetExpression expression)
        {
            if (!_targets.TryGetValue(expression, out var target))
                _targets[expression] = target = new Target { Expression = expression };
            return target;
        }

        private void RecordOffsets()
        {
            _originalOffsets = new Dictionary<KismetExpression, uint>(ReferenceEqualityComparer.Instance);
            _originalByOffset = new Dictionary<uint, List<KismetExpression>>();
            uint position = 0;
            foreach (var statement in Function.ScriptBytecode)
            {
                statement.Visit(Asset, ref position, (expression, offset) =>
                {
                    if (!_originalOffsets.TryAdd(expression, offset)) return;
                    if (!_originalByOffset.TryGetValue(offset, out var list))
                        _originalByOffset[offset] = list = new List<KismetExpression>();
                    list.Add(expression);
                });
            }
        }

        private void RecordRelativeSizes()
        {
            _relativeSizes.Clear();
            foreach (var expression in _originalOffsets.Keys)
            {
                switch (expression)
                {
                    case EX_Context { ContextExpression: { } contextExpression } context:
                        _relativeSizes[context] = contextExpression.GetSize(Asset);
                        break;
                    case EX_Skip { SkipExpression: { } skipExpression } skip:
                        _relativeSizes[skip] = skipExpression.GetSize(Asset);
                        break;
                }
            }
        }

        private bool IsLatentActionInfo(EX_StructConst structConst)
        {
            var index = structConst.Struct;
            if (index == null || index.IsNull()) return false;
            var name = index.IsImport() ? index.ToImport(Asset).ObjectName.ToString() : index.ToExport(Asset).ObjectName.ToString();
            return name == "LatentActionInfo";
        }
    }

    /// <summary>
    /// Helpers to work on Kismet expression trees.
    /// </summary>
    public static class KismetExpressionTree
    {
        /// <summary>
        /// Swaps expressions wherever the function references them: the statement list, expression fields, expression arrays,
        /// switch cases and generic constants (<see cref="KismetExpression.RawValue"/>).
        /// </summary>
        public static void Replace(StructExport function, IReadOnlyDictionary<KismetExpression, KismetExpression> replacements)
        {
            if (replacements.Count == 0 || function.ScriptBytecode == null) return;

            var statements = function.ScriptBytecode;
            for (int i = 0; i < statements.Length; i++)
            {
                if (replacements.TryGetValue(statements[i], out var top))
                    statements[i] = top;
            }

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var statement in statements)
                ReplaceIn(statement, replacements, visited);
        }

        private static void ReplaceIn(object holder, IReadOnlyDictionary<KismetExpression, KismetExpression> replacements, HashSet<object> visited)
        {
            if (holder == null || !visited.Add(holder)) return;

            foreach (var field in FieldsOf(holder.GetType()))
            {
                var value = field.GetValue(holder);
                switch (value)
                {
                    case KismetExpression expression:
                        if (replacements.TryGetValue(expression, out var replacement))
                            field.SetValue(holder, expression = replacement);
                        ReplaceIn(expression, replacements, visited);
                        break;
                    case KismetExpression[] array:
                        ReplaceInArray(array, replacements, visited);
                        break;
                    case FKismetSwitchCase[] cases:
                        for (int i = 0; i < cases.Length; i++)
                        {
                            var @case = cases[i];
                            if (@case.CaseIndexValueTerm != null && replacements.TryGetValue(@case.CaseIndexValueTerm, out var index))
                                @case.CaseIndexValueTerm = index;
                            if (@case.CaseTerm != null && replacements.TryGetValue(@case.CaseTerm, out var term))
                                @case.CaseTerm = term;
                            cases[i] = @case;
                            ReplaceIn(@case.CaseIndexValueTerm, replacements, visited);
                            ReplaceIn(@case.CaseTerm, replacements, visited);
                        }
                        break;
                    case object nested when !nested.GetType().IsValueType && nested is not string &&
                                            nested.GetType().Namespace?.StartsWith("UAssetAPI.Kismet", StringComparison.Ordinal) == true:
                        ReplaceIn(nested, replacements, visited); // e.g. the FScriptText of text constants
                        break;
                }
            }
        }

        private static void ReplaceInArray(KismetExpression[] array, IReadOnlyDictionary<KismetExpression, KismetExpression> replacements, HashSet<object> visited)
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (array[i] != null && replacements.TryGetValue(array[i], out var element))
                    array[i] = element;
                ReplaceIn(array[i], replacements, visited);
            }
        }

        private static readonly Dictionary<Type, FieldInfo[]> _fields = new Dictionary<Type, FieldInfo[]>();

        private static FieldInfo[] FieldsOf(Type type)
        {
            lock (_fields)
            {
                if (_fields.TryGetValue(type, out var fields)) return fields;
                var list = new List<FieldInfo>();
                for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                    list.AddRange(t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                return _fields[type] = list.ToArray();
            }
        }
    }
}
