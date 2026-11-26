"""Image downsampling (gamma-correct linear light) and Minecraft pixel-art conversion."""

from __future__ import annotations

from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Literal, Sequence

import numpy as np
from PIL import Image, ImageDraw

from .color_space import ciede2000, linear_to_srgb, srgb_to_lab, srgb_to_linear
from .matcher import ColorMatcher, DitherMode, MetricName, SUPPORTED_METRICS
from .palette import Palette, PaletteColor

ResizeMode = Literal["fit", "stretch", "cover"]
ResampleMode = Literal["box", "lanczos", "bilinear", "nearest"]

_PIL_RESAMPLE = {
    "box": Image.Resampling.BOX,
    "lanczos": Image.Resampling.LANCZOS,
    "bilinear": Image.Resampling.BILINEAR,
    "nearest": Image.Resampling.NEAREST,
}


def compute_target_size(
    src_w: int,
    src_h: int,
    width: int | None = None,
    height: int | None = None,
    mode: ResizeMode = "fit",
) -> tuple[int, int]:
    """Compute destination (width, height) preserving aspect ratio when requested."""
    if width is None and height is None:
        return src_w, src_h
    if width is not None and width <= 0:
        raise ValueError(f"width must be positive, got {width}")
    if height is not None and height <= 0:
        raise ValueError(f"height must be positive, got {height}")

    if width is not None and height is None:
        return width, max(1, round(src_h * (width / src_w)))
    if height is not None and width is None:
        return max(1, round(src_w * (height / src_h))), height

    assert width is not None and height is not None
    if mode in ("stretch", "cover"):
        return width, height
    if mode == "fit":
        scale = min(width / src_w, height / src_h)
        return max(1, round(src_w * scale)), max(1, round(src_h * scale))
    raise ValueError(f"Unsupported resize mode: {mode!r}")


def _resize_channel_float(
    arr_2d: np.ndarray,
    target_w: int,
    target_h: int,
    resample: Image.Resampling,
) -> np.ndarray:
    img_f = Image.fromarray(arr_2d.astype(np.float32), mode="F")
    resized = img_f.resize((target_w, target_h), resample=resample)
    return np.asarray(resized, dtype=np.float64)


def resize_image_linear(
    image: Image.Image,
    *,
    width: int | None = None,
    height: int | None = None,
    mode: ResizeMode = "fit",
    resample: ResampleMode = "box",
    bg_color: tuple[int, int, int] = (255, 255, 255),
    preserve_alpha: bool = False,
    alpha_threshold: int = 128,
) -> tuple[np.ndarray, np.ndarray | None]:
    """Resize a PIL Image in linear-light RGB space to avoid gamma darkening.

    Returns
    -------
    rgb_u8: (H, W, 3) uint8 resized sRGB image
    alpha_mask: (H, W) bool mask if preserve_alpha=True else None
    """
    rgba = image.convert("RGBA")
    src_w, src_h = rgba.size

    if mode == "cover" and width is not None and height is not None:
        src_ratio = src_w / src_h
        dst_ratio = width / height
        if src_ratio > dst_ratio:
            crop_w = max(1, round(src_h * dst_ratio))
            x0 = (src_w - crop_w) // 2
            rgba = rgba.crop((x0, 0, x0 + crop_w, src_h))
        elif src_ratio < dst_ratio:
            crop_h = max(1, round(src_w / dst_ratio))
            y0 = (src_h - crop_h) // 2
            rgba = rgba.crop((0, y0, src_w, y0 + crop_h))
        src_w, src_h = rgba.size

    target_w, target_h = compute_target_size(src_w, src_h, width, height, mode=mode)
    pil_resample = _PIL_RESAMPLE[resample]

    arr = np.asarray(rgba, dtype=np.float64) / 255.0
    rgb_lin = srgb_to_linear(arr[..., :3])
    alpha = arr[..., 3]

    if preserve_alpha:
        # Premultiply alpha in linear space so transparent borders don't bleed color
        premult = rgb_lin * alpha[..., None]
        if (src_w, src_h) != (target_w, target_h):
            out_a = np.clip(
                _resize_channel_float(alpha, target_w, target_h, pil_resample),
                0.0,
                1.0,
            )
            out_premult = np.stack(
                [
                    _resize_channel_float(premult[..., c], target_w, target_h, pil_resample)
                    for c in range(3)
                ],
                axis=-1,
            )
        else:
            out_a = alpha
            out_premult = premult

        mask = out_a >= (alpha_threshold / 255.0)
        safe_a = np.where(out_a > 1e-6, out_a, 1.0)
        out_lin = np.where(
            mask[..., None],
            np.clip(out_premult / safe_a[..., None], 0.0, 1.0),
            0.0,
        )
        out_srgb = np.rint(linear_to_srgb(out_lin) * 255.0).astype(np.uint8)
        return out_srgb, mask

    # Composite onto background color in linear light before downsampling
    bg_lin = srgb_to_linear(np.array(bg_color, dtype=np.float64) / 255.0)
    comp_lin = rgb_lin * alpha[..., None] + bg_lin * (1.0 - alpha[..., None])

    if (src_w, src_h) != (target_w, target_h):
        if resample == "nearest":
            # Nearest doesn't interpolate; direct PIL resize is identical & fast
            comp_srgb = np.rint(linear_to_srgb(comp_lin) * 255.0).astype(np.uint8)
            resized = Image.fromarray(comp_srgb, mode="RGB").resize(
                (target_w, target_h), resample=pil_resample
            )
            return np.asarray(resized, dtype=np.uint8), None
        out_lin = np.stack(
            [
                _resize_channel_float(comp_lin[..., c], target_w, target_h, pil_resample)
                for c in range(3)
            ],
            axis=-1,
        )
    else:
        out_lin = comp_lin

    out_srgb = np.rint(linear_to_srgb(np.clip(out_lin, 0.0, 1.0)) * 255.0).astype(
        np.uint8
    )
    return out_srgb, None


