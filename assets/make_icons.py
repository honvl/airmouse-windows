"""Builds the Windows icons from the iPhone app icon (icon-1024.png in the iPhone app's
AppIcon.appiconset): AirMouse.ico (connected), AirMouseIdle.ico (faded, for every other state),
and the Microsoft Store package's tile and list images in msix/Assets.

    python3 assets/make_icons.py path/to/icon-1024.png
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageEnhance

SOURCE = Path(sys.argv[1])
HERE = Path(__file__).parent
MSIX = HERE.parent / "msix" / "Assets"
ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)
# below this the dark remote fades into its square, so the notification area and other small spots
# get a closer crop around the remote, the signal and the cursor
SMALL = 40

art = Image.open(SOURCE).convert("RGBA")
w = art.width
detail = art.crop((round(w * 0.137), round(w * 0.107), round(w * 0.918), round(w * 0.889)))


def icon(size):
    """the iPhone icon at size pixels, with rounded corners like other Windows app icons"""
    image = (detail if size < SMALL else art).resize((size, size), Image.LANCZOS)
    mask = Image.new("L", (size * 4, size * 4), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size * 4 - 1, size * 4 - 1), radius=size * 4 // 5, fill=255)
    image.putalpha(mask.resize((size, size), Image.LANCZOS))
    return image


def faded(image):
    """grey and see-through, yet still findable on a dark taskbar"""
    grey = ImageEnhance.Color(image).enhance(0)
    grey.putalpha(grey.getchannel("A").point(lambda a: a * 60 // 100))
    return grey


def save_ico(name, images):
    images[-1].save(HERE / name, sizes=[i.size for i in images], append_images=images[:-1])


ready = [icon(s) for s in ICO_SIZES]
save_ico("AirMouse.ico", ready)
save_ico("AirMouseIdle.ico", [faded(i) for i in ready])

MSIX.mkdir(parents=True, exist_ok=True)
for scale in (100, 150, 200):
    icon(150 * scale // 100).save(MSIX / f"Square150x150Logo.scale-{scale}.png")
    icon(44 * scale // 100).save(MSIX / f"Square44x44Logo.scale-{scale}.png")
    icon(50 * scale // 100).save(MSIX / f"StoreLogo.scale-{scale}.png")
# the taskbar and Start's app list ask for exact pixel sizes
for size in (16, 24, 32, 48, 256):
    icon(size).save(MSIX / f"Square44x44Logo.targetsize-{size}.png")
    icon(size).save(MSIX / f"Square44x44Logo.targetsize-{size}_altform-unplated.png")
