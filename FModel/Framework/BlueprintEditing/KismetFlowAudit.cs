#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.Kismet;
using UAssetAPI.Kismet.Bytecode;
using UAssetAPI.Kismet.Bytecode.Expressions;

namespace FModel.Framework.BlueprintEditing;

public sealed record FlowFinding(string Severity, string Kind, string Message, string Key);

/// <summary>
/// Checks what the compiled control flow of a function can do, beyond whether it is well-formed: statements nothing reaches, loops that can't be left,
/// execution flow pushes that pile up, no path to the end. The execution-flow stack (<c>EX_PushExecutionFlow</c> / <c>EX_PopExecutionFlow</c>) is simulated.
/// </summary>
public static class KismetFlowAudit
{
    private const int MaxStates = 200_000;
    private const int MaxStack = 64;

    public sealed record Result(int Statements, int Reachable, int Loops, List<FlowFinding> Findings, bool Truncated);

    private sealed record State(int Index, string Stack);

    public static Result Audit(UAsset asset, StructExport function)
    {
        var code = function.ScriptBytecode;
        var findings = new List<FlowFinding>();
        if (code == null || code.Length == 0)
            return new Result(0, 0, 0, findings, false);

        // statement start offsets
        var offsets = new uint[code.Length];
        var byOffset = new Dictionary<uint, int>();
        uint position = 0;
        for (var i = 0; i < code.Length; i++)
        {
            offsets[i] = position;
            byOffset.TryAdd(position, i);
            position += code[i].GetSize(asset);
        }
        var end = position;

        int TargetOf(uint offset, string what, int from)
        {
            if (byOffset.TryGetValue(offset, out var index))
                return index;
            if (offset == end)
                return code.Length; // the end of the code
            findings.Add(new FlowFinding("error", "bad-target", $"{what} at @{offsets[from]} points to @{offset}, which is not the start of a statement", $"bad-target:{what}"));
            return -1;
        }

        // roots: the start, the entry points event stubs pass to the ubergraph, and latent resume points
        var roots = new List<int> { 0 };
        if (KismetCodeLayout.IsUbergraph(function))
            roots.AddRange(KismetCodeLayout.FindUbergraphEntryPoints(asset, function).Select(o => byOffset.TryGetValue((uint) o.EntryPoint.Value, out var i) ? i : -1).Where(o => o >= 0));
        for (var i = 0; i < code.Length; i++)
        {
            uint scan = offsets[i];
            code[i].Visit(asset, ref scan, (expression, _) =>
            {
                if (expression is EX_SkipOffsetConst skip && byOffset.TryGetValue(skip.Value, out var resume))
                    roots.Add(resume);
            });
        }

        // explore (statement, flow stack) states
        var visited = new HashSet<State>();
        var edges = new Dictionary<State, List<State>>();
        var terminals = new HashSet<State>();
        var reachable = new bool[code.Length];
        var truncated = false;
        var unboundedAt = -1;
        var computedJumps = new HashSet<int>();

        var queue = new Queue<State>();
        foreach (var root in roots.Distinct())
        {
            var state = new State(root, "");
            if (visited.Add(state))
                queue.Enqueue(state);
        }

        while (queue.Count > 0)
        {
            if (visited.Count > MaxStates)
            {
                truncated = true;
                break;
            }
            var state = queue.Dequeue();
            if (state.Index >= code.Length)
            {
                terminals.Add(state);
                continue;
            }
            reachable[state.Index] = true;
            var statement = code[state.Index];
            var stack = state.Stack.Length == 0 ? [] : state.Stack.Split(',').Select(int.Parse).ToList();
            var next = new List<State>();

            void Go(int index, List<int> newStack)
            {
                if (index < 0)
                    return;
                next.Add(new State(index, string.Join(",", newStack)));
            }

            switch (statement)
            {
                case EX_Jump jump:
                    Go(TargetOf(jump.CodeOffset, "jump", state.Index), stack);
                    break;
                case EX_JumpIfNot jumpIfNot:
                    Go(TargetOf(jumpIfNot.CodeOffset, "conditional jump", state.Index), stack);
                    Go(state.Index + 1, stack);
                    break;
                case EX_PushExecutionFlow push:
                {
                    var resume = TargetOf(push.PushingAddress, "execution flow push", state.Index);
                    if (stack.Count >= MaxStack)
                    {
                        unboundedAt = state.Index;
                        break;
                    }
                    Go(state.Index + 1, [.. stack, resume]);
                    break;
                }
                case EX_PopExecutionFlow:
                    if (stack.Count == 0)
                        terminals.Add(state); // ends the thread of execution
                    else
                        Go(stack[^1], stack[..^1]);
                    break;
                case EX_PopExecutionFlowIfNot:
                    if (stack.Count == 0)
                        terminals.Add(state);
                    else
                        Go(stack[^1], stack[..^1]);
                    Go(state.Index + 1, stack);
                    break;
                case EX_Return or EX_EndOfScript:
                    terminals.Add(state);
                    break;
                case EX_ComputedJump:
                    computedJumps.Add(state.Index);
                    terminals.Add(state);
                    break;
                default:
                    Go(state.Index + 1, stack);
                    break;
            }

            edges[state] = next;
            foreach (var successor in next)
            {
                if (visited.Add(successor))
                    queue.Enqueue(successor);
            }
        }

        // states from which an end can be reached (walk the edges backwards from the terminals)
        var canEnd = new HashSet<State>(terminals);
        var reverse = new Dictionary<State, List<State>>();
        foreach (var (from, targets) in edges)
            foreach (var target in targets)
                (reverse.TryGetValue(target, out var list) ? list : reverse[target] = []).Add(from);
        var work = new Queue<State>(canEnd);
        while (work.Count > 0)
        {
            var state = work.Dequeue();
            if (!reverse.TryGetValue(state, out var predecessors))
                continue;
            foreach (var predecessor in predecessors)
                if (canEnd.Add(predecessor))
                    work.Enqueue(predecessor);
        }

        // findings
        var trapped = visited.Where(o => !canEnd.Contains(o) && o.Index < code.Length).Select(o => o.Index).Distinct().Order().ToList();
        if (trapped.Count > 0 && !truncated)
            findings.Add(new FlowFinding("error", "no-exit", $"possible endless loop: {Range(trapped, offsets)} can be reached but nothing after them leads to the end of the function", "no-exit"));
        if (unboundedAt >= 0)
            findings.Add(new FlowFinding("error", "stack-growth", $"the execution flow stack grows without bound: the push at @{offsets[unboundedAt]} is reached again and again without a matching pop", "stack-growth"));

        // statements nothing reaches (the padding EX_Nothing and the final EndOfScript are normal)
        var unreachable = Enumerable.Range(0, code.Length).Where(i => !reachable[i] && code[i] is not (EX_Nothing or EX_EndOfScript or EX_NothingInt32)).ToList();
        if (unreachable.Count > 0 && !truncated)
            findings.Add(new FlowFinding("warning", "unreachable", $"{unreachable.Count} statement(s) can't be reached: {Range(unreachable, offsets)} ({string.Join(", ", unreachable.Take(5).Select(i => code[i].Token.ToString().Replace("EX_", "")))}{(unreachable.Count > 5 ? ", ..." : "")})", $"unreachable:{unreachable.Count}"));

        var loops = 0;
        for (var i = 0; i < code.Length; i++)
        {
            var target = code[i] switch { EX_Jump j => (int?) TargetQuiet(j.CodeOffset), EX_JumpIfNot j => TargetQuiet(j.CodeOffset), _ => null };
            if (target is { } t && t >= 0 && t <= i)
            {
                loops++;
                findings.Add(new FlowFinding("info", "loop", $"loop: the {code[i].Token.ToString().Replace("EX_", "")} at @{offsets[i]} jumps back to @{offsets[t]}", $"loop:{code[i].Token}"));
            }
        }
        int TargetQuiet(uint offset) => byOffset.TryGetValue(offset, out var index) ? index : -1;

        if (!visited.Any(o => terminals.Contains(o)) && !truncated)
            findings.Add(new FlowFinding("error", "no-end", "no path reaches a return or the end of the function", "no-end"));
        if (computedJumps.Count > 0)
            findings.Add(new FlowFinding("info", "computed-jump", $"{computedJumps.Count} computed jump(s) (not analyzed): the paths after them are unknown", "computed-jump"));
        if (truncated)
            findings.Add(new FlowFinding("info", "truncated", $"the analysis stopped after {MaxStates} states (the function has too many paths); reachability findings are left out", "truncated"));

        return new Result(code.Length, reachable.Count(o => o), loops, findings, truncated);
    }

