"""Perceptual color space conversions and distance metrics (vectorized NumPy).

Implements:
- Exact sRGB <-> Linear RGB (IEC 61966-2-1)
- Exact Linear RGB -> CIE XYZ (D65) -> CIE L*a*b*
- Exact CIEDE2000 (Delta E 2000) following Sharma, Wu, Dalal (2005)
- Exact OKLab (Bjorn Ottosson, 2020) and OKLab Euclidean distance
- CIE76 (Lab Euclidean), Redmean (weighted RGB), and sRGB Euclidean baselines
"""

from __future__ import annotations

import numpy as np

# IEC 61966-2-1 sRGB -> CIE XYZ (D65, 2-degree standard observer)
_LINEAR_RGB_TO_XYZ = np.array(
    [
        [0.4124564, 0.3575761, 0.1804375],
        [0.2126729, 0.7151522, 0.0721750],
        [0.0193339, 0.1191920, 0.9503041],
    ],
    dtype=np.float64,
)

# CIE D65 reference white
_D65_WHITE = np.array([0.95047, 1.00000, 1.08883], dtype=np.float64)

# CIE Lab constants (exact rational definitions)
_LAB_EPSILON = (6.0 / 29.0) ** 3  # 216 / 24389
_LAB_FACTOR = (1.0 / 3.0) * ((29.0 / 6.0) ** 2)
_LAB_OFFSET = 4.0 / 29.0

# OKLab matrices (Bjorn Ottosson, 2020)
_LINEAR_RGB_TO_OKLAB_LMS = np.array(
    [
        [0.4122214708, 0.5363325363, 0.0514459929],
        [0.2119034982, 0.6806995451, 0.1073969566],
        [0.0883024619, 0.2817188376, 0.6299787005],
    ],
    dtype=np.float64,
)

_OKLAB_LMS_TO_LAB = np.array(
    [
        [0.2104542553, 0.7936177850, -0.0040720468],
        [1.9779984951, -2.4285922050, 0.4505937099],
        [0.0259040371, 0.7827717662, -0.8086757660],
    ],
    dtype=np.float64,
)

_POW_25_7 = 25.0**7
_PI = float(np.pi)
_TWO_PI = float(2.0 * np.pi)
_RAD_6 = float(np.deg2rad(6.0))
_RAD_25 = float(np.deg2rad(25.0))
_RAD_30 = float(np.deg2rad(30.0))
_RAD_60 = float(np.deg2rad(60.0))
_RAD_63 = float(np.deg2rad(63.0))
_RAD_275 = float(np.deg2rad(275.0))


def _pow7(x: np.ndarray) -> np.ndarray:
    """Fast exact 7th power using 4 multiplications instead of general pow()."""
    x2 = x * x
    x4 = x2 * x2
    return x4 * x2 * x


def _as_srgb_unit(rgb: np.ndarray) -> np.ndarray:
    """Convert sRGB array (uint8 [0..255] or float [0..255]) to float64 in [0, 1]."""
    arr = np.asarray(rgb, dtype=np.float64)
    if arr.shape[-1] != 3:
        raise ValueError(f"Expected last dimension to be 3, got shape {arr.shape}")
    return np.clip(arr / 255.0, 0.0, 1.0)


def srgb_to_linear(srgb_unit: np.ndarray) -> np.ndarray:
    """Convert gamma-encoded sRGB in [0, 1] to linear-light RGB in [0, 1]."""
    c = np.asarray(srgb_unit, dtype=np.float64)
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(linear_rgb: np.ndarray) -> np.ndarray:
    """Convert linear-light RGB in [0, 1] to gamma-encoded sRGB in [0, 1]."""
    c = np.clip(np.asarray(linear_rgb, dtype=np.float64), 0.0, 1.0)
    return np.where(
        c <= 0.0031308,
        12.92 * c,
        1.055 * np.power(c, 1.0 / 2.4) - 0.055,
    )


def srgb_to_xyz(rgb: np.ndarray) -> np.ndarray:
    """Convert sRGB [0..255] array (..., 3) to CIE XYZ (D65)."""
    lin = srgb_to_linear(_as_srgb_unit(rgb))
    return lin @ _LINEAR_RGB_TO_XYZ.T


