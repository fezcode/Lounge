"""Prepares the attract-mode scenes (Settings > General > Easter eggs).

The source art was generated as separate background and foreground images, each
foreground on flat magenta. This keys the magenta out, unmixes the edges against it,
drops the pale outline the generator drew around figures, and writes 16:9 WebP files
to Assets/Attract. Run from the repository root:

    python tools/attract/cut.py <folder with the source images>
"""
import os
import sys

import numpy as np
from PIL import Image, ImageFilter

# Pair number -> (background source, foreground source), by the generator's file names.
PAIRS = {
    1: ("Gemini_Generated_Image_giqwrsgiqwrsgiqw(6).jpg", "Gemini_Generated_Image_giqwrsgiqwrsgiqw(7).jpg"),   # harbour + couch
    2: ("Gemini_Generated_Image_giqwrsgiqwrsgiqw(8).jpg", "Gemini_Generated_Image_giqwrsgiqwrsgiqw(9).jpg"),   # financial district + heist man
    3: ("Gemini_Generated_Image_fqrjt4fqrjt4fqrj.jpg", "Gemini_Generated_Image_fqrjt4fqrjt4fqrj(1).jpg"),      # ocean drive + projector
    4: ("Gemini_Generated_Image_p4ysu2p4ysu2p4ys.jpg", "Gemini_Generated_Image_p4ysu2p4ysu2p4ys(1).jpg"),      # desert motel + director's chair
    5: ("Gemini_Generated_Image_p4ysu2p4ysu2p4ys(2).jpg", "Gemini_Generated_Image_p4ysu2p4ysu2p4ys(3).jpg"),   # rain alley + woman in red
    6: ("Gemini_Generated_Image_p4ysu2p4ysu2p4ys(4).jpg", "Gemini_Generated_Image_p4ysu2p4ysu2p4ys(5).jpg"),   # drive-in + convertible
    7: ("Gemini_Generated_Image_u9smieu9smieu9sm(2).jpg", "Gemini_Generated_Image_u9smieu9smieu9sm.jpg"),      # movie palace + armchair
    8: ("Gemini_Generated_Image_giqwrsgiqwrsgiqw.jpg", "Gemini_Generated_Image_giqwrsgiqwrsgiqw(1).jpg"),      # rooftops + getaway driver
    9: ("Gemini_Generated_Image_giqwrsgiqwrsgiqw(2).jpg", "Gemini_Generated_Image_giqwrsgiqwrsgiqw(3).jpg"),   # coast road + CRT TV
    10: ("Gemini_Generated_Image_giqwrsgiqwrsgiqw(4).jpg", "Gemini_Generated_Image_giqwrsgiqwrsgiqw(5).jpg"),  # hills + detective
}

W, H = 2560, 1440


def to_frame(im):
    """Scales and centre-crops to exactly W x H (cover)."""
    s = max(W / im.width, H / im.height)
    im = im.resize((round(im.width * s), round(im.height * s)), Image.LANCZOS)
    l, t = (im.width - W) // 2, (im.height - H) // 2
    return im.crop((l, t, l + W, t + H))


def key(im):
    a = np.asarray(im.convert("RGB")).astype(np.float32)
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    m = np.minimum(r, b) - g                       # how magenta a pixel is
    border = np.concatenate([m[:8].ravel(), m[-8:].ravel(), m[:, :8].ravel(), m[:, -8:].ravel()])
    ref = float(np.percentile(border, 50))
    alpha = 1 - np.clip((m - ref * 0.35) / (ref * 0.55), 0, 1)
    # The generator outlines figures in a pale halo; shrink the matte past it.
    am = Image.fromarray((alpha * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(5)).filter(ImageFilter.GaussianBlur(0.8))
    alpha2 = np.minimum(alpha, np.asarray(am).astype(np.float32) / 255)
    K = np.median(np.concatenate([a[:8].reshape(-1, 3), a[-8:].reshape(-1, 3)]), axis=0)
    al = np.maximum(alpha, 1e-3)[..., None]
    rgb = np.where(alpha[..., None] > 0.02, (a - (1 - al) * K) / al, 0)
    # Whatever magenta tint is left on soft edges goes too.
    mm = np.minimum(rgb[..., 0], rgb[..., 2]) - rgb[..., 1]
    spill = np.clip(mm, 0, None) * (1 - alpha2)
    rgb[..., 0] -= spill
    rgb[..., 2] -= spill
    out = np.dstack([rgb, alpha2 * 255]).clip(0, 255).astype(np.uint8)
    return Image.fromarray(out, "RGBA")


def stand_on_bottom(f):
    """Loading-screen figures stand on the bottom edge: moves the subject down until its
    lowest solid row touches the frame's bottom."""
    a = np.asarray(f)[..., 3]
    rows = np.where((a > 128).sum(axis=1) > 3)[0]
    if len(rows) == 0:
        return f
    shift = H - 1 - int(rows.max())
    if shift <= 0:
        return f
    out = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    out.paste(f, (0, shift))
    return out


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else "tmp"
    out = os.path.join("Assets", "Attract")
    os.makedirs(out, exist_ok=True)
    for n, (bg, fg) in PAIRS.items():
        b = to_frame(Image.open(os.path.join(src, bg)).convert("RGB"))
        f = stand_on_bottom(to_frame(key(Image.open(os.path.join(src, fg)))))
        b.save(os.path.join(out, f"{n:02d}-bg.webp"), "WEBP", quality=82, method=6)
        f.save(os.path.join(out, f"{n:02d}-fg.webp"), "WEBP", quality=85, method=6)
        if "--check" in sys.argv:
            c = b.convert("RGBA")
            c.alpha_composite(f)
            c.convert("RGB").resize((1280, 720)).save(os.path.join(src, f"_check-{n:02d}.jpg"), quality=85)
    total = sum(os.path.getsize(os.path.join(out, x)) for x in os.listdir(out))
    print(f"{len(PAIRS)} pairs, {total / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
