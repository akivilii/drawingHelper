"""Verification suite for palette parsing, CIEDE2000/OKLab accuracy, and conversion."""

from __future__ import annotations

from pathlib import Path
import time
import unittest

import numpy as np

from drawing_helper import (
    ColorMatcher,
    Palette,
    SUPPORTED_METRICS,
    ciede2000,
    convert_image,
    generate_color_test_chart,
    linear_to_srgb,
    srgb_to_lab,
    srgb_to_linear,
    srgb_to_oklab,
)

ROOT = Path(__file__).resolve().parent.parent
PALETTE_MD = ROOT / "minecraft_palette_rgb.md"


class TestPaletteParser(unittest.TestCase):
    def test_loads_all_61_items_and_244_colors(self) -> None:
        palette = Palette.from_markdown(PALETTE_MD, verify_consistency=True)
        self.assertEqual(len(palette.item_names), 61)
        self.assertEqual(len(palette), 244)
        self.assertEqual(palette.rgb_array.shape, (244, 3))
        self.assertEqual(palette.rgb_array.dtype, np.uint8)

        # Spot-check first and last items
        self.assertEqual(palette[0].korean_name, "진홍빛 균사체")
        self.assertEqual(palette[0].english_name, "Crimson Hyphae")
        self.assertEqual(palette[0].shade, 1)
        self.assertEqual(palette[0].rgb, (74, 40, 56))
        self.assertEqual(palette[-1].korean_name, "먹물 주머니")
        self.assertEqual(palette[-1].shade, 4)
        self.assertEqual(palette[-1].rgb, (14, 14, 14))

    def test_palette_filtering(self) -> None:
        palette = Palette.from_markdown(PALETTE_MD)
        bright_only = palette.filter(shades=[1])
        self.assertEqual(len(bright_only), 61)
        self.assertTrue(all(c.shade == 1 for c in bright_only))

        no_emerald = palette.filter(exclude_items=["Emerald"])
        self.assertEqual(len(no_emerald), 240)