def srgb_to_lab(rgb: np.ndarray) -> np.ndarray:
    """Convert sRGB [0..255] array (..., 3) to CIE L*a*b* (D65)."""
    xyz = srgb_to_xyz(rgb)
    t = xyz / _D65_WHITE
    f = np.where(t > _LAB_EPSILON, np.cbrt(t), _LAB_FACTOR * t + _LAB_OFFSET)

    L = 116.0 * f[..., 1] - 16.0
    a = 500.0 * (f[..., 0] - f[..., 1])
    b = 200.0 * (f[..., 1] - f[..., 2])
    return np.stack([L, a, b], axis=-1)


def srgb_to_oklab(rgb: np.ndarray) -> np.ndarray:
    """Convert sRGB [0..255] array (..., 3) to OKLab (L in [0, 1], a, b)."""
    lin = srgb_to_linear(_as_srgb_unit(rgb))
    lms = lin @ _LINEAR_RGB_TO_OKLAB_LMS.T
    lms_cbrt = np.cbrt(np.maximum(lms, 0.0))
    return lms_cbrt @ _OKLAB_LMS_TO_LAB.T


def ciede2000(
    lab1: np.ndarray,
    lab2: np.ndarray,
    *,
    k_l: float = 1.0,
    k_c: float = 1.0,
    k_h: float = 1.0,
) -> np.ndarray:
    """Compute exact CIEDE2000 (Delta E 2000) between broadcastable Lab arrays.

    Follows Sharma, Wu, Dalal (2005), 'The CIEDE2000 Color-Difference Formula:
    Implementation Notes, Supplementary Test Data, and Mathematical Observations'.

    Parameters
    ----------
    lab1, lab2:
        Arrays of shape (..., 3) in CIE L*a*b*. Must be broadcastable on leading dims.
    k_l, k_c, k_h:
        Parametric weighting factors for Lightness, Chroma, and Hue (default 1.0).

    Returns
    -------
    np.ndarray
        Delta E 2000 values with shape equal to broadcast leading dimensions.
    """
    lab1 = np.asarray(lab1, dtype=np.float64)
    lab2 = np.asarray(lab2, dtype=np.float64)

    L1, a1, b1 = lab1[..., 0], lab1[..., 1], lab1[..., 2]
    L2, a2, b2 = lab2[..., 0], lab2[..., 1], lab2[..., 2]

    # Step 1: Calculate C'i and h'i (all angles kept in radians for speed)
    C1 = np.hypot(a1, b1)
    C2 = np.hypot(a2, b2)
    C_bar = 0.5 * (C1 + C2)

    C_bar_7 = _pow7(C_bar)
    G = 0.5 * (1.0 - np.sqrt(C_bar_7 / (C_bar_7 + _POW_25_7)))

    a1_p = (1.0 + G) * a1
    a2_p = (1.0 + G) * a2

    C1_p = np.hypot(a1_p, b1)
    C2_p = np.hypot(a2_p, b2)

    h1_p = np.arctan2(b1, a1_p)
    h1_p = np.where(h1_p < 0.0, h1_p + _TWO_PI, h1_p)
    h1_p = np.where((a1_p == 0.0) & (b1 == 0.0), 0.0, h1_p)

    h2_p = np.arctan2(b2, a2_p)
    h2_p = np.where(h2_p < 0.0, h2_p + _TWO_PI, h2_p)
    h2_p = np.where((a2_p == 0.0) & (b2 == 0.0), 0.0, h2_p)

    # Step 2: Calculate dL', dC', dH'
    dL_p = L2 - L1
    dC_p = C2_p - C1_p

    C_prod = C1_p * C2_p
    dh_diff = h2_p - h1_p
    dh_p = np.where(
        C_prod == 0.0,
        0.0,
        np.where(
            dh_diff > _PI,
            dh_diff - _TWO_PI,
            np.where(dh_diff < -_PI, dh_diff + _TWO_PI, dh_diff),
        ),
    )

    dH_p = 2.0 * np.sqrt(C_prod) * np.sin(0.5 * dh_p)

    # Step 3: Calculate CIEDE2000 Color-Difference
    L_bar_p = 0.5 * (L1 + L2)
    C_bar_p = 0.5 * (C1_p + C2_p)

    h_sum = h1_p + h2_p
    h_abs_diff = np.abs(h1_p - h2_p)
    h_bar_p = np.where(
        C_prod == 0.0,
        h_sum,
        np.where(
            h_abs_diff <= _PI,
            0.5 * h_sum,
            np.where(h_sum < _TWO_PI, 0.5 * (h_sum + _TWO_PI), 0.5 * (h_sum - _TWO_PI)),
        ),
    )

    T = (
        1.0
        - 0.17 * np.cos(h_bar_p - _RAD_30)
        + 0.24 * np.cos(2.0 * h_bar_p)
        + 0.32 * np.cos(3.0 * h_bar_p + _RAD_6)
        - 0.20 * np.cos(4.0 * h_bar_p - _RAD_63)
    )

    u = (h_bar_p - _RAD_275) / _RAD_25
    two_d_theta = _RAD_60 * np.exp(-(u * u))
    C_bar_p_7 = _pow7(C_bar_p)
    R_C = 2.0 * np.sqrt(C_bar_p_7 / (C_bar_p_7 + _POW_25_7))

    L_minus_50 = L_bar_p - 50.0
    L_minus_50_sq = L_minus_50 * L_minus_50
    S_L = 1.0 + (0.015 * L_minus_50_sq) / np.sqrt(20.0 + L_minus_50_sq)
    S_C = 1.0 + 0.045 * C_bar_p
    S_H = 1.0 + 0.015 * C_bar_p * T
    R_T = -np.sin(two_d_theta) * R_C

    term_L = dL_p / (k_l * S_L)
    term_C = dC_p / (k_c * S_C)
    term_H = dH_p / (k_h * S_H)

    delta_e_sq = (
        term_L * term_L
        + term_C * term_C
        + term_H * term_H
        + R_T * term_C * term_H
    )
    return np.sqrt(np.maximum(delta_e_sq, 0.0))


