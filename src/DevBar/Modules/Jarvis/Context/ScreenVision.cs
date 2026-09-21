using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using DevBar.Modules.Jarvis.Brain;

namespace DevBar.Modules.Jarvis.Context;

/// <summary>
/// "What's this error?" - grabs the window you're working in (or the whole
/// screen) and asks a vision model about it. Only ever runs when you ask
/// something that needs it; the image goes to the configured vision
/// provider (Gemini by default) and is not stored anywhere.
/// </summary>
internal static class ScreenVision
{
    private const int MaxWidth = 1600;

    public static async Task<string> AskAsync(ProviderRouter vision, string question, bool fullScreen, CancellationToken ct)
    {
        var (jpeg, what) = Capture(fullScreen);
        if (jpeg is null) return "Couldn't capture the screen.";
        return Google.Untrusted.Wrap(await AskImageAsync(vision, jpeg, what, question, ct));
    }

    internal static async Task<string> AskImageAsync(ProviderRouter vision, byte[] jpeg, string what, string question, CancellationToken ct)
    {
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You describe screenshots for a voice assistant. Answer the question precisely and briefly; " +
                              "quote exact error messages, file names and line numbers when relevant. Plain text, no markdown.",
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"Screenshot of {what}. {question}" },
                    new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) },
                    },
                },
            },
        };
        var reply = await vision.ChatAsync(messages, new JsonArray(), _ => { }, ct);
        return reply.Text.Length > 0 ? reply.Text : "The vision model returned nothing.";
    }

    private static (byte[]? Jpeg, string What) Capture(bool fullScreen)
    {
        Rectangle rect;
        string what;
        var fg = GetForegroundWindow();
        if (!fullScreen && fg != IntPtr.Zero && DwmGetWindowAttribute(fg, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0
            && r.Right - r.Left > 50 && r.Bottom - r.Top > 50)
        {
            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            var title = new System.Text.StringBuilder(256);
            GetWindowText(fg, title, title.Capacity);
            what = $"the window \"{title}\"";
        }
        else
        {
            rect = new Rectangle(0, 0, GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));
            what = "the whole screen";
        }

        try
        {
            using var shot = new Bitmap(rect.Width, rect.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);

            Bitmap output = shot;
            if (shot.Width > MaxWidth)
            {
                int h = (int)(shot.Height * (MaxWidth / (double)shot.Width));
                output = new Bitmap(shot, new Size(MaxWidth, h));
            }
            using var ms = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, 80L);
            output.Save(ms, codec, ps);
            if (!ReferenceEquals(output, shot)) output.Dispose();
            return (ms.ToArray(), what);
        }
        catch { return (null, what); }
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