class TestColorSpaceMath(unittest.TestCase):
    def test_srgb_linear_roundtrip(self) -> None:
        vals = np.linspace(0.0, 1.0, 256)
        roundtrip = linear_to_srgb(srgb_to_linear(vals))
        np.testing.assert_allclose(roundtrip, vals, atol=1e-12)

    def test_ciede2000_sharma_2005_reference_pairs(self) -> None:
        """Exact verification against Table 1 of Sharma, Wu, Dalal (2005)."""
        # (Lab1, Lab2, expected_delta_e_2000)
        sharma_pairs = [
            ((50.0000, 2.6772, -79.7751), (50.0000, 0.0000, -82.7485), 2.0425),
            ((50.0000, 3.1571, -77.2803), (50.0000, 0.0000, -82.7485), 2.8615),
            ((50.0000, 2.8361, -74.0200), (50.0000, 0.0000, -82.7485), 3.4412),
            ((50.0000, -1.3802, -84.2814), (50.0000, 0.0000, -82.7485), 1.0000),
            ((50.0000, -1.1848, -84.8006), (50.0000, 0.0000, -82.7485), 1.0000),
            ((50.0000, -0.9009, -85.5211), (50.0000, 0.0000, -82.7485), 1.0000),
            ((50.0000, 0.0000, 0.0000), (50.0000, -1.0000, 2.0000), 2.3669),
            ((50.0000, -1.0000, 2.0000), (50.0000, 0.0000, 0.0000), 2.3669),
            ((50.0000, 2.4900, -0.0010), (50.0000, -2.4900, 0.0009), 7.1792),
            ((50.0000, 2.4900, -0.0010), (50.0000, -2.4900, 0.0010), 7.1792),
            ((50.0000, 2.4900, -0.0010), (50.0000, -2.4900, 0.0011), 7.2195),
            ((50.0000, 2.4900, -0.0010), (50.0000, -2.4900, 0.0012), 7.2195),
            ((50.0000, -0.0010, 2.4900), (50.0000, 0.0009, -2.4900), 4.8045),
            ((50.0000, -0.0010, 2.4900), (50.0000, 0.0011, -2.4900), 4.7461),
            ((50.0000, 2.5000, 0.0000), (50.0000, 0.0000, -2.5000), 4.3065),
            ((50.0000, 2.5000, 0.0000), (73.0000, 25.0000, -18.0000), 27.1492),
            ((50.0000, 2.5000, 0.0000), (61.0000, -5.0000, 29.0000), 22.8977),
            ((50.0000, 2.5000, 0.0000), (56.0000, -27.0000, -3.0000), 31.9030),
            ((50.0000, 2.5000, 0.0000), (58.0000, 24.0000, 15.0000), 19.4535),
            ((50.0000, 2.5000, 0.0000), (50.0000, 3.1736, 0.5854), 1.0000),
            ((50.0000, 2.5000, 0.0000), (50.0000, 3.2972, 0.0000), 1.0000),
            ((50.0000, 2.5000, 0.0000), (50.0000, 1.8634, 0.5757), 1.0000),
            ((50.0000, 2.5000, 0.0000), (50.0000, 3.2592, 0.3350), 1.0000),
            ((60.2574, -34.0099, 36.2677), (60.4626, -34.1751, 39.4387), 1.2644),
            ((63.0109, -31.0961, -5.8663), (62.8187, -29.7946, -4.0864), 1.2630),
            ((61.2901, 3.7196, -5.3901), (61.4292, 2.2480, -4.9620), 1.8731),
            ((35.0831, -44.1164, 3.7933), (35.0232, -40.0716, 1.5901), 1.8645),
            ((22.7233, 20.0904, -46.6940), (23.0331, 14.9730, -42.5619), 2.0373),
            ((36.4612, 47.8580, 18.3852), (36.2715, 50.5065, 21.2231), 1.4146),
            ((90.8027, -2.0831, 1.4410), (91.1528, -1.6435, 0.0447), 1.4441),
            ((90.9257, -0.5406, -0.9208), (88.6381, -0.8985, -0.7239), 1.5381),
            ((6.7747, -0.2908, -2.4247), (5.8714, -0.0985, -2.2286), 0.6377),
            ((2.0776, 0.0795, -1.1350), (0.9033, -0.0636, -0.5514), 0.9082),
        ]
        lab1 = np.array([p[0] for p in sharma_pairs], dtype=np.float64)
        lab2 = np.array([p[1] for p in sharma_pairs], dtype=np.float64)
        expected = np.array([p[2] for p in sharma_pairs], dtype=np.float64)

        computed = ciede2000(lab1, lab2)
        np.testing.assert_allclose(computed, expected, atol=1e-4)

        # Verify parametric weights (kL=2, kC=1, kH=1) on pair 16:
        # dL'=23, L_bar'=61.5 -> S_L=1.160774, (dL'/S_L)^2=392.6122
        # With kL=2: sqrt(27.1492^2 - 0.75 * 392.6122) = 21.0386
        de_param = ciede2000(
            np.array([50.0, 2.5, 0.0]),
            np.array([73.0, 25.0, -18.0]),
            k_l=2.0,
            k_c=1.0,
            k_h=1.0,
        )
        self.assertAlmostEqual(float(de_param), 21.0386, places=4)

    def test_oklab_reference_primaries(self) -> None:
        """Verify OKLab against Bjorn Ottosson's published sRGB primary values."""
        primaries = np.array(
            [
                [255, 0, 0],
                [0, 255, 0],
                [0, 0, 255],
                [255, 255, 255],
                [0, 0, 0],
            ],
            dtype=np.uint8,
        )
        ok = srgb_to_oklab(primaries)
        expected = np.array(
            [
                [0.627955, 0.224863, 0.125846],
                [0.866440, -0.233887, 0.179498],
                [0.452014, -0.032457, -0.311528],
                [1.000000, 0.000000, 0.000000],
                [0.000000, 0.000000, 0.000000],
            ],
            dtype=np.float64,
        )
        np.testing.assert_allclose(ok, expected, atol=1e-4)

    def test_srgb_to_lab_white_and_black(self) -> None:
        lab = srgb_to_lab(np.array([[0, 0, 0], [255, 255, 255]], dtype=np.uint8))
        np.testing.assert_allclose(lab[0], [0.0, 0.0, 0.0], atol=1e-4)
        np.testing.assert_allclose(lab[1], [100.0, 0.0, 0.0], atol=1e-3)


