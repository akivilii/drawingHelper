"""Simple Tkinter test UI for Minecraft item color matching & pixel conversion.

Designed to stay minimal and cleanly separated from `drawing_helper` core logic
so the UI can later be replaced with a C# frontend.
"""

from __future__ import annotations

from pathlib import Path
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from PIL import Image, ImageTk

from drawing_helper import (
    ConversionResult,
    Palette,
    SUPPORTED_METRICS,
    convert_image,
)

DEFAULT_PALETTE_PATH = Path(__file__).resolve().parent / "minecraft_palette_rgb.md"
DITHER_OPTIONS = ("none", "bayer4", "bayer8", "floyd_steinberg")
RESIZE_MODES = ("fit", "stretch", "cover")


class DrawingHelperApp:
    """Minimal Python test UI: load image, convert to Minecraft palette, inspect result."""

    def __init__(self, root: tk.Tk, palette_path: Path = DEFAULT_PALETTE_PATH) -> None:
        self.root = root
        self.root.title("Minecraft Drawing Helper - 색상 매칭 테스트 UI")
        self.root.geometry("1120x680")
        self.root.minsize(820, 520)

        self.palette = Palette.from_markdown(palette_path)
        self.source_path: Path | None = None
        self.source_image: Image.Image | None = None
        self.result: ConversionResult | None = None

        # Keep Tk PhotoImage references alive
        self._orig_photo: ImageTk.PhotoImage | None = None
        self._conv_photo: ImageTk.PhotoImage | None = None
        # Display geometry of the converted image inside right canvas (for hover lookup)
        self._conv_box: tuple[int, int, int, int] | None = None

        self._build_ui()

    def _build_ui(self) -> None:
        # --- Top toolbar ---
        toolbar = ttk.Frame(self.root, padding=(10, 8))
        toolbar.pack(fill=tk.X)

        self.btn_open = ttk.Button(
            toolbar, text="이미지 삽입", command=self.on_open_image, width=12
        )
        self.btn_open.pack(side=tk.LEFT, padx=(0, 8))

        ttk.Label(toolbar, text="크기(px):").pack(side=tk.LEFT)
        self.var_width = tk.StringVar(value="64")
        self.var_height = tk.StringVar(value="64")
        ttk.Entry(toolbar, textvariable=self.var_width, width=5).pack(
            side=tk.LEFT, padx=(4, 2)
        )
        ttk.Label(toolbar, text="×").pack(side=tk.LEFT)
        ttk.Entry(toolbar, textvariable=self.var_height, width=5).pack(
            side=tk.LEFT, padx=(2, 8)
        )

        ttk.Label(toolbar, text="비율:").pack(side=tk.LEFT)
        self.var_mode = tk.StringVar(value="fit")
        ttk.Combobox(
            toolbar,
            textvariable=self.var_mode,
            values=RESIZE_MODES,
            state="readonly",
            width=7,
        ).pack(side=tk.LEFT, padx=(4, 8))

        ttk.Label(toolbar, text="알고리즘:").pack(side=tk.LEFT)
        self.var_metric = tk.StringVar(value="ciede2000")
        ttk.Combobox(
            toolbar,
            textvariable=self.var_metric,
            values=SUPPORTED_METRICS,
            state="readonly",
            width=10,
        ).pack(side=tk.LEFT, padx=(4, 8))

        ttk.Label(toolbar, text="디더링:").pack(side=tk.LEFT)
        self.var_dither = tk.StringVar(value="none")
        ttk.Combobox(
            toolbar,
            textvariable=self.var_dither,
            values=DITHER_OPTIONS,
            state="readonly",
            width=13,
        ).pack(side=tk.LEFT, padx=(4, 8))

        self.var_grid = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            toolbar,
            text="격자",
            variable=self.var_grid,
            command=self._render_converted_preview,
        ).pack(side=tk.LEFT, padx=(0, 8))

        self.btn_convert = ttk.Button(
            toolbar,
            text="이미지 변환",
            command=self.on_convert_image,
            width=12,
            state=tk.DISABLED,
        )
        self.btn_convert.pack(side=tk.LEFT, padx=(0, 6))

        self.btn_save = ttk.Button(
            toolbar,
            text="결과 저장",
            command=self.on_save_result,
            width=10,
            state=tk.DISABLED,
        )
        self.btn_save.pack(side=tk.LEFT)

        # --- Image viewer pane (Original | Converted) ---
        viewer = ttk.Frame(self.root, padding=(10, 4))
        viewer.pack(fill=tk.BOTH, expand=True)
        viewer.columnconfigure(0, weight=1)
        viewer.columnconfigure(1, weight=1)
        viewer.rowconfigure(1, weight=1)

        self.lbl_orig_title = ttk.Label(
            viewer, text="원본 이미지", font=("Malgun Gothic", 10, "bold")
        )
        self.lbl_orig_title.grid(row=0, column=0, sticky=tk.W, pady=(0, 4))

        self.lbl_conv_title = ttk.Label(
            viewer, text="변환 결과 (마우스를 올려 아이템 확인)", font=("Malgun Gothic", 10, "bold")
        )
        self.lbl_conv_title.grid(row=0, column=1, sticky=tk.W, padx=(8, 0), pady=(0, 4))

        self.canvas_orig = tk.Canvas(
            viewer, bg="#1E1E22", highlightthickness=1, highlightbackground="#3A3A44"
        )
        self.canvas_orig.grid(row=1, column=0, sticky=tk.NSEW, padx=(0, 4))

        self.canvas_conv = tk.Canvas(
            viewer, bg="#1E1E22", highlightthickness=1, highlightbackground="#3A3A44"
        )
        self.canvas_conv.grid(row=1, column=1, sticky=tk.NSEW, padx=(4, 0))

        self.canvas_orig.bind("<Configure>", lambda _e: self._render_original_preview())
        self.canvas_conv.bind("<Configure>", lambda _e: self._render_converted_preview())
        self.canvas_conv.bind("<Motion>", self._on_hover_converted)

        # --- Bottom status bar ---
        status_frame = ttk.Frame(self.root, padding=(10, 6))
        status_frame.pack(fill=tk.X)

        self.var_status = tk.StringVar(
            value=f"팔레트 로드 완료: {len(self.palette.item_names)}개 아이템 ({len(self.palette)}색) — '이미지 삽입' 버튼을 눌러 시작하세요."
        )
        ttk.Label(status_frame, textvariable=self.var_status).pack(side=tk.LEFT)

        self.var_hover = tk.StringVar(value="")
        ttk.Label(status_frame, textvariable=self.var_hover, foreground="#1A65B8").pack(
            side=tk.RIGHT
        )

    def on_open_image(self) -> None:
        filepath = filedialog.askopenfilename(
            title="변환할 이미지 선택",
            filetypes=[
                ("이미지 파일", "*.png *.jpg *.jpeg *.bmp *.webp *.gif"),
                ("모든 파일", "*.*"),
            ],
        )
        if not filepath:
            return

        try:
            img = Image.open(filepath)
            img.load()
        except Exception as exc:
            messagebox.showerror("이미지 로드 실패", f"이미지를 열 수 없습니다:\n{exc}")
            return

        self.source_path = Path(filepath)
        self.source_image = img
        self.result = None
        self._conv_box = None
        self.canvas_conv.delete("all")
        self.btn_convert.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.DISABLED)

        w, h = img.size
        self.lbl_orig_title.config(text=f"원본 이미지 ({self.source_path.name} — {w}×{h})")
        self.lbl_conv_title.config(text="변환 결과 (마우스를 올려 아이템 확인)")
        self.var_hover.set("")
        self.var_status.set(f"불러옴: {self.source_path.name} ({w}×{h}) — '이미지 변환'을 누르세요.")
        self._render_original_preview()

    def on_convert_image(self) -> None:
        if self.source_image is None:
            messagebox.showwarning("이미지 없음", "먼저 '이미지 삽입'으로 이미지를 불러오세요.")
            return

        try:
            w_str = self.var_width.get().strip()
            h_str = self.var_height.get().strip()
            width = int(w_str) if w_str else None
            height = int(h_str) if h_str else None
            if (width is not None and width <= 0) or (height is not None and height <= 0):
                raise ValueError("크기는 1 이상의 정수여야 합니다.")
        except ValueError as exc:
            messagebox.showerror("잘못된 크기 입력", f"픽셀 크기를 올바르게 입력하세요.\n({exc})")
            return

        metric = self.var_metric.get()
        dithering = self.var_dither.get()
        mode = self.var_mode.get()
        src_copy = self.source_image.copy()

        self.btn_convert.config(state=tk.DISABLED)
        self.btn_open.config(state=tk.DISABLED)
        self.var_status.set(f"변환 중... (알고리즘: {metric}, 디더링: {dithering})")

        def _worker() -> None:
            t0 = time.perf_counter()
            try:
                res = convert_image(
                    src_copy,
                    self.palette,
                    width=width,
                    height=height,
                    mode=mode,  # type: ignore[arg-type]
                    metric=metric,  # type: ignore[arg-type]
                    dithering=dithering,  # type: ignore[arg-type]
                )
                elapsed_ms = (time.perf_counter() - t0) * 1000.0
            except Exception as exc:
                self.root.after(0, lambda e=exc: self._on_convert_error(e))
                return
            self.root.after(0, lambda: self._on_convert_done(res, elapsed_ms))

        threading.Thread(target=_worker, daemon=True).start()

    def _on_convert_error(self, exc: Exception) -> None:
        self.btn_convert.config(state=tk.NORMAL)
        self.btn_open.config(state=tk.NORMAL)
        self.var_status.set("변환 실패")
        messagebox.showerror("변환 오류", str(exc))

    def _on_convert_done(self, res: ConversionResult, elapsed_ms: float) -> None:
        self.result = res
        self.btn_convert.config(state=tk.NORMAL)
        self.btn_open.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.NORMAL)

        used_items = len(res.item_summary())
        self.lbl_conv_title.config(
            text=f"변환 결과 ({res.width}×{res.height}px | {res.metric.upper()} | {used_items}종 아이템)"
        )
        self.var_status.set(
            f"변환 완료 ({elapsed_ms:.0f}ms) | 평균 ΔE00={res.mean_delta_e2000:.2f} "
            f"| 상위95% ΔE00={res.p95_delta_e2000:.2f} | 사용 아이템 {used_items}종"
        )
        self._render_converted_preview()

    def on_save_result(self) -> None:
        if self.result is None:
            return
        default_name = (
            f"{self.source_path.stem}_{self.result.width}x{self.result.height}_{self.result.metric}.png"
            if self.source_path
            else "converted.png"
        )
        save_path = filedialog.asksaveasfilename(
            title="변환 이미지 저장",
            defaultextension=".png",
            initialfile=default_name,
            filetypes=[("PNG 이미지", "*.png")],
        )
        if not save_path:
            return
        self.result.to_image(scale=1).save(save_path)
        self.var_status.set(f"저장 완료: {save_path} ({self.result.width}×{self.result.height}px)")

    def _render_original_preview(self) -> None:
        self.canvas_orig.delete("all")
        if self.source_image is None:
            return

        cw = max(1, self.canvas_orig.winfo_width())
        ch = max(1, self.canvas_orig.winfo_height())
        if cw < 10 or ch < 10:
            return

        img = self.source_image.convert("RGB")
        scale = min((cw - 16) / img.width, (ch - 16) / img.height)
        disp_w = max(1, int(img.width * scale))
        disp_h = max(1, int(img.height * scale))
        resample = (
            Image.Resampling.NEAREST if scale >= 2.0 else Image.Resampling.LANCZOS
        )
        preview = img.resize((disp_w, disp_h), resample=resample)

        self._orig_photo = ImageTk.PhotoImage(preview)
        self.canvas_orig.create_image(cw // 2, ch // 2, image=self._orig_photo, anchor=tk.CENTER)

    def _render_converted_preview(self) -> None:
        self.canvas_conv.delete("all")
        self._conv_box = None
        if self.result is None:
            return

        cw = max(1, self.canvas_conv.winfo_width())
        ch = max(1, self.canvas_conv.winfo_height())
        if cw < 10 or ch < 10:
            return

        # Compute largest crisp integer or fitted display size
        scale_f = min((cw - 16) / self.result.width, (ch - 16) / self.result.height)
        if scale_f >= 1.0:
            int_scale = max(1, int(scale_f))
            preview = self.result.to_image(scale=int_scale, grid=self.var_grid.get())
        else:
            disp_w = max(1, int(self.result.width * scale_f))
            disp_h = max(1, int(self.result.height * scale_f))
            preview = self.result.to_image(scale=1).resize(
                (disp_w, disp_h), resample=Image.Resampling.NEAREST
            )

        pw, ph = preview.size
        x0 = (cw - pw) // 2
        y0 = (ch - ph) // 2
        self._conv_box = (x0, y0, pw, ph)

        self._conv_photo = ImageTk.PhotoImage(preview)
        self.canvas_conv.create_image(x0, y0, image=self._conv_photo, anchor=tk.NW)

    def _on_hover_converted(self, event: tk.Event) -> None:
        if self.result is None or self._conv_box is None:
            return
        x0, y0, pw, ph = self._conv_box
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            self.var_hover.set("")
            return

        px = min(self.result.width - 1, int(rx * self.result.width / pw))
        py = min(self.result.height - 1, int(ry * self.result.height / ph))
        idx = int(self.result.palette_indices[py, px])
        if idx < 0:
            self.var_hover.set(f"({px}, {py}) 투명")
            return

        color = self.palette[idx]
        de = float(self.result.ciede2000_errors[py, px])
        self.var_hover.set(
            f"({px}, {py}) {color.display_label} | {color.hex_code} rgb{color.rgb} | ΔE00={de:.2f}"
        )


def main() -> None:
    root = tk.Tk()
    DrawingHelperApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
