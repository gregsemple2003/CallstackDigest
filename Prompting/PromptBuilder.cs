using System.Collections.Generic;
using System.Text;

namespace CallstackAnnotator
{
    public static class PromptBuilder
    {
        public enum Mode { Explain, Optimize }

        // NEW: primary Build uses supplied template text
        public static string Build(
            Mode mode,
            string template,
            string rawCallstack,
            IReadOnlyList<(CallStackFrame Frame, string? Source)> framesWithSource)
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(template))
            {
                sb.AppendLine(template.TrimEnd());
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Input: Callstack");
            sb.AppendLine("```");
            sb.AppendLine(rawCallstack.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();

            sb.AppendLine("Input: Per-frame source code (best effort)");
            for (int i = 0; i < framesWithSource.Count; i++)
            {
                var (frame, src) = framesWithSource[i];
                sb.AppendLine();
                sb.AppendLine($"[Frame {frame.Index}] {frame.Module}!{frame.Symbol}");
                if (!string.IsNullOrWhiteSpace(frame.SourcePath))
                    sb.AppendLine($"File: {frame.SourcePath}:{frame.SourceLine}");
                sb.AppendLine("```cpp");
                if (!string.IsNullOrEmpty(src))
                    sb.AppendLine(src.TrimEnd());
                else
                    sb.AppendLine("// Source not found or could not detect function body.");
                sb.AppendLine("```");
            }

            return sb.ToString();
        }

        // Back-compat overload: uses persisted template for the selected mode
        public static string Build(
            Mode mode,
            string rawCallstack,
            IReadOnlyList<(CallStackFrame Frame, string? Source)> framesWithSource)
            => Build(mode, PromptTemplates.Get(mode), rawCallstack, framesWithSource);
    }
}

