using Microsoft.Win32;
using System;
using System.Windows;

namespace CallstackDigest
{
    internal static class WinStateStoreWpf
    {
        private const string RegPath = @"Software\CallstackDigest";

        public static void Load(Window window)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                if (key == null) return;

                int x = key.GetValue("X") is int vx ? vx : int.MinValue;
                int y = key.GetValue("Y") is int vy ? vy : int.MinValue;
                int w = key.GetValue("W") is int vw ? vw : int.MinValue;
                int h = key.GetValue("H") is int vh ? vh : int.MinValue;
                var stateStr = key.GetValue("WindowState") as string;

                if (w > 100 && h > 100 && x != int.MinValue && y != int.MinValue)
                {
                    var rect = new Rect(x, y, w, h);

                    var vs = new Rect(SystemParameters.VirtualScreenLeft,
                                      SystemParameters.VirtualScreenTop,
                                      SystemParameters.VirtualScreenWidth,
                                      SystemParameters.VirtualScreenHeight);

                    if (rect.IntersectsWith(vs))
                    {
                        window.WindowStartupLocation = WindowStartupLocation.Manual;
                        window.Left = rect.X;
                        window.Top = rect.Y;
                        window.Width = rect.Width;
                        window.Height = rect.Height;
                    }
                }

                if (Enum.TryParse(stateStr, out WindowState ws))
                    window.WindowState = ws;
            }
            catch { /* swallow */ }
        }

        public static void Save(Window window)
        {
            try
            {
                var state = window.WindowState;
                // Use RestoreBounds when maximized/minimized to save normal geometry.
                var rect = (state == WindowState.Normal) ? new Rect(window.Left, window.Top, window.Width, window.Height)
                                                         : window.RestoreBounds;

                using var key = Registry.CurrentUser.CreateSubKey(RegPath);
                key?.SetValue("X", (int)rect.X);
                key?.SetValue("Y", (int)rect.Y);
                key?.SetValue("W", (int)Math.Max(0, rect.Width));
                key?.SetValue("H", (int)Math.Max(0, rect.Height));
                key?.SetValue("WindowState", state.ToString());
            }
            catch { /* swallow */ }
        }
    }
}

