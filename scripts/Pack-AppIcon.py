"""Generate Kakikomi app icon assets from AppIcon-master.png."""

from __future__ import annotations

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "Assets"
MASTER = ROOT / "Assets" / "AppIcon-master.png"
BG = (15, 23, 42, 255)  # #0F172A


def resize_square(src: Image.Image, size: int) -> Image.Image:
    return src.resize((size, size), Image.Resampling.LANCZOS)


def save_square(src: Image.Image, path: Path, size: int) -> None:
    resize_square(src, size).save(path, format="PNG")


def save_wide_centered(src: Image.Image, path: Path, width: int, height: int) -> None:
    canvas = Image.new("RGBA", (width, height), BG)
    icon_size = min(width, height)
    icon = resize_square(src, icon_size)
    x = (width - icon_size) // 2
    y = (height - icon_size) // 2
    canvas.paste(icon, (x, y), icon if icon.mode == "RGBA" else None)
    canvas.save(path, format="PNG")


def main() -> None:
    if not MASTER.exists():
        raise SystemExit(f"Missing master: {MASTER}")

    src = Image.open(MASTER).convert("RGBA")
    ASSETS.mkdir(parents=True, exist_ok=True)

    # exe / window icon
    ico_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    src.save(ASSETS / "AppIcon.ico", format="ICO", sizes=ico_sizes)

    # WinUI asset set
    save_square(src, ASSETS / "Square150x150Logo.scale-200.png", 300)
    save_square(src, ASSETS / "Square44x44Logo.scale-200.png", 88)
    save_square(src, ASSETS / "Square44x44Logo.targetsize-24_altform-unplated.png", 24)
    save_square(src, ASSETS / "Square44x44Logo.targetsize-48_altform-lightunplated.png", 48)
    save_square(src, ASSETS / "StoreLogo.png", 50)
    save_square(src, ASSETS / "LockScreenLogo.scale-200.png", 96)
    save_wide_centered(src, ASSETS / "Wide310x150Logo.scale-200.png", 620, 300)
    save_wide_centered(src, ASSETS / "SplashScreen.scale-200.png", 1240, 600)

    print(f"OK: packed icons into {ASSETS}")


if __name__ == "__main__":
    main()
