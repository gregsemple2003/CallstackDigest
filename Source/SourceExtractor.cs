using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CallstackAnnotator
{
    /// <summary>
    /// Extracts the source text of a function that appears in a call stack frame.
    /// Strategy (in order):
    ///  1) Anchor by symbol name (best). Parse signature, skip qualifiers/attributes/macros, then capture body {…} or expression-bodied (=> …;).
    ///  2) If anchoring fails, take the smallest enclosing balanced block around the target line whose header looks like a function (or accessor when applicable).
    ///  3) If all fails, return nearby lines as a fallback preview.
    /// </summary>
    public static class SourceExtractor
    {
        // ---------- Configuration ----------
        private const int AnchorSearchWindowChars = 20000; // ± window from target index for name-based anchoring
        private const int HeaderBacktrackMaxChars = 8000;  // how far to look back from '{' for the header
        private const int FallbackContextRadius = 24;       // lines in the "nearby" fallback

        // ---------- Caching ----------
        private sealed class FileBlob
        {
            public string Content = "";
            public string Cleaned = "";              // comments/strings replaced by spaces, newlines preserved
            public List<int> LineStarts = new();       // 0-based char index of each line start in Content
            public bool IsCSharp;
            public bool IsCppLike;
        }

        // Cache: path -> FileBlob (thread-safe)
        private static readonly ConcurrentDictionary<string, FileBlob> BlobCache =
            new(StringComparer.OrdinalIgnoreCase);

        // ---------- Public API ----------
        public static bool TryExtractFunctionSource(CallStackFrame frame, out string code, out string message)
        {
            code = "";
            message = "";

            if (string.IsNullOrWhiteSpace(frame.SourcePath) || !frame.SourceLine.HasValue)
            {
                message = "No file path / line info available for this frame.";
                return false;
            }

            string path = RemapPath(frame.SourcePath!);
            if (!File.Exists(path))
            {
                message = $"Source file not found: {path}";
                return false;
            }

            var blob = GetOrBuildBlob(path);

            int targetLine1Based = Math.Max(1, frame.SourceLine!.Value);
            int targetIdx = (targetLine1Based - 1) < blob.LineStarts.Count
                ? blob.LineStarts[targetLine1Based - 1]
                : blob.Content.Length > 0 ? blob.Content.Length - 1 : 0;

            string functionName = SymbolHelpers.ExtractFunctionName(frame.Symbol)?.Trim() ?? string.Empty;

            // 1) Best effort: anchor by symbol name
            if (!string.IsNullOrEmpty(functionName) &&
                TryExtractByNameAnchor(blob, functionName, targetIdx,
                    out int startIdx, out int endIdx, out int startLine, out int endLine))
            {
                code = StampCurrentLineAndClamp(blob, startIdx, endIdx, startLine, endLine, targetLine1Based);
                message = $"Lines {startLine}–{endLine}";
                return true;
            }

            // 2) Heuristic fallback: smallest enclosing balanced block that looks like a function (or accessor)
            if (TryExtractByEnclosingBlock(blob, targetIdx, functionName,
                    out int s2, out int e2, out int l2, out int r2))
            {
                code = StampCurrentLineAndClamp(blob, s2, e2, l2, r2, targetLine1Based);
                message = $"Lines {l2}–{r2}";
                return true;
            }

            // 3) As a last resort, provide nearby context (with ==> marker)
            message = "Could not confidently locate function body. Showing nearby lines.";
            code = ExtractNearby(blob.Content, targetLine1Based, FallbackContextRadius);
            return false;
        }

        // OPTIONAL: remap root if your build path differs from the callstack path
        // Example: return path.Replace(@"C:\UnrealEngine2\", @"D:\UE5\");
        private static string RemapPath(string path) => path; // <— customize if needed

        // ---------- Blob building ----------
        private static FileBlob GetOrBuildBlob(string path)
        {
            if (BlobCache.TryGetValue(path, out var cached))
                return cached;

            string content = File.ReadAllText(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            bool isCSharp = ext == ".cs";
            bool isCppLike = ext is ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" or ".inl";

            string cleaned = StripCommentsAndStringsAdvanced(content);
            var lineStarts = ComputeLineStarts(content);

            var blob = new FileBlob
            {
                Content = content,
                Cleaned = cleaned,
                LineStarts = lineStarts,
                IsCSharp = isCSharp,
                IsCppLike = isCppLike
            };
            BlobCache[path] = blob;
            return blob;
        }

        // ---------- Strategy 1: Anchor by name ----------
        private static bool TryExtractByNameAnchor(
            FileBlob blob,
            string functionName,
            int targetIdx,
            out int startIdx, out int endIdx, out int startLine, out int endLine)
        {
            startIdx = endIdx = startLine = endLine = 0;

            // Search a window around the target for occurrences of the name
            int leftBound = Math.Max(0, targetIdx - AnchorSearchWindowChars);
            int rightBound = Math.Min(blob.Cleaned.Length - 1, targetIdx + AnchorSearchWindowChars);

            // Try nearest occurrence before the target first; if that fails, try after
            foreach (var idx in EnumerateNameHits(blob.Cleaned, functionName, leftBound, rightBound, targetIdx))
            {
                if (TryFormFunctionFromNameIndex(blob, functionName, idx, targetIdx,
                        out startIdx, out endIdx, out startLine, out endLine))
                    return true;
            }

            return false;
        }

        private static IEnumerable<int> EnumerateNameHits(string cleaned, string name, int left, int right, int pivot)
        {
            // 1) last before pivot
            int pos = Math.Min(pivot, cleaned.Length - 1);
            while (pos >= left)
            {
                int hit = cleaned.LastIndexOf(name, pos, pos - left + 1, StringComparison.Ordinal);
                if (hit < 0) break;
                yield return hit;
                pos = hit - 1;
                // Limit the number of candidates to keep this fast
                if (pos < pivot - 6 * 1024) break;
            }

            // 2) first after pivot
            pos = pivot + 1;
            while (pos <= right)
            {
                int hit = cleaned.IndexOf(name, pos, right - pos + 1, StringComparison.Ordinal);
                if (hit < 0) break;
                yield return hit;
                pos = hit + name.Length;
                if (pos > pivot + 6 * 1024) break;
            }
        }

        private static bool TryFormFunctionFromNameIndex(
            FileBlob blob,
            string functionName,
            int nameIdx,
            int targetIdx,
            out int startIdx, out int endIdx, out int startLine, out int endLine)
        {
            startIdx = endIdx = startLine = endLine = 0;

            // Accept: Foo<T>(...), Foo(...), operator<<(...) etc.
            // After name, optional generic angle args, then '(' params ')', then post-signature qualifiers/attrs, then either:
            //   - '{' … '}' (body)
            //   - '=>' … ';' (expression-bodied)
            //   - ';' (declaration-only) -> reject
            int i = nameIdx + functionName.Length;
            var s = blob.Cleaned;

            // Skip whitespace
            i = SkipWhitespace(s, i);

            // Optional generic args immediately after name, e.g. Foo<T,U>
            if (i < s.Length && s[i] == '<')
            {
                int gt = FindMatchingAngles(s, i);
                if (gt < 0) return false;
                i = gt + 1;
                i = SkipWhitespace(s, i);
            }

            // Must see '(' for a function-like thing; accessors won't be hit here (handled elsewhere)
            if (i >= s.Length || s[i] != '(') return false;

            int openParen = i;
            int closeParen = FindMatchingParen(s, openParen);
            if (closeParen < 0) return false;

            // After ')': qualifiers, attributes, trailing return, constructor initializer, constraints, etc.
            int j = closeParen + 1;

            // Allow C# generic constraints "where T : ..." or C++ trailing return "-> type", "noexcept", "requires", attributes, ctor ":" initializer
            if (!SkipPostSignatureDecorations(s, ref j))
                return false; // saw a ';' meaning it's likely a declaration only

            // At this point we expect one of: '{' … '}', expression-bodied '=> … ;', or (rare) macro that immediately opens '{'
            // Expression-bodied methods (C#)
            if (j + 1 < s.Length && s[j] == '=' && s[j + 1] == '>')
            {
                int arrow = j;
                int semi = s.IndexOf(';', arrow + 2);
                if (semi < 0) return false;

                // Compute signature start line by walking upward lines
                int signatureStart = FindSignatureStart(blob.Content, nameIdx);
                var lineStarts = blob.LineStarts;
                startIdx = signatureStart;
                endIdx = Math.Min(blob.Content.Length, semi + 1);
                startLine = IndexToLine(lineStarts, startIdx);
                endLine = IndexToLine(lineStarts, endIdx - 1);
                // Ensure target lies within or very near (same or one of adjacent lines)
                int targetLine = IndexToLine(lineStarts, targetIdx);
                if (targetLine >= startLine - 1 && targetLine <= endLine + 1)
                    return true;

                return false;
            }

            // Regular body '{' … '}'
            if (j < s.Length && s[j] == '{')
            {
                int openBrace = j;
                if (!TryFindMatchingBrace(s, openBrace, out int closeBrace))
                    return false;

                int sigStart = FindSignatureStart(blob.Content, nameIdx);
                var lineStarts = blob.LineStarts;

                startIdx = sigStart;
                endIdx = Math.Min(blob.Content.Length, closeBrace + 1);
                startLine = IndexToLine(lineStarts, startIdx);
                endLine = IndexToLine(lineStarts, endIdx - 1);

                // Prefer blocks that actually cover the target
                int targetLine = IndexToLine(lineStarts, targetIdx);
                if (targetLine >= startLine && targetLine <= endLine)
                    return true;

                // If not covering, still accept if it's the closest match around the target region
                // (e.g., stack points to a logging line after the method)
                int sigDist = Math.Abs(targetIdx - nameIdx);
                if (sigDist < 4096) return true;

                return false;
            }

            return false;
        }

        // Skip decorations between close paren and the body or terminator.
        // Advances 'i' to the first significant token following the signature.
        // Returns false if a ';' (declaration-only) is encountered before a body/arrow.
        private static bool SkipPostSignatureDecorations(string s, ref int i)
        {
            while (i < s.Length)
            {
                i = SkipWhitespace(s, i);
                if (i >= s.Length) break;

                // Body
                if (s[i] == '{') return true;

                // Expression-bodied
                if (s[i] == '=' && i + 1 < s.Length && s[i + 1] == '>')
                    return true;

                // Declaration-only
                if (s[i] == ';') return false;

                // C++ ctor initializer list: ':' Base(args) , member{init} …
                if (s[i] == ':') { i++; continue; }

                // Trailing return: '-> type'
                if (s[i] == '-' && i + 1 < s.Length && s[i + 1] == '>')
                {
                    i += 2;
                    // Skip a type (identifiers, templates, pointers/references, qualifiers)
                    while (i < s.Length)
                    {
                        if (s[i] == '<') { int gt = FindMatchingAngles(s, i); i = gt >= 0 ? gt + 1 : s.Length; continue; }
                        if (s[i] == '(') { int cp = FindMatchingParen(s, i); i = cp >= 0 ? cp + 1 : s.Length; continue; }
                        if ("*&:.[]".IndexOf(s[i]) >= 0 || char.IsLetterOrDigit(s[i]) || s[i] == '_' || char.IsWhiteSpace(s[i]))
                        { i++; continue; }
                        break;
                    }
                    continue;
                }

                // Attributes (C++): [[...]]
                if (s[i] == '[' && i + 1 < s.Length && s[i + 1] == '[')
                {
                    int end = s.IndexOf("]]", i + 2, StringComparison.Ordinal);
                    i = end >= 0 ? end + 2 : s.Length;
                    continue;
                }

                // C# attributes on same line: [Attr(...)]
                if (s[i] == '[')
                {
                    int end = FindMatchingBracket(s, i);
                    i = end >= 0 ? end + 1 : s.Length;
                    continue;
                }

                // 'noexcept ( … )', 'requires …', 'where …', 'override', 'final', 'const', 'volatile', 'mutable', 'extern', 'static', 'async', 'unsafe', UE_*(...), __declspec(...)
                if (char.IsLetter(s[i]) || s[i] == '_' )
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    string word = s.Substring(start, i - start);

                    // If macro-like and followed by '(', skip its argument list
                    i = SkipWhitespace(s, i);
                    if (i < s.Length && s[i] == '(')
                    {
                        int cp = FindMatchingParen(s, i);
                        i = cp >= 0 ? cp + 1 : s.Length;
                        continue;
                    }

                    // Otherwise continue scanning words/qualifiers
                    continue;
                }

                // Generic constraints or templates
                if (s[i] == '<') { int gt = FindMatchingAngles(s, i); i = gt >= 0 ? gt + 1 : s.Length; continue; }

                // Parenthesized trailing specifiers (e.g., noexcept(expr))
                if (s[i] == '(') { int cp = FindMatchingParen(s, i); i = cp >= 0 ? cp + 1 : s.Length; continue; }

                // Unknown token: advance cautiously
                i++;
            }

            return true;
        }

        // ---------- Strategy 2: Enclosing balanced block ----------
        private static bool TryExtractByEnclosingBlock(
            FileBlob blob,
            int targetIdx,
            string functionName,
            out int startIdx, out int endIdx, out int startLine, out int endLine)
        {
            startIdx = endIdx = startLine = endLine = 0;

            var s = blob.Cleaned;

            // Walk backwards to find the *closest* '{' whose matching '}' encloses target.
            int search = Math.Min(targetIdx, s.Length - 1);
            while (search >= 0)
            {
                int open = s.LastIndexOf('{', search);
                if (open < 0) break;

                if (!TryFindMatchingBrace(s, open, out int close))
                {
                    search = open - 1;
                    continue;
                }

                // Does target lie in this block?
                if (open <= targetIdx && targetIdx <= close)
                {
                    // Get the header text preceding this '{'
                    var header = ExtractHeader(s, open, HeaderBacktrackMaxChars);

                    // Decide if this header looks like a real function or an accessor.
                    if (LooksLikeFunctionHeader(header, functionName) ||
                        LooksLikeAccessorHeader(header, functionName))
                    {
                        int sigStartIdx = FindSignatureStart(blob.Content, open);
                        var lineStarts = blob.LineStarts;

                        startIdx = sigStartIdx;
                        endIdx = Math.Min(blob.Content.Length, close + 1);
                        startLine = IndexToLine(lineStarts, startIdx);
                        endLine = IndexToLine(lineStarts, endIdx - 1);
                        return true;
                    }
                }

                search = open - 1;
            }

            // Special case: C# expression-bodied member where target is on or near that line (no braces).
            if (blob.IsCSharp && TryFindExpressionBodiedAround(blob, targetIdx,
                    out startIdx, out endIdx, out startLine, out endLine))
            {
                return true;
            }

            return false;
        }

        private static bool LooksLikeFunctionHeader(string header, string functionName)
        {
            if (string.IsNullOrWhiteSpace(header)) return false;

            // Must have '(' to be function-like (accessors handled separately)
            if (!header.Contains('(')) return false;

            // Avoid obvious control statements or type blocks
            if (Regex.IsMatch(header, @"\b(if|for|while|switch|catch|else|do|try)\s*\(",
                    RegexOptions.CultureInvariant))
                return false;
            if (Regex.IsMatch(header, @"\b(class|struct|namespace|enum|union)\b",
                    RegexOptions.CultureInvariant))
                return false;

            // Avoid typical lambda headers
            if (header.Contains("](")) return false; // C++ lambda capture '[](...)'

            // Strong hint: function name appears near the header (operator() still OK)
            var fn = functionName?.Trim();
            if (!string.IsNullOrEmpty(fn))
            {
                if (fn.StartsWith("operator", StringComparison.Ordinal))
                    return header.Contains("operator", StringComparison.Ordinal);
                return header.Contains(fn, StringComparison.Ordinal);
            }

            return true;
        }

        private static bool LooksLikeAccessorHeader(string header, string functionName)
        {
            // C# property/event accessors have no '(' in their header: "{ get; set; }" or "{ get { ... } }"
            // Accept these only if the symbol name suggests get_/set_/add_/remove_
            if (string.IsNullOrEmpty(functionName)) return false;

            bool wantsAccessor =
                functionName.StartsWith("get_", StringComparison.Ordinal) ||
                functionName.StartsWith("set_", StringComparison.Ordinal) ||
                functionName.StartsWith("add_", StringComparison.Ordinal) ||
                functionName.StartsWith("remove_", StringComparison.Ordinal) ||
                functionName.Equals("get", StringComparison.Ordinal) ||
                functionName.Equals("set", StringComparison.Ordinal) ||
                functionName.Equals("add", StringComparison.Ordinal) ||
                functionName.Equals("remove", StringComparison.Ordinal);

            if (!wantsAccessor) return false;

            var trimmed = header.TrimEnd();
            if (trimmed.EndsWith("get", StringComparison.Ordinal) ||
                trimmed.EndsWith("set", StringComparison.Ordinal) ||
                trimmed.EndsWith("add", StringComparison.Ordinal) ||
                trimmed.EndsWith("remove", StringComparison.Ordinal))
            {
                return true;
            }
            return false;
        }

        private static string ExtractHeader(string cleaned, int braceIndex, int maxBackChars)
        {
            int start = Math.Max(0, braceIndex - maxBackChars);
            string segment = cleaned.Substring(start, braceIndex - start);
            var lines = segment.Split('\n');
            int take = Math.Max(1, Math.Min(lines.Length, 30));
            return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - take)));
        }

        private static int FindSignatureStart(string content, int anchorIdx)
        {
            // Walk upwards from the line containing 'anchorIdx' (method name or '{')
            // Include attributes, template/macros, multi-line signatures, and qualifier-only lines
            var lineStarts = ComputeLineStarts(content);
            var lines = SplitLines(content);

            int line = IndexToLine(lineStarts, anchorIdx); // 1-based
            int startLine = line;

            while (startLine > 1)
            {
                string prev = lines[startLine - 2].Trim();

                if (string.IsNullOrEmpty(prev)) break;
                if (prev.StartsWith("#")) break; // preprocessor boundary
                if (prev.EndsWith(";")) break;   // previous statement ended

                // C# attributes, C++ attributes, UE macros, template lines, multi-line parameter list,
                // qualifier-only lines (const/noexcept/override/final/volatile/where/requires)
                if (prev.StartsWith("[") || prev.Contains("template<") || prev.Contains("UE_") || prev.Contains("__declspec"))
                {
                    startLine--;
                    continue;
                }

                if (prev.Contains("(") || prev.EndsWith(","))
                {
                    startLine--;
                    continue;
                }

                if (Regex.IsMatch(prev, @"\b(const|noexcept|override|final|requires|volatile|where|async|unsafe|extern)\b$",
                        RegexOptions.CultureInvariant))
                {
                    startLine--;
                    continue;
                }

                // constructor initializer list continued on previous line (C++)
                if (prev.EndsWith(":") || prev.Contains(" : "))
                {
                    startLine--;
                    continue;
                }

                break;
            }

            return lineStarts[startLine - 1];
        }

        // ---------- Special-case: expression-bodied (C#) ----------
        private static bool TryFindExpressionBodiedAround(
            FileBlob blob,
            int targetIdx,
            out int startIdx, out int endIdx, out int startLine, out int endLine)
        {
            startIdx = endIdx = startLine = endLine = 0;
            if (!blob.IsCSharp) return false;

            // Check the current line and a few lines above (to handle wrapped signatures)
            int targetLine = IndexToLine(blob.LineStarts, targetIdx);
            var lines = SplitLines(blob.Content);

            int minLine = Math.Max(1, targetLine - 5);
            for (int ln = targetLine; ln >= minLine; ln--)
            {
                int lineStart = blob.LineStarts[ln - 1];
                int lineEnd = (ln < blob.LineStarts.Count) ? blob.LineStarts[ln] - 1 : blob.Content.Length - 1;

                string slice = blob.Content.Substring(lineStart, lineEnd - lineStart + 1);

                int arrow = slice.IndexOf("=>", StringComparison.Ordinal);
                if (arrow < 0) continue;

                // Find terminating ';' after '=>'
                int semiGlobal = blob.Content.IndexOf(';', lineStart + arrow + 2);
                if (semiGlobal < 0) continue;

                // Find a plausible signature start above
                int sigStart = FindSignatureStart(blob.Content, lineStart + arrow);
                startIdx = sigStart;
                endIdx = Math.Min(blob.Content.Length, semiGlobal + 1);
                startLine = IndexToLine(blob.LineStarts, startIdx);
                endLine = IndexToLine(blob.LineStarts, endIdx - 1);
                return true;
            }

            return false;
        }

        // ---------- Utilities: nearby fallback ----------
        private static string ExtractNearby(string content, int line, int radius)
        {
            var lines = SplitLines(content);
            int L = Math.Max(1, line - radius);
            int R = Math.Min(lines.Count, line + radius);
            var sb = new StringBuilder();
            for (int i = L; i <= R; i++)
            {
                string marker = (i == line) ? (AppSettings.CurrentLineMarker + " ") : "";
                sb.AppendLine($"{marker}{i,6}: {lines[i - 1]}");
            }
            return sb.ToString();
        }

        // ---------- Render annotated range with marker and cropping ----------
        private static string RenderAnnotatedRange(FileBlob blob, int startLine, int endLine, int highlightLine, out bool cropped)
        {
            cropped = false;

            // Clamp
            startLine = Math.Max(1, startLine);
            endLine   = Math.Max(startLine, endLine);

            // If highlight is outside the range, clamp it to the nearest line so we still show a sensible marker
            int hl = Math.Max(startLine, Math.Min(endLine, highlightLine));

            // Decide whether to crop
            int total = endLine - startLine + 1;
            int max = Math.Max(1, AppSettings.MaxSourceLinesPerFunction);

            int first = startLine;
            int last  = endLine;

            if (total > max)
            {
                cropped = true;
                int window = max;                // total lines in the window (≤ max)
                int half = window / 2;

                first = Math.Max(startLine, hl - half);
                last  = first + window - 1;
                if (last > endLine)
                {
                    last = endLine;
                    first = Math.Max(startLine, last - window + 1);
                }
            }

            var lines = SplitLines(blob.Content);
            var sb = new StringBuilder();

            if (first > startLine) sb.AppendLine("// ... source omitted (above)");
            for (int ln = first; ln <= last; ln++)
            {
                string marker = (ln == hl) ? AppSettings.CurrentLineMarker : "   ";
                sb.AppendLine($"{marker} {ln,6}: {lines[ln - 1]}");
            }
            if (last < endLine) sb.AppendLine("// ... source omitted (below)");

            return sb.ToString();
        }

        // ---------- Text helpers ----------
        private static List<int> ComputeLineStarts(string s)
        {
            var starts = new List<int>(capacity: Math.Max(16, s.Length / 32)) { 0 };
            for (int i = 0; i < s.Length; i++)
                if (s[i] == '\n') starts.Add(i + 1);
            return starts;
        }

        private static int IndexToLine(List<int> lineStarts, int index)
        {
            // Binary search over lineStarts to compute 1-based line number
            int lo = 0, hi = lineStarts.Count - 1, ans = 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (lineStarts[mid] <= index) { ans = mid + 1; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }

        private static List<string> SplitLines(string content)
        {
            return content.Replace("\r\n", "\n").Split('\n').ToList();
        }

        private static int SkipWhitespace(string s, int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            return i;
        }

        // ---------- Balanced matcher helpers over the CLEANED string ----------
        private static bool TryFindMatchingBrace(string s, int openIdx, out int closeIdx)
        {
            int depth = 1;
            for (int i = openIdx + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { closeIdx = i; return true; }
                }
            }
            closeIdx = -1;
            return false;
        }

        private static int FindMatchingParen(string s, int openIdx)
        {
            int depth = 1;
            for (int i = openIdx + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingAngles(string s, int openIdx)
        {
            // Rough angle matching (sufficient for generics/templates on cleaned code)
            int depth = 1;
            for (int i = openIdx + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') depth++;
                else if (c == '>')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static int FindMatchingBracket(string s, int openIdx)
        {
            int depth = 1;
            for (int i = openIdx + 1; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        // ---------- Cleaning: strip comments & strings while preserving layout ----------
        // Replaces chars inside comments/strings with spaces; keeps newlines intact so indices/lines map to original content.
        private static string StripCommentsAndStringsAdvanced(string s)
        {
            var sb = new StringBuilder(s.Length);
            int i = 0;

            void AppendSpace(int count = 1)
            {
                for (int k = 0; k < count; k++) sb.Append(' ');
            }

            while (i < s.Length)
            {
                char c = s[i];
                char n = (i + 1 < s.Length) ? s[i + 1] : '\0';

                // line comment //
                if (c == '/' && n == '/')
                {
                    sb.Append(' '); sb.Append(' ');
                    i += 2;
                    while (i < s.Length && s[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }

                // block comment /* ... */
                if (c == '/' && n == '*')
                {
                    sb.Append(' '); sb.Append(' ');
                    i += 2;
                    while (i < s.Length)
                    {
                        if (s[i] == '\n') sb.Append('\n');
                        else sb.Append(' ');
                        if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '/')
                        {
                            sb.Append(' '); sb.Append(' ');
                            i += 2; // consume closing */
                        break;
                        }
                        else i++;
                    }
                    continue;
                }

                // C# verbatim string @"..."
                if (c == '@' && n == '"')
                {
                    sb.Append(' '); sb.Append(' ');
                    i += 2;
                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == '"' )
                        {
                            // doubled "" inside verbatim string
                            if (i + 1 < s.Length && s[i + 1] == '"')
                            {
                                AppendSpace(2);
                                i += 2;
                                continue;
                            }
                            sb.Append(' ');
                            i++;
                        break;
                        }
                        if (ch == '\n') sb.Append('\n'); else sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                // C# raw string """...""" (3+ quotes)
                if (c == '"' && i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    // count opening quotes
                    int q = i;
                    while (q < s.Length && s[q] == '"') q++;
                    int openCount = q - i;

                    for (int k = 0; k < openCount; k++) sb.Append(' ');
                    i += openCount;

                    // find closing run of openCount quotes
                    while (i < s.Length)
                    {
                        if (s[i] == '\n') sb.Append('\n'); else sb.Append(' ');
                        // detect closing run
                        if (s[i] == '"')
                        {
                            int p = i;
                            while (p < s.Length && s[p] == '"') p++;
                            int run = p - i;
                            if (run >= openCount)
                            {
                                for (int k = 0; k < openCount; k++) sb.Append(' ');
                                i = i + openCount;
                        break;
                            }
                        }
                        i++;
                    }
                    continue;
                }

                // C++ raw string: R"delim( ... )delim"
                if (c == 'R' && n == '"')
                {
                    // Parse delimiter
                    int delimStart = i + 2;
                    int paren = s.IndexOf('(', delimStart);
                    if (paren > delimStart)
                    {
                        string delim = s.Substring(delimStart, paren - delimStart);
                        // emit spaces for R"delim(
                        sb.Append(' '); sb.Append(' ');
                        for (int k = delimStart; k <= paren; k++) sb.Append(' ');
                        i = paren + 1;

                        // find closing )delim"
                        string closer = ")" + delim + "\"";
                        int closeAt = s.IndexOf(closer, i, StringComparison.Ordinal);
                        while (i < s.Length && (closeAt < 0 || i < closeAt))
                        {
                            if (i < s.Length && s[i] == '\n') sb.Append('\n'); else sb.Append(' ');
                            i++;
                        }
                        if (closeAt >= 0)
                        {
                            for (int k = 0; k < closer.Length; k++) sb.Append(' ');
                            i = closeAt + closer.Length;
                        }
                        continue;
                    }
                }

                // normal string "..." (C/C#/C++)
                if (c == '"')
                {
                    sb.Append(' ');
                    i++;
                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == '\\') { sb.Append(' '); i += Math.Min(2, s.Length - i); continue; }
                        if (ch == '"') { sb.Append(' '); i++; break; }
                        if (ch == '\n') sb.Append('\n'); else sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                // char literal 'x' or multi-char 'ab' in C/C++
                if (c == '\'')
                {
                    sb.Append(' ');
                    i++;
                    while (i < s.Length)
                    {
                        char ch = s[i];
                        if (ch == '\\') { sb.Append(' '); i += Math.Min(2, s.Length - i); continue; }
                        if (ch == '\'') { sb.Append(' '); i++; break; }
                        if (ch == '\n') sb.Append('\n'); else sb.Append(' ');
                        i++;
                    }
                    continue;
                }

                // passthrough
                sb.Append(c);
                i++;
            }

            return sb.ToString();
        }

        // ---------- END ----------

        /// <summary>
        /// Adds the current-line marker to the slice [startIdx,endIdx) and clamps to a centered window
        /// whose height ≤ MaxSourceLinesPerFunction while ensuring the marker line is included.
        /// </summary>
        private static string StampCurrentLineAndClamp(
            FileBlob blob, int startIdx, int endIdx, int startLine, int endLine, int targetLine1Based)
        {
            string segment = blob.Content.Substring(startIdx, endIdx - startIdx);
            var rows = segment.Replace("\r\n", "\n").Split('\n').ToList();
            int total = rows.Count;

            // Compute the line within the segment to mark (1-based)
            int rel = Math.Clamp(targetLine1Based - startLine + 1, 1, total);
            rows[rel - 1] = $"{AppSettings.CurrentLineMarker} {rows[rel - 1]}";

            int maxLines = Math.Max(0, AppSettings.MaxSourceLinesPerFunction);
            if (maxLines > 0 && total > maxLines)
            {
                int half = maxLines / 2;
                int begin = Math.Max(1, rel - half);
                int end = Math.Min(total, begin + maxLines - 1);
                begin = Math.Max(1, end - maxLines + 1); // re-adjust if near the tail
                rows = rows.Skip(begin - 1).Take(end - begin + 1).ToList();
            }

            return string.Join("\n", rows);
        }
    }
}
