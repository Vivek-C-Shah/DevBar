using System.Runtime.InteropServices;
using System.Windows.Media;

namespace DevBar.Core;

/// <summary>
/// Real OS-compositor blur behind the window (Windows' "Acrylic" material),
/// not a fake alpha-blend trick — the latter is exactly what leaked crisp
/// background content through the bar earlier in this project. Genuine DWM/
/// compositor blur makes whatever's behind illegible by design, which is both
/// the look the user asked for ("glass like iPhone/Mac apps") and the safe
/// property that made that leak possible: nothing behind it is ever readable.
///
/// Uses the long-standing undocumented-but-stable SetWindowCompositionAttribute
/// API rather than the newer DWMWA_SYSTEMBACKDROP_TYPE (Mica), because that
/// newer API requires AllowsTransparency="False" and DWM-level corner
/// rounding — a different window model than the AllowsTransparency=True +
/// WM_NCHITTEST click-through setup this app already relies on. This one
/// composites correctly with a layered (AllowsTransparency) window, which is
/// exactly the scenario it was originally built for.
///
/// IMPORTANT: live blur-behind is not free — DWM has to keep re-sampling
/// whatever's behind the window for as long as blur is enabled, which costs
/// real idle CPU (measured ~40% of a core here, vs. 0.0% with it off).
/// That's a direct conflict with this app's "never cost CPU while idle"
/// requirement, so callers must only enable it while the bar is expanded
/// (call <see cref="Enable"/> in Expand(), <see cref="Disable"/> in
/// Collapse()) — never leave it on for the ~99% of the time the bar sits
/// idle as a small pill. There's a brief settling cost right after each
/// toggle as DWM tears down/rebuilds its compositor swap chain, but it's
/// gone within a couple of seconds — measured 0.0% once settled.
/// </summary>
internal static class GlassEffect
{
    private enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor; // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute; // WCA_ACCENT_POLICY = 19
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Enables acrylic blur-behind, tinted with <paramref name="tint"/> at
    /// <paramref name="tintOpacity"/> (0-255). Falls back to plain blur (no
    /// acrylic noise/tint layer) on Windows versions that don't support
    /// acrylic, and no-ops silently if the whole API is unavailable — a
    /// missing blur effect should never be a reason this app fails to start.
    /// Call <see cref="Disable"/> as soon as the bar collapses.
    /// </summary>
    public static void Enable(IntPtr hwnd, Color tint, byte tintOpacity)
    {
        try
        {
            int abgr = (tintOpacity << 24) | (tint.B << 16) | (tint.G << 8) | tint.R;
            var accent = new AccentPolicy
            {
                AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = abgr,
            };

            if (!Send(hwnd, accent))
            {
                // Acrylic unsupported on this build (e.g. pre-1803) — plain blur still works.
                accent.AccentState = AccentState.ACCENT_ENABLE_BLURBEHIND;
                Send(hwnd, accent);
            }
        }
        catch
        {
            // No blur is a cosmetic downgrade, never a crash.
        }
    }

    /// <summary>Turns blur-behind back off — this is what keeps idle CPU at 0%.</summary>
    public static void Disable(IntPtr hwnd)
    {
        try
        {
            Send(hwnd, new AccentPolicy { AccentState = AccentState.ACCENT_DISABLED });
        }
        catch
        {
            // best-effort; worst case the panel just stays opaque
        }
    }

    private static bool Send(IntPtr hwnd, AccentPolicy accent)
    {
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                Data = ptr,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
