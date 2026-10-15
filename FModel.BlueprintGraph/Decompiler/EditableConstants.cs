using System.Collections.Generic;
using System.Globalization;
using CUE4Parse.UE4.Kismet;

namespace FModel.BlueprintGraph.Decompiler
{
    public enum EditableConstantKind
    {
        Bool,
        Int,
        Int64,
        UInt64,
        Float,
        Double,
        Byte,
        Name,
        String,
    }

    /// <summary>
    /// Literal bytecode constants whose value can be edited from a graph pin.
    /// </summary>
    public static class EditableConstants
    {
        private static readonly Dictionary<EExprToken, EditableConstantKind> Kinds = new()
        {
            [EExprToken.EX_True] = EditableConstantKind.Bool,
            [EExprToken.EX_False] = EditableConstantKind.Bool,
            [EExprToken.EX_IntConst] = EditableConstantKind.Int,
            [EExprToken.EX_IntZero] = EditableConstantKind.Int,
            [EExprToken.EX_IntOne] = EditableConstantKind.Int,
            [EExprToken.EX_Int64Const] = EditableConstantKind.Int64,
            [EExprToken.EX_UInt64Const] = EditableConstantKind.UInt64,
            [EExprToken.EX_FloatConst] = EditableConstantKind.Float,
            [EExprToken.EX_DoubleConst] = EditableConstantKind.Double,
            [EExprToken.EX_ByteConst] = EditableConstantKind.Byte,
            [EExprToken.EX_IntConstByte] = EditableConstantKind.Byte,
            [EExprToken.EX_NameConst] = EditableConstantKind.Name,
            [EExprToken.EX_StringConst] = EditableConstantKind.String,
            [EExprToken.EX_UnicodeStringConst] = EditableConstantKind.String,
        };

        public static bool IsEditable(KismetExpression expression) => Kinds.ContainsKey(expression.Token);

        public static bool TryGetKind(KismetExpression expression, out EditableConstantKind kind) => Kinds.TryGetValue(expression.Token, out kind);

        /// <summary>The raw value of the constant as edit text (enum pins may display a name instead).</summary>
        public static string FormatValue(KismetExpression expression)
        {
            var inv = CultureInfo.InvariantCulture;
            return expression switch
            {
                EX_True => "true",
                EX_False => "false",
                EX_IntConst o => o.Value.ToString(inv),
                EX_IntZero => "0",
                EX_IntOne => "1",
                EX_Int64Const o => o.Value.ToString(inv),
                EX_UInt64Const o => o.Value.ToString(inv),
                EX_FloatConst o => o.Value.ToString("R", inv),
                EX_DoubleConst o => o.Value.ToString("R", inv),
                EX_ByteConst o => o.Value.ToString(inv),
                EX_IntConstByte o => o.Value.ToString(inv),
                EX_NameConst o => o.Value.ToString(),
                EX_StringConst o => o.Value,
                EX_UnicodeStringConst o => o.Value,
                _ => string.Empty,
            };
        }

        /// <summary>Checks that the text is a valid value for the constant kind.</summary>
        public static bool IsValid(EditableConstantKind kind, string text)
        {
            text = text.Trim();
            var inv = CultureInfo.InvariantCulture;
            return kind switch
            {
                EditableConstantKind.Bool => bool.TryParse(text, out _),
                EditableConstantKind.Int => int.TryParse(text, NumberStyles.Integer, inv, out _),
                EditableConstantKind.Int64 => long.TryParse(text, NumberStyles.Integer, inv, out _),
                EditableConstantKind.UInt64 => ulong.TryParse(text, NumberStyles.Integer, inv, out _),
                EditableConstantKind.Float => float.TryParse(text, NumberStyles.Float, inv, out _),
                EditableConstantKind.Double => double.TryParse(text, NumberStyles.Float, inv, out _),
                EditableConstantKind.Byte => byte.TryParse(text, NumberStyles.Integer, inv, out _),
                EditableConstantKind.Name => text.Length > 0,
                EditableConstantKind.String => true,
                _ => false,
            };
        }
    }
}
