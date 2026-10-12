"""Builds the Windows icons from the iPhone app icon: AirMouse.ico (connected) and
AirMouseIdle.ico (faded, for every other state).

    python3 assets/make_icons.py path/to/icon-1024.png
"""
import sys
from pathlib import Path

from PIL import Image, ImageEnhance

SOURCE = Path(sys.argv[1])
OUT = Path(__file__).parent
SIZES = [(s, s) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)]

art = Image.open(SOURCE).convert("RGBA")
# the macOS-style artwork has a transparent margin; Windows icons fill their square
art = art.crop(art.getchannel("A").getbbox())
art.save(OUT / "AirMouse.ico", sizes=SIZES)

faded = ImageEnhance.Color(art).enhance(0)
faded.putalpha(faded.getchannel("A").point(lambda a: a * 45 // 100))
faded.save(OUT / "AirMouseIdle.ico", sizes=SIZES)
