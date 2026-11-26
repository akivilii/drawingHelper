"""CLI entry point for testing color matching and converting images to Minecraft items."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import time

from drawing_helper import (
    ColorMatcher,
    Palette,
    SUPPORTED_METRICS,
    convert_image,
    create_algorithm_comparison,
    generate_color_test_chart,
)

DEFAULT_PALETTE_PATH = Path(__file__).resolve().parent / "minecraft_palette_rgb.md"


def cmd_lookup(args: argparse.Namespace) -> None:
    palette = Palette.from_markdown(args.palette)
    if args.shades:
        palette = palette.filter(shades=args.shades)

    metrics = list(SUPPORTED_METRICS) if args.all_metrics else [args.metric]
    for metric in metrics:
        matcher = ColorMatcher(
            palette,
            metric=metric,
            k_l=args.kl,
            k_c=args.kc,
            k_h=args.kh,
        )
        results = matcher.match_color(args.color, top_k=args.top)
        print(f"\n[{metric.upper()}] Target: {args.color}")
        for rank, res in enumerate(results, start=1):
            c = res.color
            print(
                f"  {rank}. {c.display_label:<32s} | {c.hex_code} rgb{c.rgb} "
                f"| dist={res.distance:.4f}"
            )


def cmd_convert(args: argparse.Namespace) -> None:
    palette = Palette.from_markdown(args.palette)
    if args.shades:
        palette = palette.filter(shades=args.shades)

    t0 = time.perf_counter()
    res = convert_image(
        args.input,
        palette,
        width=args.width,
        height=args.height,
        mode=args.mode,
        resample=args.resample,
        metric=args.metric,
        dithering=args.dithering,
        dither_strength=args.dither_strength,
        preserve_alpha=args.preserve_alpha,
        k_l=args.kl,
        k_c=args.kc,
        k_h=args.kh,
    )
    elapsed_ms = (time.perf_counter() - t0) * 1000.0

    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    res.to_image(scale=args.scale, grid=args.grid).save(out_path)

    print(f"Saved: {out_path} ({res.width}x{res.height}, scale={args.scale}x)")
    print(
        f"Metric: {res.metric} | Dithering: {res.dithering} | "
        f"Time: {elapsed_ms:.1f} ms"
    )
    print(
        f"Perceptual Error (CIEDE2000): mean={res.mean_delta_e2000:.3f}, "
        f"p95={res.p95_delta_e2000:.3f}, max={res.max_delta_e2000:.3f}"
    )

    items = res.item_summary()
    print(f"\nUsed items ({len(items)} distinct items):")
    for row in items[:15]:
        shades_str = ", ".join(f"{k}단계:{v}" for k, v in row["shades"].items())
        print(f"  - {row['item_name']}: {row['total_pixels']}px ({shades_str})")
    if len(items) > 15:
        print(f"  ... and {len(items) - 15} more items")

    if args.json_out:
        json_path = Path(args.json_out)
        json_path.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "width": res.width,
            "height": res.height,
            "metric": res.metric,
            "dithering": res.dithering,
            "mean_delta_e2000": res.mean_delta_e2000,
            "p95_delta_e2000": res.p95_delta_e2000,
            "items": items,
            "grid": res.blueprint_grid(),
        }
        json_path.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"Blueprint JSON saved: {json_path}")


def cmd_compare(args: argparse.Namespace) -> None:
    palette = Palette.from_markdown(args.palette)
    if args.input:
        source = args.input
    else:
        source = generate_color_test_chart(
            width=args.width or 128,
            height=args.height or 96,
        )

    sheet, results = create_algorithm_comparison(
        source,
        palette,
        width=args.width if args.input else None,
        height=args.height if args.input else None,
        scale=args.scale,
        dithering=args.dithering,
    )
    out_path = Path(args.output)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out_path)

    print(f"Comparison sheet saved: {out_path}")
    print("\nAlgorithm Perceptual Accuracy (evaluated in standard CIEDE2000, lower is better):")
    print(f"{'Metric':<12s} | {'Mean dE00':>10s} | {'P95 dE00':>10s} | {'Max dE00':>10s}")
    print("-" * 52)
    for name, res in results.items():
        print(
            f"{name:<12s} | {res.mean_delta_e2000:10.4f} | "
            f"{res.p95_delta_e2000:10.4f} | {res.max_delta_e2000:10.4f}"
        )


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Minecraft item RGB perceptual color matching & pixel art converter."
    )
    parser.add_argument(
        "--palette",
        default=str(DEFAULT_PALETTE_PATH),
        help="Path to minecraft_palette_rgb.md",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    # lookup
    p_lookup = sub.add_parser("lookup", help="Find closest Minecraft items for a color")
    p_lookup.add_argument("color", help="HEX color like '#FF8866'")
    p_lookup.add_argument("--metric", default="ciede2000", choices=SUPPORTED_METRICS)
    p_lookup.add_argument("--all-metrics", action="store_true")
    p_lookup.add_argument("--top", type=int, default=5)
    p_lookup.add_argument("--shades", type=int, nargs="*", choices=[1, 2, 3, 4])
    p_lookup.add_argument("--kl", type=float, default=1.0)
    p_lookup.add_argument("--kc", type=float, default=1.0)
    p_lookup.add_argument("--kh", type=float, default=1.0)
    p_lookup.set_defaults(func=cmd_lookup)

    # convert
    p_conv = sub.add_parser("convert", help="Resize and convert an image to Minecraft items")
    p_conv.add_argument("input", help="Input image path")
    p_conv.add_argument("-o", "--output", required=True, help="Output PNG path")
    p_conv.add_argument("-W", "--width", type=int, default=None)
    p_conv.add_argument("-H", "--height", type=int, default=None)
    p_conv.add_argument("--mode", default="fit", choices=["fit", "stretch", "cover"])
    p_conv.add_argument(
        "--resample", default="box", choices=["box", "lanczos", "bilinear", "nearest"]
    )
    p_conv.add_argument("--metric", default="ciede2000", choices=SUPPORTED_METRICS)
    p_conv.add_argument(
        "--dithering",
        default="none",
        choices=["none", "floyd_steinberg", "bayer4", "bayer8"],
    )
    p_conv.add_argument("--dither-strength", type=float, default=1.0)
    p_conv.add_argument("--scale", type=int, default=1)
    p_conv.add_argument("--grid", action="store_true")
    p_conv.add_argument("--preserve-alpha", action="store_true")
    p_conv.add_argument("--shades", type=int, nargs="*", choices=[1, 2, 3, 4])
    p_conv.add_argument("--kl", type=float, default=1.0)
    p_conv.add_argument("--kc", type=float, default=1.0)
    p_conv.add_argument("--kh", type=float, default=1.0)
    p_conv.add_argument("--json-out", default=None, help="Optional blueprint JSON output")
    p_conv.set_defaults(func=cmd_convert)

    # compare
    p_cmp = sub.add_parser(
        "compare",
        help="Generate visual comparison sheet across color matching metrics",
    )
    p_cmp.add_argument("--input", default=None, help="Optional input image (defaults to synthetic chart)")
    p_cmp.add_argument("-o", "--output", default="comparison.png", help="Output PNG path")
    p_cmp.add_argument("-W", "--width", type=int, default=None)
    p_cmp.add_argument("-H", "--height", type=int, default=None)
    p_cmp.add_argument("--scale", type=int, default=3)
    p_cmp.add_argument(
        "--dithering",
        default="none",
        choices=["none", "floyd_steinberg", "bayer4", "bayer8"],
    )
    p_cmp.set_defaults(func=cmd_compare)

    return parser


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
