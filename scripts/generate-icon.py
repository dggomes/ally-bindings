from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "AllyBindings.Windows" / "Assets"
ASSETS.mkdir(parents=True, exist_ok=True)

scale = 4
size = 256
image = Image.new("RGBA", (size * scale, size * scale), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)

def box(coords, radius, fill=None, outline=None, width=1):
    draw.rounded_rectangle(tuple(v * scale for v in coords), radius * scale, fill=fill, outline=outline, width=width * scale)

def ellipse(coords, fill=None, outline=None, width=1):
    draw.ellipse(tuple(v * scale for v in coords), fill=fill, outline=outline, width=width * scale)

def line(points, fill, width):
    draw.line([(x * scale, y * scale) for x, y in points], fill=fill, width=width * scale, joint="curve")

# Distinctive dark handheld tile with an ultraviolet accent—not ASUS artwork.
box((8, 8, 248, 248), 52, fill="#111522", outline="#5E48D8", width=4)
box((22, 22, 234, 234), 42, fill="#171D2E")

# Stylised handheld/controller body.
body = [(47, 91), (68, 72), (188, 72), (209, 91), (221, 158), (204, 190),
        (176, 176), (80, 176), (52, 190), (35, 158), (47, 91)]
line(body, "#F5F7FF", 9)
line([(52, 91), (72, 84), (184, 84), (204, 91)], "#8B73FF", 7)
box((91, 88, 165, 161), 12, fill="#111522", outline="#8B73FF", width=5)

# Left stick + d-pad.
ellipse((57, 101, 83, 127), fill="#8B73FF", outline="#F5F7FF", width=3)
box((56, 138, 84, 148), 3, fill="#F5F7FF")
box((65, 129, 75, 157), 3, fill="#F5F7FF")

# ABXY cluster.
for x, y, colour in [(187, 105, "#7EE7C4"), (201, 119, "#FF8AAE"), (173, 119, "#70C8FF"), (187, 133, "#FFD36E")]:
    ellipse((x - 7, y - 7, x + 7, y + 7), fill=colour)

# Binding/link mark on the screen.
line([(110, 114), (122, 104), (134, 104)], "#F5F7FF", 6)
line([(122, 143), (134, 153), (146, 153)], "#F5F7FF", 6)
line([(118, 137), (138, 117)], "#8B73FF", 8)
ellipse((105, 102, 122, 119), outline="#F5F7FF", width=5)
ellipse((134, 137, 151, 154), outline="#F5F7FF", width=5)

image = image.resize((size, size), Image.Resampling.LANCZOS)
png_path = ASSETS / "AllyBindings.png"
ico_path = ASSETS / "AllyBindings.ico"
image.save(png_path)
image.save(ico_path, sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)])
print(png_path)
print(ico_path)
