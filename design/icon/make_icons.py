"""Builds every Nyx icon from one design: a dot-matrix crescent moon on black, with one
red dot (Charon, Pluto's moon). Run: python design/icon/make_icons.py"""
import math, os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
INK, RED, BG = (242, 242, 242), (235, 47, 55), (10, 10, 10)

def dots(grid=11):
    """Dot centres (0..1) of a crescent on a grid, plus the red moon dot, centred as a group."""
    pitch = 1.0 / grid
    out = []
    for gy in range(grid):
        for gx in range(grid):
            x, y = (gx + 0.5) * pitch, (gy + 0.5) * pitch
            in_disc = math.hypot(x - 0.5, y - 0.5) <= 0.46
            in_bite = math.hypot(x - 0.70, y - 0.36) <= 0.36
            if in_disc and not in_bite:
                out.append((x, y))
    red = (0.70, 0.36)  # Charon sits in the crescent's opening
    xs = [x for x, _ in out] + [red[0]]
    ys = [y for _, y in out] + [red[1]]
    dx = 0.5 - (min(xs) + max(xs)) / 2
    dy = 0.5 - (min(ys) + max(ys)) / 2
    return [(x + dx, y + dy) for x, y in out], pitch, (red[0] + dx, red[1] + dy)

def render(size, rounded=True, grid=11):
    s = size * 4  # supersample for smooth dots
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    if rounded:
        d.rounded_rectangle([0, 0, s - 1, s - 1], radius=int(s * 0.22), fill=BG)
    else:
        d.rectangle([0, 0, s, s], fill=BG)
    pts, pitch, red = dots(grid)
    pad = 0.12  # keep the moon inside the tile
    area = s * (1 - 2 * pad)
    r = pitch * area * 0.38
    for x, y in pts:
        cx, cy = s * pad + x * area, s * pad + y * area
        d.ellipse([cx - r, cy - r, cx + r, cy + r], fill=INK)
    cx, cy = s * pad + red[0] * area, s * pad + red[1] * area
    rr = r * 1.6
    d.ellipse([cx - rr, cy - rr, cx + rr, cy + rr], fill=RED)
    return im.resize((size, size), Image.LANCZOS)

# ---------------------------------------------------------------- Windows
ico_sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
# small sizes use a coarser grid so the dots stay readable
frames = [render(n, grid=7 if n <= 24 else 9 if n <= 48 else 13) for n in ico_sizes]
frames[-1].save(os.path.join(ROOT, "desktop", "Assets", "app.ico"), sizes=[(n, n) for n in ico_sizes],
                append_images=frames[:-1])
pkg = os.path.join(ROOT, "desktop", "package", "Assets")
render(150, rounded=False, grid=13).save(os.path.join(pkg, "Square150x150Logo.png"))
render(44, rounded=False, grid=9).save(os.path.join(pkg, "Square44x44Logo.png"))
render(50, rounded=False, grid=9).save(os.path.join(pkg, "StoreLogo.png"))
render(512, grid=13).save(os.path.join(ROOT, "design", "icon", "nyx-icon-512.png"))

# ---------------------------------------------------------------- Android
res = os.path.join(ROOT, "android", "app", "src", "main", "res")
for folder in ("drawable", "mipmap-anydpi-v26", "values"):
    os.makedirs(os.path.join(res, folder), exist_ok=True)

def vector(fill_main, fill_red, viewport=108.0, inset=0.28, grid=13, red=True):
    """Dots as circle paths inside the adaptive-icon safe zone."""
    pts, pitch, rd = dots(grid)
    area = viewport * (1 - 2 * inset)
    off = viewport * inset
    r = pitch * area * 0.38
    def circle(cx, cy, rr):
        return f"M{cx - rr:.2f},{cy:.2f}a{rr:.2f},{rr:.2f} 0 1,0 {2 * rr:.2f},0a{rr:.2f},{rr:.2f} 0 1,0 {-2 * rr:.2f},0"
    main = "".join(circle(off + x * area, off + y * area, r) for x, y in pts)
    paths = f'    <path android:fillColor="{fill_main}" android:pathData="{main}"/>\n'
    if red:
        paths += f'    <path android:fillColor="{fill_red}" android:pathData="{circle(off + rd[0] * area, off + rd[1] * area, r * 1.6)}"/>\n'
    return (f'<vector xmlns:android="http://schemas.android.com/apk/res/android"\n'
            f'    android:width="{viewport:.0f}dp" android:height="{viewport:.0f}dp"\n'
            f'    android:viewportWidth="{viewport:.0f}" android:viewportHeight="{viewport:.0f}">\n{paths}</vector>\n')

open(os.path.join(res, "drawable", "ic_launcher_foreground.xml"), "w").write(vector("#F2F2F2", "#EB2F37"))
# themed icons (Android 13+, used a lot by Nothing OS): one colour, the system tints it
open(os.path.join(res, "drawable", "ic_launcher_monochrome.xml"), "w").write(vector("#FFFFFFFF", "#FFFFFFFF"))
# status-bar icon: white only, fills more of its 24dp box
open(os.path.join(res, "drawable", "ic_stat_nyx.xml"), "w").write(vector("#FFFFFFFF", "#FFFFFFFF", viewport=24.0, inset=0.02, grid=9))
open(os.path.join(res, "values", "ic_launcher_background.xml"), "w").write(
    '<?xml version="1.0" encoding="utf-8"?>\n<resources>\n    <color name="ic_launcher_background">#0A0A0A</color>\n</resources>\n')
adaptive = ('<?xml version="1.0" encoding="utf-8"?>\n'
            '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
            '    <background android:drawable="@color/ic_launcher_background"/>\n'
            '    <foreground android:drawable="@drawable/ic_launcher_foreground"/>\n'
            '    <monochrome android:drawable="@drawable/ic_launcher_monochrome"/>\n'
            '</adaptive-icon>\n')
for name in ("ic_launcher.xml", "ic_launcher_round.xml"):
    open(os.path.join(res, "mipmap-anydpi-v26", name), "w").write(adaptive)
print("icons written")
