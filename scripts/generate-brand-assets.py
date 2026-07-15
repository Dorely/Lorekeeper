from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
BRAND_DIR = ROOT / "Lorekeeper" / "wwwroot" / "branding"
SOURCE_PATH = BRAND_DIR / "icon.png"

PNG_SIZES = {
    "icon-512.png": 512,
    "icon-192.png": 192,
    "apple-touch-icon.png": 180,
    "favicon-32x32.png": 32,
    "favicon-16x16.png": 16,
}
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


def main() -> None:
    with Image.open(SOURCE_PATH) as source_image:
        source = source_image.convert("RGBA")

    if source.size != (1024, 1024):
        raise ValueError(f"{SOURCE_PATH} must be a 1024x1024 image; found {source.size}.")

    for filename, size in PNG_SIZES.items():
        resized = source.resize((size, size), Image.Resampling.LANCZOS)
        resized.save(BRAND_DIR / filename, format="PNG", optimize=True)

    source.save(
        BRAND_DIR / "icon.ico",
        format="ICO",
        sizes=[(size, size) for size in ICO_SIZES],
    )


if __name__ == "__main__":
    main()