@dataclass
class ConversionResult:
    """Output of converting an image into a Minecraft item-palette pixel grid."""

    source_resized: np.ndarray  # (H, W, 3) uint8
    pixel_rgb: np.ndarray  # (H, W, 3) uint8
    palette_indices: np.ndarray  # (H, W) int32 (-1 for transparent)
    distances: np.ndarray  # (H, W) float64 in the matcher's metric
    ciede2000_errors: np.ndarray  # (H, W) float64 objective perceptual Delta E 2000
    alpha_mask: np.ndarray | None  # (H, W) bool or None
    palette: Palette
    metric: str
    dithering: str

    @property
    def height(self) -> int:
        return int(self.pixel_rgb.shape[0])

    @property
    def width(self) -> int:
        return int(self.pixel_rgb.shape[1])

    @property
    def mean_delta_e2000(self) -> float:
        errs = self._valid_errors()
        return float(np.mean(errs)) if errs.size else 0.0

    @property
    def p95_delta_e2000(self) -> float:
        errs = self._valid_errors()
        return float(np.percentile(errs, 95)) if errs.size else 0.0

    @property
    def max_delta_e2000(self) -> float:
        errs = self._valid_errors()
        return float(np.max(errs)) if errs.size else 0.0

    def _valid_errors(self) -> np.ndarray:
        if self.alpha_mask is not None:
            return self.ciede2000_errors[self.alpha_mask]
        return self.ciede2000_errors.ravel()

    def to_image(self, *, scale: int = 1, grid: bool = False) -> Image.Image:
        """Render the pixel art as a PIL Image, optionally upscaled with a pixel grid."""
        if scale < 1:
            raise ValueError("scale must be >= 1")

        if self.alpha_mask is not None:
            alpha_u8 = np.where(self.alpha_mask, 255, 0).astype(np.uint8)
            rgba = np.dstack([self.pixel_rgb, alpha_u8])
            img = Image.fromarray(rgba, mode="RGBA")
        else:
            img = Image.fromarray(self.pixel_rgb, mode="RGB")

        if scale > 1:
            img = img.resize(
                (self.width * scale, self.height * scale),
                resample=Image.Resampling.NEAREST,
            )

        if grid and scale >= 4:
            draw = ImageDraw.Draw(img)
            line_color = (40, 40, 40, 128) if img.mode == "RGBA" else (40, 40, 40)
            w_px, h_px = img.size
            for x in range(0, w_px, scale):
                draw.line([(x, 0), (x, h_px - 1)], fill=line_color, width=1)
            for y in range(0, h_px, scale):
                draw.line([(0, y), (w_px - 1, y)], fill=line_color, width=1)

        return img

    def color_counts(self) -> list[tuple[PaletteColor, int]]:
        """Return (PaletteColor, pixel_count) sorted by descending frequency."""
        valid = self.palette_indices[self.palette_indices >= 0]
        counts = Counter(int(i) for i in valid.ravel())
        return [
            (self.palette[idx], cnt)
            for idx, cnt in counts.most_common()
        ]

    def item_summary(self) -> list[dict]:
        """Aggregate required pixel counts grouped by Minecraft item across shades."""
        per_item: dict[str, dict[int, int]] = defaultdict(dict)
        for pcolor, cnt in self.color_counts():
            per_item[pcolor.item_name][pcolor.shade] = cnt

        summary = []
        for item_name, shade_map in per_item.items():
            summary.append(
                {
                    "item_name": item_name,
                    "total_pixels": sum(shade_map.values()),
                    "shades": dict(sorted(shade_map.items())),
                }
            )
        summary.sort(key=lambda row: row["total_pixels"], reverse=True)
        return summary

    def blueprint_grid(self) -> list[list[dict | None]]:
        """Return an HxW nested list mapping every pixel to its Minecraft item & shade."""
        grid: list[list[dict | None]] = []
        for y in range(self.height):
            row: list[dict | None] = []
            for x in range(self.width):
                idx = int(self.palette_indices[y, x])
                if idx < 0:
                    row.append(None)
                else:
                    c = self.palette[idx]
                    row.append(
                        {
                            "item_name": c.item_name,
                            "korean_name": c.korean_name,
                            "english_name": c.english_name,
                            "shade": c.shade,
                            "hex": c.hex_code,
                            "rgb": list(c.rgb),
                        }
                    )
            grid.append(row)
        return grid


