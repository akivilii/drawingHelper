"""Markdown palette parser and data structures for Minecraft item RGB palettes."""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import re
from typing import Iterable, Sequence

import numpy as np

SHADE_LABELS: dict[int, str] = {
    1: "가장 밝음",
    2: "밝음",
    3: "어두움",
    4: "가장 어두움",
}

_GROUP_HEADER_RE = re.compile(r"^##\s+(.+?)\s*$")
_SUMMARY_ITEM_RE = re.compile(
    r"^-\s+`((?:#[0-9A-Fa-f]{6}\s*)+)`\s+-\s+(.+?)\s*$"
)
_TABLE_ROW_RE = re.compile(
    r"^\|\s*([^|]+?)\s*\|\s*"
    r"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*"
    r"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*"
    r"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|\s*"
    r"`#([0-9A-Fa-f]{6})`<br>rgb\((\d+),\s*(\d+),\s*(\d+)\)\s*\|$"
)
_NAME_SPLIT_RE = re.compile(r"^(.+?)\s*\(([^()]+)\)\s*$")


def hex_to_rgb(hex_code: str) -> tuple[int, int, int]:
    """Convert '#RRGGBB' or 'RRGGBB' to an (R, G, B) tuple in [0, 255]."""
    cleaned = hex_code.strip().lstrip("#")
    if len(cleaned) != 6:
        raise ValueError(f"Invalid HEX color: {hex_code!r}")
    return (
        int(cleaned[0:2], 16),
        int(cleaned[2:4], 16),
        int(cleaned[4:6], 16),
    )


def rgb_to_hex(rgb: Sequence[int]) -> str:
    """Convert an (R, G, B) tuple to uppercase '#RRGGBB'."""
    r, g, b = int(rgb[0]), int(rgb[1]), int(rgb[2])
    return f"#{r:02X}{g:02X}{b:02X}"


def _split_item_name(full_name: str) -> tuple[str, str]:
    match = _NAME_SPLIT_RE.match(full_name.strip())
    if match:
        return match.group(1).strip(), match.group(2).strip()
    name = full_name.strip()
    return name, name


@dataclass(frozen=True)
class PaletteColor:
    """Single paintable color corresponding to a Minecraft item and shade level."""

    item_name: str
    korean_name: str
    english_name: str
    shade: int  # 1 (brightest) .. 4 (darkest)
    rgb: tuple[int, int, int]
    hex_code: str
    group: str = ""

    @property
    def shade_label(self) -> str:
        return SHADE_LABELS.get(self.shade, f"{self.shade}단계")

    @property
    def display_label(self) -> str:
        return f"{self.korean_name} ({self.shade}단계: {self.shade_label})"


