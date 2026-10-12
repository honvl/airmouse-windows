"""Builds the Microsoft Store package's tile and list images (msix/Assets) from AirMouse.ico, so
they match the app's own icon.

    python3 assets/make_msix_assets.py
"""
from pathlib import Path

from PIL import Image

HERE = Path(__file__).parent
OUT = HERE.parent / "msix" / "Assets"

icon = Image.open(HERE / "AirMouse.ico")
icon.size = (256, 256)
icon.load()
art = icon.convert("RGBA")


def save(name, size):
    art.resize((size, size), Image.LANCZOS).save(OUT / name)


OUT.mkdir(parents=True, exist_ok=True)
for scale in (100, 150, 200):
    save(f"Square150x150Logo.scale-{scale}.png", 150 * scale // 100)
    save(f"Square44x44Logo.scale-{scale}.png", 44 * scale // 100)
    save(f"StoreLogo.scale-{scale}.png", 50 * scale // 100)
# the taskbar and Start's app list ask for exact pixel sizes
for size in (16, 24, 32, 48, 256):
    save(f"Square44x44Logo.targetsize-{size}.png", size)
    save(f"Square44x44Logo.targetsize-{size}_altform-unplated.png", size)