    private static string Range(IReadOnlyList<int> indices, uint[] offsets)
    {
        var ranges = new List<string>();
        var start = indices[0];
        var previous = start;
        foreach (var index in indices.Skip(1).Append(int.MaxValue))
        {
            if (index == previous + 1)
            {
                previous = index;
                continue;
            }
            ranges.Add(start == previous ? $"@{offsets[start]}" : $"@{offsets[start]}..@{offsets[previous]}");
            start = previous = index;
            if (ranges.Count >= 6)
                break;
        }
        return string.Join(", ", ranges) + (ranges.Count >= 6 ? ", ..." : "");
    }

    /// <summary>
    /// The findings the edit adds: kinds that weren't there, and counts that grew (unreachable statements, loops). Loops that were there stay quiet.
    /// </summary>
    public static List<string> NewFindings(Result before, Result after)
    {
        var known = before.Findings.GroupBy(o => o.Key).ToDictionary(o => o.Key, o => o.Count());
        var result = new List<string>();
        foreach (var finding in after.Findings)
        {
            // an unreachable count that changed is a new finding even though the kind existed
            if (finding.Kind == "unreachable" || !known.ContainsKey(finding.Key) || known[finding.Key]-- <= 0)
            {
                if (finding.Kind == "unreachable" && before.Findings.Any(o => o.Key == finding.Key))
                    continue;
                result.Add($"{finding.Severity.ToUpperInvariant()} {finding.Message}");
            }
        }
        if (before.Reachable == before.Statements && after.Reachable < after.Statements && !after.Findings.Any(o => o.Kind == "unreachable"))
            result.Add($"{after.Statements - after.Reachable} statement(s) became unreachable");
        return result;
    }

