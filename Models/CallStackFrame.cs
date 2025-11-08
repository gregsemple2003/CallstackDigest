using System.IO;
using System.Text;

namespace CallstackDigest
{
    public sealed class CallStackFrame
    {
        public int Index { get; init; }
        public bool IsInline { get; init; }
        public string Module { get; init; } = "";
        public string Symbol { get; init; } = "";
        public int? ReportedLine { get; init; }
        public string? SourcePath { get; set; } // Comes from the "at C:\...\file.cpp(line)" info
        public int? SourceLine { get; init; }

        public string ShortFunctionName => SymbolHelpers.ExtractFunctionName(Symbol);

        public override string ToString()
        {
            var loc = SourcePath != null && SourceLine.HasValue
                ? $"{Path.GetFileName(SourcePath)}:{SourceLine}"
                : "(no file)";
            var inline = IsInline ? "[Inline] " : "";
            return $"{Index:D2}  {inline}{Module}!{ShortFunctionName}  {loc}";
        }
    }

    public static class SymbolHelpers
    {
        // Extracts a display-friendly function name from a full symbol (handles operators & templates).
        public static string ExtractFunctionName(string symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return symbol;

            // Drop argument list to isolate name part
            string namePart = symbol;
            int paren = namePart.IndexOf('(');
            if (paren >= 0) namePart = namePart.Substring(0, paren);

            // Remove nested template args to simplify (::X<...>::Y)
            namePart = StripTemplates(namePart);

            // Function name is after the last "::"
            int lastScope = namePart.LastIndexOf("::", System.StringComparison.Ordinal);
            if (lastScope >= 0 && lastScope < namePart.Length - 2)
                namePart = namePart.Substring(lastScope + 2);

            return namePart.Trim();
        }

        public static string StripTemplates(string s)
        {
            var sb = new StringBuilder(s.Length);
            int depth = 0;
            foreach (char c in s)
            {
                if (c == '<') { depth++; continue; }
                if (c == '>') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}