def oklab_distance(
    oklab1: np.ndarray,
    oklab2: np.ndarray,
    *,
    w_l: float = 1.0,
    w_a: float = 1.0,
    w_b: float = 1.0,
) -> np.ndarray:
    """Compute (optionally weighted) Euclidean distance in OKLab space."""
    diff = np.asarray(oklab1, dtype=np.float64) - np.asarray(oklab2, dtype=np.float64)
    return np.sqrt(
        (w_l * diff[..., 0]) ** 2
        + (w_a * diff[..., 1]) ** 2
        + (w_b * diff[..., 2]) ** 2
    )


def cie76_distance(lab1: np.ndarray, lab2: np.ndarray) -> np.ndarray:
    """Compute CIE76 (Delta E 76) Euclidean distance in CIE L*a*b*."""
    diff = np.asarray(lab1, dtype=np.float64) - np.asarray(lab2, dtype=np.float64)
    return np.sqrt(np.sum(diff**2, axis=-1))


def redmean_distance(rgb1: np.ndarray, rgb2: np.ndarray) -> np.ndarray:
    """Compute CompuPhase Redmean weighted RGB distance (sRGB [0..255])."""
    c1 = np.asarray(rgb1, dtype=np.float64)
    c2 = np.asarray(rgb2, dtype=np.float64)
    r_bar = 0.5 * (c1[..., 0] + c2[..., 0])
    dr = c1[..., 0] - c2[..., 0]
    dg = c1[..., 1] - c2[..., 1]
    db = c1[..., 2] - c2[..., 2]
    return np.sqrt(
        (2.0 + r_bar / 256.0) * (dr**2)
        + 4.0 * (dg**2)
        + (2.0 + (255.0 - r_bar) / 256.0) * (db**2)
    )


def rgb_euclidean_distance(rgb1: np.ndarray, rgb2: np.ndarray) -> np.ndarray:
    """Compute naive sRGB Euclidean distance (sRGB [0..255])."""
    diff = np.asarray(rgb1, dtype=np.float64) - np.asarray(rgb2, dtype=np.float64)
    return np.sqrt(np.sum(diff**2, axis=-1))