    public static string Format(Result result, string function)
    {
        var sb = new StringBuilder($"{function}: {result.Statements} statements, {result.Reachable} reachable, {result.Loops} loop(s)\n");
        if (result.Findings.Count == 0)
            sb.AppendLine("no findings: every statement is reachable, every path can end.");
        foreach (var finding in result.Findings.OrderBy(o => o.Severity switch { "error" => 0, "warning" => 1, _ => 2 }))
            sb.AppendLine($"- {finding.Severity.ToUpperInvariant()} {finding.Message}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A readable difference between the statements of a function before and after an edit, independent of how offsets and references are stored.</summary>
public static class KismetDiff
{
    private static readonly Regex CodeTarget = new(@"^@(\d+)$", RegexOptions.Compiled);

    /// <summary>The statements as normalized JSON lines: code offsets become statement numbers ("->#12"), so inserting a statement doesn't change every jump.</summary>
    public static List<(JObject Json, string Line, uint Offset)> Normalize(UAsset asset, StructExport function)
    {
        var code = function.ScriptBytecode ?? [];
        var offsets = new List<uint>();
        uint position = 0;
        foreach (var statement in code)
        {
            offsets.Add(position);
            position += statement.GetSize(asset);
        }
        var index = offsets.Select((o, i) => (o, i)).ToDictionary(o => o.o, o => o.i);
        index.TryAdd(position, code.Length);

        // a jump is named by the statement it reaches (its content and which equal statement it is), not by its position, so inserting code doesn't change every jump
        var jsons = code.Select(o => KismetObjects.ToJson(asset, o)).ToList();
        var targets = jsons.Select(o => o.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String && CodeTarget.IsMatch(v.Value<string>()!)).ToList()).ToList();
        foreach (var value in targets.SelectMany(o => o))
            value.Value = "->?";
        var contents = jsons.Select(o => o.ToString(Newtonsoft.Json.Formatting.None)).ToList();
        var seen = new Dictionary<string, int>();
        var keys = contents.Select(o => $"{o}#{seen[o] = seen.GetValueOrDefault(o) + 1}").ToList();

        var result = new List<(JObject, string, uint)>();
        for (var i = 0; i < code.Length; i++)
        {
            // read the original targets again (they were blanked for the content keys)
            var original = KismetObjects.ToJson(asset, code[i]);
            var originalTargets = original.DescendantsAndSelf().OfType<JValue>().Where(v => v.Type == JTokenType.String && CodeTarget.IsMatch(v.Value<string>()!)).ToList();
            for (var k = 0; k < originalTargets.Count; k++)
            {
                var offset = uint.Parse(CodeTarget.Match(originalTargets[k].Value<string>()!).Groups[1].Value);
                targets[i][k].Value = index.TryGetValue(offset, out var target) && target < code.Length ? $"->{Hash(keys[target])}" : "->end";
            }
            result.Add((jsons[i], jsons[i].ToString(Newtonsoft.Json.Formatting.None), offsets[i]));
        }
        return result;
    }

    private static string Hash(string key) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..8];

    public static string Compare(UAsset beforeAsset, StructExport before, UAsset afterAsset, StructExport after, int limit = 40)
    {
        var a = Normalize(beforeAsset, before);
        var b = Normalize(afterAsset, after);

        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix].Line == b[prefix].Line)
            prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[^(suffix + 1)].Line == b[^(suffix + 1)].Line)
            suffix++;
        var middleA = a.Skip(prefix).Take(a.Count - prefix - suffix).ToList();
        var middleB = b.Skip(prefix).Take(b.Count - prefix - suffix).ToList();
        if (middleA.Count == 0 && middleB.Count == 0)
            return "no statement changed";
        if ((long) middleA.Count * middleB.Count > 16_000_000)
            return $"{middleA.Count} statements before and {middleB.Count} after differ: too many to align";

        // longest common subsequence of the changed middle
        var lcs = new int[middleA.Count + 1, middleB.Count + 1];
        for (var i = middleA.Count - 1; i >= 0; i--)
            for (var j = middleB.Count - 1; j >= 0; j--)
                lcs[i, j] = middleA[i].Line == middleB[j].Line ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var lines = new List<string>();
        var removed = new List<int>();
        var added = new List<int>();

        void Flush()
        {
            // a removed statement right before an added one is a changed statement: show what changed inside it
            var pairs = Math.Min(removed.Count, added.Count);
            for (var k = 0; k < pairs; k++)
                lines.Add($"~ statement {prefix + removed[k]} (@{middleA[removed[k]].Offset}) {middleA[removed[k]].Json.Value<string>("op")} changed: " +
                          string.Join("; ", Cells(middleA[removed[k]].Json, middleB[added[k]].Json).Take(6)));
            foreach (var i in removed.Skip(pairs))
                lines.Add($"- statement {prefix + i} (@{middleA[i].Offset}) removed: {Short(middleA[i].Line)}");
            foreach (var j in added.Skip(pairs))
                lines.Add($"+ new statement {prefix + j} (@{middleB[j].Offset} in the edited code): {Short(middleB[j].Line)}");
            removed.Clear();
            added.Clear();
        }

        for (int i = 0, j = 0; i < middleA.Count || j < middleB.Count;)
        {
            if (i < middleA.Count && j < middleB.Count && middleA[i].Line == middleB[j].Line)
            {
                Flush();
                i++;
                j++;
            }
            else if (j < middleB.Count && (i == middleA.Count || lcs[i, j + 1] >= lcs[i + 1, j]))
                added.Add(j++);
            else
                removed.Add(i++);
        }
        Flush();

        return $"{lines.Count} difference(s) (statement numbers count from 0; jumps compare by the statement they reach):\n" +
               string.Join("\n", lines.Take(limit)) + (lines.Count > limit ? $"\n... {lines.Count - limit} more" : "");
    }

    private static IEnumerable<string> Cells(JToken before, JToken after, string path = "")
    {
        if (before is JObject b && after is JObject a)
        {
            foreach (var key in b.Properties().Select(o => o.Name).Union(a.Properties().Select(o => o.Name)))
                foreach (var cell in Cells(b[key] ?? JValue.CreateNull(), a[key] ?? JValue.CreateNull(), path.Length == 0 ? key : $"{path}.{key}"))
                    yield return cell;
        }
        else if (before is JArray ba && after is JArray aa && ba.Count == aa.Count)
        {
            for (var i = 0; i < ba.Count; i++)
                foreach (var cell in Cells(ba[i], aa[i], $"{path}[{i}]"))
                    yield return cell;
        }
        else if (!JToken.DeepEquals(before, after))
        {
            yield return $"{path}: {Short(before.ToString(Newtonsoft.Json.Formatting.None))} -> {Short(after.ToString(Newtonsoft.Json.Formatting.None))}";
        }
    }

    private static string Short(string text) => text.Length > 140 ? text[..137] + "..." : text;
}
