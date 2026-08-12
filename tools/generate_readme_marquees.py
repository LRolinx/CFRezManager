#!/usr/bin/env python3
"""Generate GitHub-compatible animated contributor and supporter marquees."""

from __future__ import annotations

from dataclasses import dataclass
from math import ceil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


WIDTH = 900
HEIGHT = 72
FRAME_DURATION_MS = 20
SCROLL_SPEED_PX = 2
CHIP_HEIGHT = 46
CHIP_GAP = 30
COIN_SIZE = 16
BACKGROUND = "#0d1117"
BORDER = "#30363d"
CHIP_BACKGROUND = "#161b22"
TEXT = "#f0f6fc"
ACCENT = "#e3b341"

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
OUTPUT_DIRECTORY = REPOSITORY_ROOT / "assets" / "readme"


@dataclass(frozen=True)
class MarqueeItem:
    name: str
    detail: str | None = None


def find_font(bold: bool) -> Path:
    filenames = ("msyhbd.ttc", "msyh.ttc") if bold else ("msyh.ttc", "msyhbd.ttc")
    candidates = [Path("C:/Windows/Fonts") / filename for filename in filenames]
    candidates.extend(
        [
            Path("/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc"),
            Path("/System/Library/Fonts/PingFang.ttc"),
        ]
    )

    for candidate in candidates:
        if candidate.is_file():
            return candidate

    raise FileNotFoundError(
        "No CJK font was found. Install Microsoft YaHei or Noto Sans CJK, "
        "then update find_font() with its path."
    )


def text_width(draw: ImageDraw.ImageDraw, value: str, font: ImageFont.FreeTypeFont) -> int:
    return ceil(draw.textlength(value, font=font))


def build_layout(
    items: list[MarqueeItem],
    name_font: ImageFont.FreeTypeFont,
    detail_font: ImageFont.FreeTypeFont,
) -> tuple[list[tuple[MarqueeItem, int]], int]:
    probe = ImageDraw.Draw(Image.new("RGB", (1, 1)))
    layout: list[tuple[MarqueeItem, int]] = []
    track_width = 0

    for item in items:
        width = 40 + text_width(probe, item.name, name_font)
        if item.detail:
            width += 12 + COIN_SIZE + 6 + text_width(probe, item.detail, detail_font)
        layout.append((item, width))
        track_width += width + CHIP_GAP

    return layout, track_width


def draw_chip(
    draw: ImageDraw.ImageDraw,
    x: int,
    width: int,
    item: MarqueeItem,
    name_font: ImageFont.FreeTypeFont,
    detail_font: ImageFont.FreeTypeFont,
) -> None:
    top = (HEIGHT - CHIP_HEIGHT) // 2
    bottom = top + CHIP_HEIGHT
    draw.rounded_rectangle(
        (x, top, x + width, bottom),
        radius=CHIP_HEIGHT // 2,
        fill=CHIP_BACKGROUND,
        outline=BORDER,
        width=1,
    )

    name_box = draw.textbbox((0, 0), item.name, font=name_font)
    name_y = (HEIGHT - (name_box[3] - name_box[1])) // 2 - name_box[1]
    name_x = x + 20
    draw.text((name_x, name_y), item.name, font=name_font, fill=TEXT)

    if item.detail:
        detail_x = name_x + text_width(draw, item.name, name_font) + 12
        coin_y = (HEIGHT - COIN_SIZE) // 2
        draw.ellipse(
            (detail_x, coin_y, detail_x + COIN_SIZE, coin_y + COIN_SIZE),
            fill=ACCENT,
            outline="#f2cc60",
            width=1,
        )
        draw.line(
            (detail_x + COIN_SIZE // 2, coin_y + 4, detail_x + COIN_SIZE // 2, coin_y + COIN_SIZE - 4),
            fill="#9a6700",
            width=2,
        )
        detail_x += COIN_SIZE + 6
        detail_box = draw.textbbox((0, 0), item.detail, font=detail_font)
        detail_y = (HEIGHT - (detail_box[3] - detail_box[1])) // 2 - detail_box[1]
        draw.text((detail_x, detail_y), item.detail, font=detail_font, fill=ACCENT)


def render_marquee(filename: str, items: list[MarqueeItem]) -> Path:
    name_font = ImageFont.truetype(str(find_font(bold=True)), 25)
    detail_font = ImageFont.truetype(str(find_font(bold=True)), 23)
    layout, track_width = build_layout(items, name_font, detail_font)
    frame_count = ceil(track_width / SCROLL_SPEED_PX)
    rgb_frames: list[Image.Image] = []

    for frame_index in range(frame_count):
        offset = round(frame_index * track_width / frame_count)
        frame = Image.new("RGB", (WIDTH, HEIGHT), BACKGROUND)
        draw = ImageDraw.Draw(frame)

        first_track = -(offset // track_width + 1) * track_width
        for track_x in range(first_track, WIDTH + track_width, track_width):
            item_x = track_x - offset % track_width
            for item, item_width in layout:
                draw_chip(draw, item_x, item_width, item, name_font, detail_font)
                item_x += item_width + CHIP_GAP

        draw.rounded_rectangle((0, 0, WIDTH - 1, HEIGHT - 1), radius=10, outline=BORDER, width=1)
        rgb_frames.append(frame)

    palette = rgb_frames[0].quantize(colors=128)
    frames = [
        frame.quantize(palette=palette, dither=Image.Dither.NONE)
        for frame in rgb_frames
    ]

    OUTPUT_DIRECTORY.mkdir(parents=True, exist_ok=True)
    output_path = OUTPUT_DIRECTORY / filename
    frames[0].save(
        output_path,
        format="GIF",
        save_all=True,
        append_images=frames[1:],
        duration=FRAME_DURATION_MS,
        loop=0,
        disposal=2,
        optimize=True,
    )
    return output_path


def main() -> None:
    outputs = [
        render_marquee(
            "contributors.gif",
            [
                MarqueeItem("TigerShota"),
                MarqueeItem("风吹"),
                MarqueeItem("ka9ura"),
            ],
        ),
        render_marquee(
            "supporters-smooth.gif",
            [
                MarqueeItem("黑猫不是警长", "20"),
                MarqueeItem("KissJoJo", "100"),
                MarqueeItem("Saya", "66"),
                MarqueeItem("¹", "1"),
            ],
        ),
    ]

    for output in outputs:
        print(output.relative_to(REPOSITORY_ROOT))


if __name__ == "__main__":
    main()