def convert_image(
    image: Image.Image | str | Path,
    palette: Palette,
    *,
    width: int | None = None,
    height: int | None = None,
    mode: ResizeMode = "fit",
    resample: ResampleMode = "box",
    metric: MetricName = "ciede2000",
    dithering: DitherMode = "none",
    dither_strength: float = 1.0,
    bg_color: tuple[int, int, int] = (255, 255, 255),
    preserve_alpha: bool = False,
    k_l: float = 1.0,
    k_c: float = 1.0,
    k_h: float = 1.0,
) -> ConversionResult:
    """Downsample an image to target pixel size and map each pixel to nearest item color."""
    pil_img = Image.open(image) if isinstance(image, (str, Path)) else image
    resized_rgb, alpha_mask = resize_image_linear(
        pil_img,
        width=width,
        height=height,
        mode=mode,
        resample=resample,
        bg_color=bg_color,
        preserve_alpha=preserve_alpha,
    )

    matcher = ColorMatcher(
        palette,
        metric=metric,
        k_l=k_l,
        k_c=k_c,
        k_h=k_h,
    )
    indices, distances = matcher.match_image(
        resized_rgb,
        dithering=dithering,
        dither_strength=dither_strength,
    )

    pixel_rgb = palette.rgb_array[indices].copy()
    if alpha_mask is not None:
        indices = np.where(alpha_mask, indices, -1).astype(np.int32)
        pixel_rgb = np.where(alpha_mask[..., None], pixel_rgb, 0).astype(np.uint8)
        distances = np.where(alpha_mask, distances, 0.0)

    # Objective perceptual error evaluated uniformly in standard CIEDE2000
    de2000 = ciede2000(srgb_to_lab(resized_rgb), srgb_to_lab(pixel_rgb))
    if alpha_mask is not None:
        de2000 = np.where(alpha_mask, de2000, 0.0)

    return ConversionResult(
        source_resized=resized_rgb,
        pixel_rgb=pixel_rgb,
        palette_indices=indices,
        distances=distances,
        ciede2000_errors=de2000,
        alpha_mask=alpha_mask,
        palette=palette,
        metric=metric,
        dithering=dithering,
    )