class Palette:
    """Collection of Minecraft item colors with vectorized RGB array access."""

    def __init__(self, colors: Sequence[PaletteColor]) -> None:
        if not colors:
            raise ValueError("Palette must contain at least one color.")
        self._colors: tuple[PaletteColor, ...] = tuple(colors)
        self._rgb_array: np.ndarray = np.array(
            [c.rgb for c in self._colors], dtype=np.uint8
        )

    def __len__(self) -> int:
        return len(self._colors)

    def __getitem__(self, index: int) -> PaletteColor:
        return self._colors[index]

    def __iter__(self):
        return iter(self._colors)

    @property
    def colors(self) -> tuple[PaletteColor, ...]:
        return self._colors

    @property
    def rgb_array(self) -> np.ndarray:
        """Shape (M, 3) uint8 array of sRGB colors."""
        return self._rgb_array

    @property
    def item_names(self) -> list[str]:
        """Unique item names in palette order."""
        seen: dict[str, None] = {}
        for c in self._colors:
            seen[c.item_name] = None
        return list(seen.keys())

    def filter(
        self,
        *,
        shades: Iterable[int] | None = None,
        include_items: Iterable[str] | None = None,
        exclude_items: Iterable[str] | None = None,
        groups: Iterable[str] | None = None,
    ) -> Palette:
        """Return a filtered subset of the palette."""
        shade_set = set(shades) if shades is not None else None
        include_set = (
            {s.strip().lower() for s in include_items}
            if include_items is not None
            else None
        )
        exclude_set = (
            {s.strip().lower() for s in exclude_items}
            if exclude_items is not None
            else set()
        )
        group_set = {g.strip() for g in groups} if groups is not None else None

        filtered: list[PaletteColor] = []
        for color in self._colors:
            if shade_set is not None and color.shade not in shade_set:
                continue
            if group_set is not None and color.group not in group_set:
                continue

            names = {
                color.item_name.lower(),
                color.korean_name.lower(),
                color.english_name.lower(),
            }
            if include_set is not None and not (names & include_set):
                continue
            if names & exclude_set:
                continue
            filtered.append(color)

        return Palette(filtered)

    @classmethod
    def from_markdown(
        cls,
        path: str | Path,
        *,
        verify_consistency: bool = True,
    ) -> Palette:
        """Parse a Minecraft palette markdown file (supports table and summary formats)."""
        md_path = Path(path)
        text = md_path.read_text(encoding="utf-8")

        table_colors: list[PaletteColor] = []
        summary_colors: list[PaletteColor] = []
        current_group = ""

        for raw_line in text.splitlines():
            line = raw_line.strip()
            if not line:
                continue

            header_match = _GROUP_HEADER_RE.match(line)
            if header_match:
                current_group = header_match.group(1).strip()
                continue

            table_match = _TABLE_ROW_RE.match(line)
            if table_match:
                full_name = table_match.group(1).strip()
                ko_name, en_name = _split_item_name(full_name)
                for shade_idx in range(4):
                    base = 2 + shade_idx * 4
                    hex_str = f"#{table_match.group(base).upper()}"
                    rgb = (
                        int(table_match.group(base + 1)),
                        int(table_match.group(base + 2)),
                        int(table_match.group(base + 3)),
                    )
                    if verify_consistency and hex_to_rgb(hex_str) != rgb:
                        raise ValueError(
                            f"HEX/RGB mismatch in table for {full_name} shade {shade_idx + 1}: "
                            f"{hex_str} vs rgb{rgb}"
                        )
                    table_colors.append(
                        PaletteColor(
                            item_name=full_name,
                            korean_name=ko_name,
                            english_name=en_name,
                            shade=shade_idx + 1,
                            rgb=rgb,
                            hex_code=hex_str,
                            group=current_group,
                        )
                    )
                continue

            summary_match = _SUMMARY_ITEM_RE.match(line)
            if summary_match:
                hex_codes = summary_match.group(1).strip().split()
                full_name = summary_match.group(2).strip()
                ko_name, en_name = _split_item_name(full_name)
                for shade_idx, raw_hex in enumerate(hex_codes, start=1):
                    hex_str = f"#{raw_hex.lstrip('#').upper()}"
                    summary_colors.append(
                        PaletteColor(
                            item_name=full_name,
                            korean_name=ko_name,
                            english_name=en_name,
                            shade=shade_idx,
                            rgb=hex_to_rgb(hex_str),
                            hex_code=hex_str,
                            group=current_group,
                        )
                    )

        if table_colors and summary_colors and verify_consistency:
            if len(table_colors) != len(summary_colors):
                raise ValueError(
                    f"Summary ({len(summary_colors)}) and table ({len(table_colors)}) "
                    "color counts do not match."
                )
            for s_col, t_col in zip(summary_colors, table_colors):
                if (
                    s_col.item_name != t_col.item_name
                    or s_col.shade != t_col.shade
                    or s_col.rgb != t_col.rgb
                ):
                    raise ValueError(
                        f"Summary/table mismatch: {s_col} != {t_col}"
                    )

        colors = table_colors or summary_colors
        if not colors:
            raise ValueError(f"No palette entries found in {md_path}")
        return cls(colors)
