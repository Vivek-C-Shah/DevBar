using System.Windows.Media;

namespace DevBar.Core;

/// <summary>Small RGB↔HSL helper used to derive the mesh-gradient blob colors from the live accent color.</summary>
internal static class ColorMath
{
    /// <summary>Returns a copy of <paramref name="c"/> with hue shifted by <paramref name="degrees"/> and a new alpha.</summary>
    public static Color RotateHue(Color c, double degrees, byte alpha)
    {
        var (h, s, l) = ToHsl(c);
        h = (h + degrees) % 360;
        if (h < 0) h += 360;
        var rgb = FromHsl(h, s, l);
        return Color.FromArgb(alpha, rgb.R, rgb.G, rgb.B);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;

        if (max == min)
        {
            s = 0;
        }
        else
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        if (s == 0)
        {
            byte v = (byte)Math.Round(l * 255);
            return (v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360.0;

        double r = HueToRgb(p, q, hk + 1.0 / 3);
        double g = HueToRgb(p, q, hk);
        double b = HueToRgb(p, q, hk - 1.0 / 3);

        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
