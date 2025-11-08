using System;

namespace CallstackAnnotator
{
    public static class AppSettings
    {
        /// <summary>
        /// Number of frames from the TOP (most recent) of the callstack that will include source in the Prompt tab.
        /// Frames after this are not emitted in the prompt to minimize size.
        /// </summary>
        public static int FramesToAnnotateFromTop { get; set; } = 10;

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
        public static int MaxSourceLinesPerFunction { get; set; } = 120;

        /// <summary>
        /// Marker used to indicate the frame's current executing source line.
        /// </summary>
        public const string CurrentLineMarker = "==>";
    }
}
