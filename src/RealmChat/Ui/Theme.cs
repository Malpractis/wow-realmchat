using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RealmChat
{
    public enum ThemeMode { Auto, Light, Dark }

    // One theme's design tokens. Every color the UI paints comes from here so
    // light and dark stay in lockstep; no form or control hardcodes a color.
    public sealed class Palette
    {
        public bool Dark;
        public Color Background;     // form surface
        public Color Surface;       // inputs, log, cards
        public Color Text;
        public Color TextMuted;     // captions, secondary copy (>= 4.5:1 on Background)
        public Color Border;
        public Color Accent;        // primary action
        public Color AccentHover;
        public Color AccentPressed;
        public Color AccentText;
        public Color ButtonFace;    // secondary buttons
        public Color ButtonHover;
        public Color ButtonPressed;
        public Color Link;
        public Color Success;
        public Color Warning;
        public Color Danger;
    }

    // App-wide theme state: a resolved Palette plus the user's chosen mode
    // (Auto follows the Windows "app mode" setting live). Forms subscribe to
    // Changed and restyle in place - no restart required.
    public static class Theme
    {
        public static readonly Palette Light = new Palette
        {
            Dark = false,
            Background = Color.FromArgb(0xF4, 0xF5, 0xF7),
            Surface = Color.White,
            Text = Color.FromArgb(0x1B, 0x1F, 0x27),
            TextMuted = Color.FromArgb(0x5B, 0x64, 0x72),
            Border = Color.FromArgb(0xD4, 0xD9, 0xE0),
            Accent = Color.FromArgb(0x25, 0x63, 0xEB),
            AccentHover = Color.FromArgb(0x1E, 0x56, 0xD6),
            AccentPressed = Color.FromArgb(0x1A, 0x4C, 0xBF),
            AccentText = Color.White,
            ButtonFace = Color.White,
            ButtonHover = Color.FromArgb(0xEF, 0xF1, 0xF4),
            ButtonPressed = Color.FromArgb(0xE3, 0xE7, 0xEC),
            Link = Color.FromArgb(0x1D, 0x4E, 0xD8),
            Success = Color.FromArgb(0x15, 0x80, 0x3D),
            Warning = Color.FromArgb(0xB4, 0x53, 0x09),
            Danger = Color.FromArgb(0xB9, 0x1C, 0x1C),
        };

        public static readonly Palette Dark = new Palette
        {
            Dark = true,
            Background = Color.FromArgb(0x1F, 0x21, 0x27),
            Surface = Color.FromArgb(0x2A, 0x2D, 0x35),
            Text = Color.FromArgb(0xE9, 0xEB, 0xEF),
            TextMuted = Color.FromArgb(0xA2, 0xA9, 0xB4),
            Border = Color.FromArgb(0x3C, 0x40, 0x49),
            Accent = Color.FromArgb(0x3E, 0x7B, 0xFA),
            AccentHover = Color.FromArgb(0x5B, 0x8F, 0xFB),
            AccentPressed = Color.FromArgb(0x2F, 0x67, 0xDD),
            AccentText = Color.White,
            ButtonFace = Color.FromArgb(0x2E, 0x32, 0x3B),
            ButtonHover = Color.FromArgb(0x38, 0x3D, 0x48),
            ButtonPressed = Color.FromArgb(0x26, 0x2A, 0x32),
            Link = Color.FromArgb(0x7D, 0xA7, 0xFF),
            Success = Color.FromArgb(0x4C, 0xC3, 0x8A),
            Warning = Color.FromArgb(0xE5, 0xA9, 0x3D),
            Danger = Color.FromArgb(0xF0, 0x71, 0x6A),
        };

        public static ThemeMode Mode { get; private set; }
        public static Palette Current { get; private set; }
        public static event Action Changed;

        static Theme()
        {
            Mode = ThemeMode.Auto;
            Current = Resolve();
        }

        // Called once at startup with the persisted config value (null = Auto).
        public static void Init(string configured)
        {
            Mode = Parse(configured);
            Current = Resolve();
        }

        public static void Set(ThemeMode mode)
        {
            Mode = mode;
            Current = Resolve();
            Raise();
        }

        // WM_SETTINGCHANGE("ImmersiveColorSet") hook: only matters in Auto mode.
        public static void SystemChanged()
        {
            if (Mode != ThemeMode.Auto) return;
            var p = Resolve();
            if (p == Current) return;
            Current = p;
            Raise();
        }

        public static ThemeMode Parse(string s)
        {
            if (string.Equals(s, "dark", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Dark;
            if (string.Equals(s, "light", StringComparison.OrdinalIgnoreCase)) return ThemeMode.Light;
            return ThemeMode.Auto;
        }

        public static string Serialize(ThemeMode m)
        {
            return m == ThemeMode.Dark ? "dark" : m == ThemeMode.Light ? "light" : "auto";
        }

        private static Palette Resolve()
        {
            bool dark = Mode == ThemeMode.Dark || (Mode == ThemeMode.Auto && SystemPrefersDark());
            return dark ? Dark : Light;
        }

        private static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }   // no key (older Windows) -> light
        }

        private static void Raise()
        {
            var h = Changed;
            if (h != null) h();
        }
    }

    // Shared drawing helpers for the custom-painted controls.
    internal static class Ui
    {
        public static int Dpi(Control c, int logical)
        {
            return (int)Math.Round(logical * c.DeviceDpi / 96.0);
        }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // The two OS integrations dark mode needs: a dark title bar (DWM) and dark
    // scrollbars (uxtheme). Both are best-effort - on Windows versions without
    // them the window simply keeps the stock chrome.
    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public static void DarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            int v = dark ? 1 : 0;
            try
            {
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 1903+); 19 on 1809.
                if (DwmSetWindowAttribute(hwnd, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, 4);
            }
            catch { }
        }

        public static void ThemeScrollbars(Control c, bool dark)
        {
            try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); }
            catch { }
        }
    }
}

