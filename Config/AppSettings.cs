using System;
using System.IO;
using System.Text.Json;

namespace CallstackAnnotator
{
    public static class AppSettings
    {
        // Persistence backing (JSON in %APPDATA%\CallstackAnnotator\settings.json)
        private sealed class Data
        {
            public int FramesToAnnotateFromTop { get; set; } = 10;
            public int MaxSourceLinesPerFunction { get; set; } = 120;
        }

        private static readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CallstackAnnotator");
        private static readonly string FilePath = Path.Combine(AppDir, "settings.json");
        private static readonly object Gate = new();
        private static Data _data;

        static AppSettings()
        {
            _data = Load();
            _framesToAnnotateFromTop = Math.Max(0, _data.FramesToAnnotateFromTop);
            _maxSourceLinesPerFunction = Math.Max(1, _data.MaxSourceLinesPerFunction);
        }

        private static Data Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<Data>(json);
                    if (loaded != null) return loaded;
                }
            }
            catch { /* ignore */ }
            return new Data();
        }

        private static void Save()
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppDir);
                    _data.FramesToAnnotateFromTop = _framesToAnnotateFromTop;
                    _data.MaxSourceLinesPerFunction = _maxSourceLinesPerFunction;
                    var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(FilePath, json);
                }
            }
            catch { /* swallow to avoid breaking the app */ }
        }

        /// <summary>
        /// Number of frames from the TOP (most recent) of the callstack that will include source in the Prompt tab.
        /// Frames after this are not emitted in the prompt to minimize size.
        /// </summary>
        private static int _framesToAnnotateFromTop = 10;
        public static int FramesToAnnotateFromTop
        {
            get => _framesToAnnotateFromTop;
            set
            {
                _framesToAnnotateFromTop = Math.Max(0, value);
                Save();
            }
        }

        /// <summary>
        /// Back-compat shim: previously we annotated from the bottom. Setting this will now control
        /// the top count to preserve existing config files or CLI overrides.
        /// </summary>
        [System.Obsolete("Use FramesToAnnotateFromTop. This now maps to the top count.")]
        public static int FramesToAnnotateFromBottom
        {
            get => FramesToAnnotateFromTop;
            set => FramesToAnnotateFromTop = Math.Max(0, value);
        }

        /// <summary>
        /// Maximum number of lines included for a single function annotation.
        /// If the extracted function has more lines, we collapse to a centered window
        /// around the current (==>) line whose total height is ≤ this value.
        /// </summary>
        private static int _maxSourceLinesPerFunction = 120;
        public static int MaxSourceLinesPerFunction
        {
            get => _maxSourceLinesPerFunction;
            set
            {
                _maxSourceLinesPerFunction = Math.Max(1, value);
                Save();
            }
        }

        /// <summary>
        /// Marker used to indicate the frame's current executing source line.
        /// </summary>
        public const string CurrentLineMarker = "==>";
    }
}
