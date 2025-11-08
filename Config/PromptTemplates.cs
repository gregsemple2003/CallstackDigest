// File: Config\PromptTemplates.cs

using System;
using System.IO;
using System.Text.Json;

namespace CallstackAnnotator
{
    public static class PromptTemplates
    {
        private sealed class Data
        {
            public string Explain { get; set; } = DefaultExplain;
            public string Optimize { get; set; } = DefaultOptimize;
        }

        private static readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CallstackAnnotator");
        private static readonly string FilePath = Path.Combine(AppDir, "templates.json");
        private static readonly object Gate = new();

        private static Data _data = LoadInternal();

        // ---- Defaults (match prior hard-coded behavior) ----

        public const string DefaultExplain =
@"System: You are a senior engineer expert in Unreal Engine networking (Iris, NetworkPrediction), C++, and performance.

Task: Explain what is going on in this callstack.
For each frame: briefly explain the key points of the algorithm (1–3 bullets).
Then provide one concise paragraph summarizing the overall behavior.";

        public const string DefaultOptimize =
@"System: You are a senior engineer expert in Unreal Engine networking (Iris, NetworkPrediction), C++, and performance.

Task: Analyze this callstack for performance and reliability risks.
Identify hotspots (esp. serialization/replication), redundant work, and contention points.
Propose concrete improvements (quick wins and deeper changes) with trade-offs.";

        // ---- Public API ----

        public static string Get(PromptBuilder.Mode mode)
        {
            lock (Gate)
            {
                return mode == PromptBuilder.Mode.Explain ? _data.Explain : _data.Optimize;
            }
        }

        public static void Set(PromptBuilder.Mode mode, string template)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(template))
                    template = mode == PromptBuilder.Mode.Explain ? DefaultExplain : DefaultOptimize;

                if (mode == PromptBuilder.Mode.Explain) _data.Explain = template;
                else _data.Optimize = template;
                SaveInternal(_data);
            }
        }

        public static void ResetToDefault(PromptBuilder.Mode mode)
        {
            Set(mode, mode == PromptBuilder.Mode.Explain ? DefaultExplain : DefaultOptimize);
        }

        // ---- I/O ----

        private static Data LoadInternal()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<Data>(json);
                    if (loaded != null)
                    {
                        // fill missing with defaults
                        loaded.Explain ??= DefaultExplain;
                        loaded.Optimize ??= DefaultOptimize;
                        return loaded;
                    }
                }
            }
            catch { /* ignore and use defaults */ }

            return new Data();
        }

        private static void SaveInternal(Data data)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { /* swallow persistence errors to never break the app */ }
        }
    }
}