def generate_color_test_chart(width: int = 128, height: int = 96) -> Image.Image:
    """Generate a challenging multi-band color test chart for evaluating matchers.

    Bands (top to bottom):
    1. Hue x Brightness vivid spectrum
    2. Pastel / desaturated spectrum
    3. Skin tones & warm earth gradients
    4. Deep blues, indigos, and purples (tests Lab blue-purple twist)
    5. Pure neutral grayscale ramp (tests chroma bleed / gray tinting)
    """
    arr = np.zeros((height, width, 3), dtype=np.uint8)
    x = np.linspace(0.0, 1.0, width, endpoint=False)

    b1 = int(height * 0.30)
    b2 = int(height * 0.50)
    b3 = int(height * 0.68)
    b4 = int(height * 0.84)

    def hsv_to_rgb_grid(h: np.ndarray, s: np.ndarray, v: np.ndarray) -> np.ndarray:
        h6 = (h * 6.0) % 6.0
        c = v * s
        x_val = c * (1.0 - np.abs(h6 % 2.0 - 1.0))
        m = v - c
        r = np.zeros_like(h)
        g = np.zeros_like(h)
        b = np.zeros_like(h)
        for sector, (rv, gv, bv) in enumerate(
            [
                (c, x_val, 0),
                (x_val, c, 0),
                (0, c, x_val),
                (0, x_val, c),
                (x_val, 0, c),
                (c, 0, x_val),
            ]
        ):
            mask = (h6 >= sector) & (h6 < sector + 1)
            r = np.where(mask, rv, r)
            g = np.where(mask, gv, g)
            b = np.where(mask, bv, b)
        rgb = np.stack([r + m, g + m, b + m], axis=-1)
        return np.rint(np.clip(rgb, 0.0, 1.0) * 255.0).astype(np.uint8)

    # Band 1: Vivid hue x value
    yy = np.linspace(1.0, 0.15, b1)[:, None]
    hh = np.broadcast_to(x[None, :], (b1, width))
    arr[0:b1] = hsv_to_rgb_grid(hh, np.full_like(hh, 0.92), np.broadcast_to(yy, (b1, width)))

    # Band 2: Pastel hue x saturation
    h2 = b2 - b1
    ss = np.linspace(0.15, 0.65, h2)[:, None]
    hh2 = np.broadcast_to(x[None, :], (h2, width))
    arr[b1:b2] = hsv_to_rgb_grid(hh2, np.broadcast_to(ss, (h2, width)), np.full_like(hh2, 0.88))

    # Band 3: Skin tones & warm earth
    h3 = b3 - b2
    skin_stops = np.array(
        [
            [255, 224, 196],
            [241, 194, 155],
            [224, 172, 132],
            [198, 134, 92],
            [141, 85, 56],
            [92, 54, 36],
        ],
        dtype=np.float64,
    )
    t = np.linspace(0.0, len(skin_stops) - 1, width)
    idx0 = np.floor(t).astype(int)
    idx1 = np.minimum(idx0 + 1, len(skin_stops) - 1)
    frac = (t - idx0)[:, None]
    skin_row = skin_stops[idx0] * (1.0 - frac) + skin_stops[idx1] * frac
    shade_scale = np.linspace(1.08, 0.72, h3)[:, None, None]
    arr[b2:b3] = np.rint(np.clip(skin_row[None, :, :] * shade_scale, 0, 255)).astype(np.uint8)

    # Band 4: Blue -> Indigo -> Purple gradient (stress-tests CIE76 vs CIEDE2000/OKLab)
    h4 = b4 - b3
    hue_bp = np.linspace(0.55, 0.82, width)[None, :]
    val_bp = np.linspace(0.95, 0.25, h4)[:, None]
    arr[b3:b4] = hsv_to_rgb_grid(
        np.broadcast_to(hue_bp, (h4, width)),
        np.full((h4, width), 0.78),
        np.broadcast_to(val_bp, (h4, width)),
    )

    # Band 5: Pure neutral grayscale ramp (0 -> 255)
    h5 = height - b4
    gray = np.rint(np.linspace(10, 255, width)).astype(np.uint8)
    arr[b4:height] = np.broadcast_to(gray[None, :, None], (h5, width, 3))

    return Image.fromarray(arr, mode="RGB")


def create_algorithm_comparison(
    image: Image.Image | str | Path,
    palette: Palette,
    *,
    width: int | None = None,
    height: int | None = None,
    metrics: Sequence[MetricName] = ("rgb", "redmean", "cie76", "oklab", "ciede2000"),
    scale: int = 3,
    dithering: DitherMode = "none",
) -> tuple[Image.Image, dict[str, ConversionResult]]:
    """Create a side-by-side comparison sheet across color matching metrics."""
    pil_img = Image.open(image) if isinstance(image, (str, Path)) else image
    results: dict[str, ConversionResult] = {}

    for m in metrics:
        if m not in SUPPORTED_METRICS:
            raise ValueError(f"Unknown metric: {m}")
        results[m] = convert_image(
            pil_img,
            palette,
            width=width,
            height=height,
            metric=m,
            dithering=dithering,
        )

    first = next(iter(results.values()))
    orig_img = Image.fromarray(first.source_resized, mode="RGB").resize(
        (first.width * scale, first.height * scale),
        resample=Image.Resampling.NEAREST,
    )

    panels: list[tuple[str, Image.Image]] = [("Original", orig_img)]
    for m in metrics:
        res = results[m]
        label = f"{m.upper()} | avg dE00={res.mean_delta_e2000:.2f} | p95={res.p95_delta_e2000:.2f}"
        panels.append((label, res.to_image(scale=scale)))

    cols = 3
    rows = (len(panels) + cols - 1) // cols
    panel_w, panel_h = orig_img.size
    header_h = 24
    pad = 12

    sheet_w = cols * panel_w + (cols + 1) * pad
    sheet_h = rows * (panel_h + header_h) + (rows + 1) * pad
    sheet = Image.new("RGB", (sheet_w, sheet_h), (24, 24, 28))
    draw = ImageDraw.Draw(sheet)

    for idx, (label, panel_img) in enumerate(panels):
        r, c = divmod(idx, cols)
        x0 = pad + c * (panel_w + pad)
        y0 = pad + r * (panel_h + header_h + pad)
        draw.text((x0 + 2, y0 + 4), label, fill=(235, 235, 240))
        sheet.paste(panel_img, (x0, y0 + header_h))

    return sheet, results
