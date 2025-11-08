using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace CallstackDigest
{
    public static class CallStackParser
    {
        // Example lines:
        // UnrealEditor-IrisCore.dll!UE::CoreUObject::Private::ResolveObjectHandleNoRead(...) Line 424
        //     at C:\UnrealEngine2\Engine\Source\Runtime\CoreUObject\Public\UObject\ObjectHandle.h(424)
        // [Inline Frame] UnrealEditor-Mover.dll!operator<<(FArchive &) Line 1777
        // kernel32.dll!00007ff919167374()

        private static readonly Regex FrameLine = new Regex(
            @"^\s*(?:\[(?<inline>Inline Frame)\]\s*)?(?<module>[^!\s]+)!(?<symbol>.+?)(?:\s+Line\s+(?<line>\d+))?\s*$",
            RegexOptions.Compiled);

        private static readonly Regex AtLine = new Regex(
            @"^\s*at\s+(?<path>.+)\((?<line>\d+)\)\s*$",
            RegexOptions.Compiled);

        public static List<CallStackFrame> Parse(string clipboardText)
        {
            var frames = new List<CallStackFrame>();
            if (string.IsNullOrWhiteSpace(clipboardText)) return frames;

            var lines = clipboardText.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var m = FrameLine.Match(lines[i]);
                if (!m.Success) continue;

                bool isInline = !string.IsNullOrEmpty(m.Groups["inline"].Value);
                string module = m.Groups["module"].Value.Trim();
                string symbol = m.Groups["symbol"].Value.Trim();
                int? lineNo = int.TryParse(m.Groups["line"].Value, out var ln) ? ln : null;

                string? path = null;
                int? srcLine = null;

                if (i + 1 < lines.Length)
                {
                    var at = AtLine.Match(lines[i + 1]);
                    if (at.Success)
                    {
                        path = at.Groups["path"].Value.Trim();
                        srcLine = int.TryParse(at.Groups["line"].Value, out var l2) ? l2 : null;
                        i++; // consume the 'at' line
                    }
                }

                frames.Add(new CallStackFrame
                {
                    Index = frames.Count,
                    IsInline = isInline,
                    Module = module,
                    Symbol = symbol,
                    ReportedLine = lineNo,
                    SourcePath = path,
                    SourceLine = srcLine
                });
            }

            return frames;
        }
    }
}