class TestColorMatcherAndConverter(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.palette = Palette.from_markdown(PALETTE_MD)

    def test_exact_palette_colors_zero_distance(self) -> None:
        for metric in SUPPORTED_METRICS:
            matcher = ColorMatcher(self.palette, metric=metric)
            indices, dists = matcher.match_rgb_batch(self.palette.rgb_array)
            # Every palette color matches itself (or an identical RGB if any) with 0 distance
            np.testing.assert_allclose(dists, 0.0, atol=1e-9)
            matched_rgb = self.palette.rgb_array[indices]
            np.testing.assert_array_equal(matched_rgb, self.palette.rgb_array)

    def test_ciede2000_outperforms_rgb_and_cie76_on_chart(self) -> None:
        chart = generate_color_test_chart(width=128, height=96)
        errors = {}
        for metric in SUPPORTED_METRICS:
            res = convert_image(chart, self.palette, metric=metric)
            errors[metric] = res.mean_delta_e2000

        # CIEDE2000 is the global optimum for CIEDE2000 error; OKLab should beat naive RGB
        self.assertLess(errors["ciede2000"], errors["rgb"])
        self.assertLess(errors["ciede2000"], errors["redmean"])
        self.assertLess(errors["ciede2000"], errors["cie76"])
        self.assertLess(errors["oklab"], errors["rgb"])

    def test_neutral_grays_stay_neutral_in_ciede2000(self) -> None:
        matcher = ColorMatcher(self.palette, metric="ciede2000")
        grays = np.array([[v, v, v] for v in range(15, 256, 10)], dtype=np.uint8)
        indices, _ = matcher.match_rgb_batch(grays)
        matched_lab = srgb_to_lab(self.palette.rgb_array[indices])
        chromas = np.hypot(matched_lab[:, 1], matched_lab[:, 2])
        # All matched grays should have very low chroma (no green/purple tinting)
        self.assertLess(float(np.max(chromas)), 6.0)

    def test_convert_image_resizing_and_dithering(self) -> None:
        chart = generate_color_test_chart(width=160, height=120)
        for dither in ("none", "bayer4", "bayer8", "floyd_steinberg"):
            res = convert_image(
                chart,
                self.palette,
                width=40,
                height=30,
                metric="ciede2000",
                dithering=dither,  # type: ignore[arg-type]
            )
            self.assertEqual((res.width, res.height), (40, 30))
            self.assertEqual(res.pixel_rgb.shape, (30, 40, 3))
            self.assertEqual(len(res.blueprint_grid()), 30)
            self.assertEqual(len(res.blueprint_grid()[0]), 40)
            self.assertGreater(len(res.item_summary()), 5)

    def test_32x32_tile_slicing(self) -> None:
        chart = generate_color_test_chart(width=70, height=50)
        res = convert_image(chart, self.palette, width=70, height=50, metric="ciede2000")
        self.assertEqual(res.tile_grid_shape(32), (3, 2))

        sub00, bounds00 = res.slice_tile(0, 0, 32)
        self.assertEqual(bounds00, (0, 0, 32, 32))
        self.assertEqual((sub00.width, sub00.height), (32, 32))

        # Edge tile handles partial remainder cleanly
        sub21, bounds21 = res.slice_tile(2, 1, 32)
        self.assertEqual(bounds21, (64, 32, 70, 50))
        self.assertEqual((sub21.width, sub21.height), (6, 18))
        np.testing.assert_array_equal(sub21.pixel_rgb, res.pixel_rgb[32:50, 64:70])

    def test_performance_128x128(self) -> None:
        chart = generate_color_test_chart(width=128, height=128)
        arr = np.asarray(chart)
        matcher = ColorMatcher(self.palette, metric="ciede2000")
        t0 = time.perf_counter()
        indices, _ = matcher.match_rgb_batch(arr)
        elapsed = time.perf_counter() - t0
        self.assertEqual(indices.shape, (128, 128))
        # Should finish comfortably within a few seconds even under CPU load
        self.assertLess(elapsed, 5.0)


if __name__ == "__main__":
    unittest.main()
