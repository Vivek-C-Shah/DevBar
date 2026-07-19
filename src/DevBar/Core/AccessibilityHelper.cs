namespace DevBar.Core;

// Named AccessibilityHelper, not "Accessibility" — that name collides with
// the built-in COM interop namespace (Accessibility.dll / IAccessible),
// which WPF projects pull in implicitly and which confuses the compiler
// into resolving the name as a namespace instead of this class.
internal static class AccessibilityHelper
{
    /// <summary>
    /// Mirrors Windows' Settings → Accessibility → Visual effects → "Transparency
    /// effects" toggle. When the user has turned this off, the bar must not use
    /// Acrylic blur-behind or the mesh-gradient glass layer — both fall back to
    /// a flat opaque panel. Checked once at startup; a live toggle mid-session
    /// takes effect on the next launch, not immediately (matches how most
    /// native apps handle this exact setting).
    /// </summary>
    public static bool PrefersReducedTransparency()
    {
        try
        {
            var ui = new global::Windows.UI.ViewManagement.UISettings();
            return !ui.AdvancedEffectsEnabled;
        }
        catch
        {
            // If we can't ask, default to the safer (flat, no blur) option.
            return true;
        }
    }
}
