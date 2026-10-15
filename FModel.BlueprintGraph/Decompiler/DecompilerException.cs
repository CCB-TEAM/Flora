using System;

namespace FModel.BlueprintGraph.Decompiler
{
    internal class DecompilerException : Exception
    {
        public new string Message;
        public DecompilerContext? Context;

        public DecompilerException(string message, DecompilerContext? context = null)
        {
            Message = message;
            Context = context;
        }
    }
}
