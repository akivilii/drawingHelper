"""Core perceptual color matching engine for Minecraft item palettes."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Literal, Sequence

import numpy as np

from .color_space import (
    cie76_distance,
    ciede2000,
    linear_to_srgb,
    oklab_distance,
    redmean_distance,
    rgb_euclidean_distance,
    srgb_to_lab,
    srgb_to_linear,
    srgb_to_oklab,
)
from .palette import Palette, PaletteColor, hex_to_rgb

MetricName = Literal["ciede2000", "oklab", "cie76", "redmean", "rgb"]
DitherMode = Literal["none", "floyd_steinberg", "bayer4", "bayer8"]

SUPPORTED_METRICS: tuple[MetricName, ...] = (
    "ciede2000",
    "oklab",
    "cie76",
    "redmean",
    "rgb",
)

# Normalized Bayer ordered dithering matrices centered around 0 in [-0.5, 0.5)
_BAYER_4 = (
    np.array(
        [
            [0, 8, 2, 10],
            [12, 4, 14, 6],
            [3, 11, 1, 9],
            [15, 7, 13, 5],
        ],
        dtype=np.float64,
    )
    / 16.0
    - 0.5
)


def _make_bayer_8() -> np.ndarray:
    m4 = np.array(
        [
            [0, 8, 2, 10],
            [12, 4, 14, 6],
            [3, 11, 1, 9],
            [15, 7, 13, 5],
        ],
        dtype=np.float64,
    )
    top = np.hstack([4 * m4, 4 * m4 + 2])
    bottom = np.hstack([4 * m4 + 3, 4 * m4 + 1])
    return np.vstack([top, bottom]) / 64.0 - 0.5


_BAYER_8 = _make_bayer_8()


@dataclass(frozen=True)
class MatchResult:
    """Result of matching a single target color to a palette entry."""

    color: PaletteColor
    palette_index: int
    distance: float
    metric: str


def _parse_single_rgb(color: Sequence[int] | str) -> tuple[int, int, int]:
    if isinstance(color, str):
        return hex_to_rgb(color)
    if len(color) != 3:
        raise ValueError(f"Expected 3 RGB channels, got {len(color)}")
    return (int(color[0]), int(color[1]), int(color[2]))


class ColorMatcher:
    """Fast, vectorized perceptual nearest-color matcher against a Palette.

    Default metric is ``ciede2000`` (exact ISO/CIE Delta E 2000), which provides
    the highest alignment with human color perception across lightness, chroma,
    hue, neutral grays, and the blue-purple region.
    """

    def __init__(
        self,
        palette: Palette,
        *,
        metric: MetricName = "ciede2000",
        k_l: float = 1.0,
        k_c: float = 1.0,
        k_h: float = 1.0,
        oklab_weights: tuple[float, float, float] = (1.0, 1.0, 1.0),
        chunk_size: int = 512,
    ) -> None:
        if metric not in SUPPORTED_METRICS:
            raise ValueError(
                f"Unsupported metric {metric!r}. Choose from {SUPPORTED_METRICS}."
            )
        self.palette = palette
        self.metric: MetricName = metric
        self.k_l = float(k_l)
        self.k_c = float(k_c)
        self.k_h = float(k_h)
        self.oklab_weights = tuple(float(w) for w in oklab_weights)
        self.chunk_size = int(chunk_size)

        # Precompute palette coordinates in all color spaces once
        self._palette_rgb = palette.rgb_array.astype(np.float64)
        self._palette_lab = srgb_to_lab(self._palette_rgb)
        self._palette_oklab = srgb_to_oklab(self._palette_rgb)

    def _compute_distance_matrix(self, query_rgb: np.ndarray) -> np.ndarray:
        """Compute (N, M) distance matrix for query_rgb of shape (N, 3)."""
        q = np.asarray(query_rgb, dtype=np.float64)
        if self.metric == "ciede2000":
            q_lab = srgb_to_lab(q)
            return ciede2000(
                q_lab[:, None, :],
                self._palette_lab[None, :, :],
                k_l=self.k_l,
                k_c=self.k_c,
                k_h=self.k_h,
            )
        if self.metric == "oklab":
            q_ok = srgb_to_oklab(q)
            wl, wa, wb = self.oklab_weights
            return oklab_distance(
                q_ok[:, None, :],
                self._palette_oklab[None, :, :],
                w_l=wl,
                w_a=wa,
                w_b=wb,
            )
        if self.metric == "cie76":
            q_lab = srgb_to_lab(q)
            return cie76_distance(
                q_lab[:, None, :],
                self._palette_lab[None, :, :],
            )
        if self.metric == "redmean":
            return redmean_distance(
                q[:, None, :],
                self._palette_rgb[None, :, :],
            )
        if self.metric == "rgb":
            return rgb_euclidean_distance(
                q[:, None, :],
                self._palette_rgb[None, :, :],
            )
        raise AssertionError(f"Unhandled metric: {self.metric}")

    def match_color(
        self,
        color: Sequence[int] | str,
        *,
        top_k: int = 1,
    ) -> list[MatchResult]:
        """Find the top-k perceptually closest palette colors for one RGB or HEX color."""
        if top_k < 1:
            raise ValueError("top_k must be >= 1")
        rgb = _parse_single_rgb(color)
        query = np.array([rgb], dtype=np.float64)
        dists = self._compute_distance_matrix(query)[0]

        k = min(top_k, len(self.palette))
        order = np.argsort(dists, kind="stable")[:k]
        return [
            MatchResult(
                color=self.palette[int(idx)],
                palette_index=int(idx),
                distance=float(dists[idx]),
                metric=self.metric,
            )
            for idx in order
        ]

    def match_rgb_batch(
        self,
        rgb_array: np.ndarray,
    ) -> tuple[np.ndarray, np.ndarray]:
        """Match an array of sRGB [0..255] colors of shape (..., 3) to nearest palette indices.

        Uses exact unique-color deduplication for uint8 arrays so repeated colors
        are never recomputed, and chunks large batches to keep peak memory low.

        Returns
        -------
        indices: np.ndarray of int32 with shape rgb_array.shape[:-1]
        distances: np.ndarray of float64 with shape rgb_array.shape[:-1]
        """
        arr = np.asarray(rgb_array)
        if arr.ndim < 1 or arr.shape[-1] != 3:
            raise ValueError(f"Expected shape (..., 3), got {arr.shape}")

        lead_shape = arr.shape[:-1]
        flat = arr.reshape(-1, 3)

        if flat.shape[0] == 0:
            return (
                np.empty(lead_shape, dtype=np.int32),
                np.empty(lead_shape, dtype=np.float64),
            )

        # Deduplicate exact integer sRGB colors for large speedups without approximation
        if np.issubdtype(flat.dtype, np.integer):
            flat_u8 = np.clip(flat, 0, 255).astype(np.uint8, copy=False)
            packed = (
                (flat_u8[:, 0].astype(np.uint32) << 16)
                | (flat_u8[:, 1].astype(np.uint32) << 8)
                | flat_u8[:, 2].astype(np.uint32)
            )
            unique_packed, inverse = np.unique(packed, return_inverse=True)
            unique_rgb = np.stack(
                [
                    (unique_packed >> 16) & 0xFF,
                    (unique_packed >> 8) & 0xFF,
                    unique_packed & 0xFF,
                ],
                axis=-1,
            ).astype(np.float64)

            u_indices, u_dists = self._match_chunked(unique_rgb)
            indices = u_indices[inverse].reshape(lead_shape)
            distances = u_dists[inverse].reshape(lead_shape)
            return indices, distances

        indices, distances = self._match_chunked(flat.astype(np.float64, copy=False))
        return indices.reshape(lead_shape), distances.reshape(lead_shape)

    def _match_chunked(self, flat_rgb: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        n = flat_rgb.shape[0]
        out_idx = np.empty(n, dtype=np.int32)
        out_dist = np.empty(n, dtype=np.float64)

        for start in range(0, n, self.chunk_size):
            end = min(start + self.chunk_size, n)
            dmat = self._compute_distance_matrix(flat_rgb[start:end])
            best = np.argmin(dmat, axis=1)
            out_idx[start:end] = best.astype(np.int32)
            out_dist[start:end] = dmat[np.arange(end - start), best]

        return out_idx, out_dist

    def match_image(
        self,
        image_rgb: np.ndarray,
        *,
        dithering: DitherMode = "none",
        dither_strength: float = 1.0,
    ) -> tuple[np.ndarray, np.ndarray]:
        """Map an (H, W, 3) uint8 RGB image to palette indices and perceptual distances.

        Parameters
        ----------
        image_rgb:
            (H, W, 3) uint8 array in sRGB [0..255].
        dithering:
            - "none": pure per-pixel nearest perceptual match (default)
            - "floyd_steinberg": serpentine Floyd-Steinberg error diffusion in linear RGB
            - "bayer4" / "bayer8": ordered Bayer dithering
        dither_strength:
            Multiplier for dithering intensity in [0.0, 1.0] (default 1.0).
        """
        img = np.asarray(image_rgb, dtype=np.uint8)
        if img.ndim != 3 or img.shape[2] != 3:
            raise ValueError(f"Expected (H, W, 3) image, got {img.shape}")

        if dithering == "none" or dither_strength <= 0.0:
            return self.match_rgb_batch(img)

        strength = float(np.clip(dither_strength, 0.0, 1.0))

        if dithering in ("bayer4", "bayer8"):
            matrix = _BAYER_4 if dithering == "bayer4" else _BAYER_8
            h, w, _ = img.shape
            mh, mw = matrix.shape
            tiled = np.tile(matrix, ((h + mh - 1) // mh, (w + mw - 1) // mw))[:h, :w]
            # Spread scaled for a ~244-color palette (~24 sRGB levels)
            spread = 24.0 * strength
            perturbed = np.clip(
                img.astype(np.float64) + tiled[:, :, None] * spread, 0.0, 255.0
            )
            indices, _ = self.match_rgb_batch(np.rint(perturbed).astype(np.uint8))
            matched_rgb = self.palette.rgb_array[indices]
            distances = self.pairwise_distance(img, matched_rgb)
            return indices, distances

        if dithering == "floyd_steinberg":
            return self._floyd_steinberg_serpentine(img, strength=strength)

        raise ValueError(
            f"Unsupported dithering mode {dithering!r}. "
            "Choose from 'none', 'floyd_steinberg', 'bayer4', 'bayer8'."
        )

    def _floyd_steinberg_serpentine(
        self,
        img_u8: np.ndarray,
        *,
        strength: float = 1.0,
    ) -> tuple[np.ndarray, np.ndarray]:
        """Serpentine Floyd-Steinberg dithering with error diffusion in linear light."""
        h, w, _ = img_u8.shape
        # Work in linear RGB [0, 1] so diffused luminance averages physically
        buf_lin = srgb_to_linear(img_u8.astype(np.float64) / 255.0)
        palette_lin = srgb_to_linear(self._palette_rgb / 255.0)
        indices = np.empty((h, w), dtype=np.int32)

        for y in range(h):
            left_to_right = (y % 2) == 0
            xs = range(w) if left_to_right else range(w - 1, -1, -1)
            step = 1 if left_to_right else -1

            for x in xs:
                old_lin = np.clip(buf_lin[y, x], 0.0, 1.0)
                old_srgb = linear_to_srgb(old_lin) * 255.0

                dists = self._compute_distance_matrix(old_srgb[None, :])[0]
                idx = int(np.argmin(dists))
                indices[y, x] = idx

                # Clamp diffused error to prevent halo blowup on out-of-gamut regions
                err = np.clip(old_lin - palette_lin[idx], -0.35, 0.35) * strength

                nx = x + step
                if 0 <= nx < w:
                    buf_lin[y, nx] += err * (7.0 / 16.0)
                if y + 1 < h:
                    px = x - step
                    if 0 <= px < w:
                        buf_lin[y + 1, px] += err * (3.0 / 16.0)
                    buf_lin[y + 1, x] += err * (5.0 / 16.0)
                    if 0 <= nx < w:
                        buf_lin[y + 1, nx] += err * (1.0 / 16.0)

        matched_rgb = self.palette.rgb_array[indices]
        distances = self.pairwise_distance(img_u8, matched_rgb)
        return indices, distances

    def pairwise_distance(self, rgb1: np.ndarray, rgb2: np.ndarray) -> np.ndarray:
        """Compute element-wise distance between two matching-shape (..., 3) RGB arrays."""
        c1 = np.asarray(rgb1, dtype=np.float64)
        c2 = np.asarray(rgb2, dtype=np.float64)
        if self.metric == "ciede2000":
            return ciede2000(
                srgb_to_lab(c1),
                srgb_to_lab(c2),
                k_l=self.k_l,
                k_c=self.k_c,
                k_h=self.k_h,
            )
        if self.metric == "oklab":
            wl, wa, wb = self.oklab_weights
            return oklab_distance(
                srgb_to_oklab(c1),
                srgb_to_oklab(c2),
                w_l=wl,
                w_a=wa,
                w_b=wb,
            )
        if self.metric == "cie76":
            return cie76_distance(srgb_to_lab(c1), srgb_to_lab(c2))
        if self.metric == "redmean":
            return redmean_distance(c1, c2)
        if self.metric == "rgb":
            return rgb_euclidean_distance(c1, c2)
        raise AssertionError(f"Unhandled metric: {self.metric}")
