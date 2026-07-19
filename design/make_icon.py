"""
Constructs the DevBar mark: a dark rounded tile, a bright accent bar docked
to the top edge (the strip), with two muted lines beneath it (the desktop
content it sits above). Encodes the product concept directly — no
letter-in-a-box. Exports SVG + a full favicon/icon set.

Palette from .tastemaker/style-lock.md.
"""
import math

BG = (11, 14, 20)          # #0B0E14
ACCENT = (94, 161, 255)    # #5EA1FF fallback accent
LINE = (139, 148, 163)     # #8B94A3 muted

SVG_TEMPLATE = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64">
  <rect width="64" height="64" rx="14" fill="#0B0E14"/>
  <rect x="10" y="14" width="44" height="5" rx="2.5" fill="#5EA1FF"/>
  <rect x="10" y="30" width="44" height="4" rx="2" fill="#8B94A3" opacity="0.55"/>
  <rect x="10" y="40" width="28" height="4" rx="2" fill="#8B94A3" opacity="0.35"/>
</svg>
"""


def write_svg(path):
    with open(path, "w", encoding="utf-8") as f:
        f.write(SVG_TEMPLATE)


def render_png(size):
    from PIL import Image, ImageDraw

    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    scale = size / 64.0

    def rect(x, y, w, h, r, color):
        draw.rounded_rectangle(
            [x * scale, y * scale, (x + w) * scale, (y + h) * scale],
            radius=max(1, r * scale),
            fill=color,
        )

    rect(0, 0, 64, 64, 14, BG + (255,))
    rect(10, 14, 44, 5, 2.5, ACCENT + (255,))
    rect(10, 30, 44, 4, 2, LINE + (140,))
    rect(10, 40, 28, 4, 2, LINE + (90,))
    return img


def main():
    import os

    out_dir = os.path.dirname(os.path.abspath(__file__))
    svg_path = os.path.join(out_dir, "assets", "logo.svg")
    write_svg(svg_path)
    print("wrote", svg_path)

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256, 512]
    imgs = {s: render_png(s) for s in sizes}

    png_path = os.path.join(out_dir, "assets", "logo-512.png")
    imgs[512].save(png_path)
    print("wrote", png_path)

    # ICO with multiple embedded sizes (16/32/48/256) for crisp taskbar/tray rendering
    ico_sizes = [16, 20, 24, 32, 48, 256]
    ico_path = os.path.join(out_dir, "..", "src", "DevBar", "Assets", "devbar.ico")
    imgs[256].save(
        ico_path,
        format="ICO",
        sizes=[(s, s) for s in ico_sizes],
    )
    print("wrote", ico_path)

    # a couple of standalone PNGs for the README hero
    imgs[128].save(os.path.join(out_dir, "assets", "logo-128.png"))


if __name__ == "__main__":
    main()
