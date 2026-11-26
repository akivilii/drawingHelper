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

from PIL import Image, ImageDraw, ImageTk

from drawing_helper import (
    ConversionResult,
    Palette,
    SUPPORTED_METRICS,
    convert_image,
)

DEFAULT_PALETTE_PATH = Path(__file__).resolve().parent / "minecraft_palette_rgb.md"
DITHER_OPTIONS = ("none", "bayer4", "bayer8", "floyd_steinberg")
RESIZE_MODES = ("fit", "stretch", "cover")
TILE_SIZE = 32


class DrawingHelperApp:
    """Minimal Python test UI: load image, convert to Minecraft palette, inspect 32x32 tiles."""

    def __init__(self, root: tk.Tk, palette_path: Path = DEFAULT_PALETTE_PATH) -> None:
        self.root = root
        self.root.title("Minecraft Drawing Helper - 색상 매칭 테스트 UI")
        self.root.geometry("1180x720")
        self.root.minsize(880, 560)

        self.palette = Palette.from_markdown(palette_path)
        self.source_path: Path | None = None
        self.source_image: Image.Image | None = None
        self.result: ConversionResult | None = None

        # Selected 32x32 tile (col, row) 0-based; None means full-image overview
        self.selected_tile: tuple[int, int] | None = None
        self._tile_list: list[tuple[int, int]] = []

        # Keep Tk PhotoImage references alive
        self._orig_photo: ImageTk.PhotoImage | None = None
        self._conv_photo: ImageTk.PhotoImage | None = None

        # Display geometries inside canvases: (x0, y0, disp_w, disp_h)
        self._orig_box: tuple[int, int, int, int] | None = None
        # Converted canvas geometry + source pixel bounds: (x0, y0, disp_w, disp_h, src_x0, src_y0, src_w, src_h)
        self._conv_view: tuple[int, int, int, int, int, int, int, int] | None = None

        self._build_ui()
        self._bind_shortcuts()

    def _build_ui(self) -> None:
        # --- Row 1: Main toolbar (Load, size, algorithm, convert, save) ---
        toolbar = ttk.Frame(self.root, padding=(10, 8, 10, 4))
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

        # --- Row 2: 32x32 tile zoom navigation bar ---
        tilebar = ttk.Frame(self.root, padding=(10, 2, 10, 6))
        tilebar.pack(fill=tk.X)

        ttk.Label(tilebar, text=f"{TILE_SIZE}×{TILE_SIZE} 칸 보기:").pack(side=tk.LEFT)

        self.btn_full_view = ttk.Button(
            tilebar,
            text="전체 보기",
            command=self.show_full_view,
            width=9,
            state=tk.DISABLED,
        )
        self.btn_full_view.pack(side=tk.LEFT, padx=(6, 4))

        self.btn_prev_tile = ttk.Button(
            tilebar, text="◀", command=lambda: self.step_tile(-1), width=3, state=tk.DISABLED
        )
        self.btn_prev_tile.pack(side=tk.LEFT)

        self.var_tile_select = tk.StringVar(value="전체 보기")
        self.cmb_tile = ttk.Combobox(
            tilebar,
            textvariable=self.var_tile_select,
            values=("전체 보기",),
            state="disabled",
            width=26,
        )
        self.cmb_tile.pack(side=tk.LEFT, padx=4)
        self.cmb_tile.bind("<<ComboboxSelected>>", self._on_tile_combo_selected)

        self.btn_next_tile = ttk.Button(
            tilebar, text="▶", command=lambda: self.step_tile(1), width=3, state=tk.DISABLED
        )
        self.btn_next_tile.pack(side=tk.LEFT, padx=(0, 10))

        self.var_tile_lines = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            tilebar,
            text=f"{TILE_SIZE}×{TILE_SIZE} 경계선",
            variable=self.var_tile_lines,
            command=self._render_all_previews,
        ).pack(side=tk.LEFT, padx=(0, 8))

        self.var_pixel_grid = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            tilebar,
            text="확대 픽셀 격자",
            variable=self.var_pixel_grid,
            command=self._render_converted_preview,
        ).pack(side=tk.LEFT, padx=(0, 10))

        self.btn_tile_items = ttk.Button(
            tilebar,
            text="아이템 목록",
            command=self.on_show_item_summary,
            width=11,
            state=tk.DISABLED,
        )
        self.btn_tile_items.pack(side=tk.LEFT)

        ttk.Label(
            tilebar,
            text="(이미지 클릭: 칸 확대 / 우클릭·Esc: 전체 보기 / 방향키: 칸 이동)",
            foreground="#666666",
        ).pack(side=tk.RIGHT)

        # --- Image viewer pane (Original | Converted) ---
        viewer = ttk.Frame(self.root, padding=(10, 2))
        viewer.pack(fill=tk.BOTH, expand=True)
        viewer.columnconfigure(0, weight=1)
        viewer.columnconfigure(1, weight=1)
        viewer.rowconfigure(1, weight=1)

        self.lbl_orig_title = ttk.Label(
            viewer, text="원본 이미지", font=("Malgun Gothic", 10, "bold")
        )
        self.lbl_orig_title.grid(row=0, column=0, sticky=tk.W, pady=(0, 4))

        self.lbl_conv_title = ttk.Label(
            viewer,
            text="변환 결과 (클릭하여 32×32 칸 확대 / 마우스 올려 아이템 확인)",
            font=("Malgun Gothic", 10, "bold"),
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
        self.canvas_conv.bind("<Button-1>", self._on_click_converted)
        self.canvas_conv.bind("<Button-3>", lambda _e: self.show_full_view())
        self.canvas_orig.bind("<Button-1>", self._on_click_original)

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

    def _bind_shortcuts(self) -> None:
        self.root.bind("<Escape>", lambda _e: self.show_full_view())
        self.root.bind("<Left>", lambda _e: self._move_tile_direction(-1, 0))
        self.root.bind("<Right>", lambda _e: self._move_tile_direction(1, 0))
        self.root.bind("<Up>", lambda _e: self._move_tile_direction(0, -1))
        self.root.bind("<Down>", lambda _e: self._move_tile_direction(0, 1))

    # --- Actions ---
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
        self.selected_tile = None
        self._tile_list = []
        self._conv_view = None
        self.canvas_conv.delete("all")

        self.btn_convert.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.DISABLED)
        self._set_tile_controls_enabled(False)

        w, h = img.size
        self.lbl_orig_title.config(text=f"원본 이미지 ({self.source_path.name} — {w}×{h})")
        self.lbl_conv_title.config(
            text="변환 결과 (클릭하여 32×32 칸 확대 / 마우스 올려 아이템 확인)"
        )
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
        self.selected_tile = None
        self.btn_convert.config(state=tk.NORMAL)
        self.btn_open.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.NORMAL)

        # Build 32x32 tile list
        cols, rows = res.tile_grid_shape(TILE_SIZE)
        self._tile_list = [(c, r) for r in range(rows) for c in range(cols)]
        combo_values = ["전체 보기"] + [self._format_tile_label(c, r) for c, r in self._tile_list]
        self.cmb_tile.config(values=combo_values)
        self.var_tile_select.set("전체 보기")
        self._set_tile_controls_enabled(True)

        used_items = len(res.item_summary())
        self.var_status.set(
            f"변환 완료 ({elapsed_ms:.0f}ms) | {res.width}×{res.height}px ({cols}×{rows}칸) "
            f"| 평균 ΔE00={res.mean_delta_e2000:.2f} | 상위95% ΔE00={res.p95_delta_e2000:.2f} "
            f"| 사용 아이템 {used_items}종"
        )
        self._update_conv_title()
        self._render_all_previews()

    def _set_tile_controls_enabled(self, enabled: bool) -> None:
        state = tk.NORMAL if enabled else tk.DISABLED
        self.btn_full_view.config(state=state)
        self.btn_prev_tile.config(state=state)
        self.btn_next_tile.config(state=state)
        self.btn_tile_items.config(state=state)
        self.cmb_tile.config(state="readonly" if enabled else "disabled")
        if not enabled:
            self.cmb_tile.config(values=("전체 보기",))
            self.var_tile_select.set("전체 보기")

    def _format_tile_label(self, col: int, row: int) -> str:
        assert self.result is not None
        x0 = col * TILE_SIZE
        y0 = row * TILE_SIZE
        x1 = min(x0 + TILE_SIZE, self.result.width) - 1
        y1 = min(y0 + TILE_SIZE, self.result.height) - 1
        return f"칸 ({col + 1}, {row + 1}) [x:{x0}~{x1}, y:{y0}~{y1}]"

    # --- Tile Navigation ---
    def select_tile(self, tile: tuple[int, int] | None) -> None:
        if self.result is None:
            return
        self.selected_tile = tile
        if tile is None:
            self.var_tile_select.set("전체 보기")
        else:
            self.var_tile_select.set(self._format_tile_label(tile[0], tile[1]))
        self._update_conv_title()
        self._render_all_previews()

    def show_full_view(self) -> None:
        if self.result is not None and self.selected_tile is not None:
            self.select_tile(None)

    def step_tile(self, delta: int) -> None:
        if self.result is None or not self._tile_list:
            return
        if self.selected_tile is None:
            self.select_tile(self._tile_list[0] if delta > 0 else self._tile_list[-1])
            return
        idx = self._tile_list.index(self.selected_tile)
        new_idx = (idx + delta) % len(self._tile_list)
        self.select_tile(self._tile_list[new_idx])

    def _move_tile_direction(self, dx: int, dy: int) -> None:
        if self.result is None or self.selected_tile is None:
            return
        # Ignore arrow keys while typing inside an Entry widget
        if isinstance(self.root.focus_get(), (ttk.Entry, tk.Entry)):
            return
        cols, rows = self.result.tile_grid_shape(TILE_SIZE)
        tc, tr = self.selected_tile
        nc = max(0, min(cols - 1, tc + dx))
        nr = max(0, min(rows - 1, tr + dy))
        if (nc, nr) != (tc, tr):
            self.select_tile((nc, nr))

    def _on_tile_combo_selected(self, _event: tk.Event) -> None:
        if self.result is None:
            return
        idx = self.cmb_tile.current()
        if idx <= 0:
            self.select_tile(None)
        elif idx - 1 < len(self._tile_list):
            self.select_tile(self._tile_list[idx - 1])

    def _update_conv_title(self) -> None:
        if self.result is None:
            return
        cols, rows = self.result.tile_grid_shape(TILE_SIZE)
        if self.selected_tile is None:
            used = len(self.result.item_summary())
            self.lbl_conv_title.config(
                text=(
                    f"변환 결과 전체 ({self.result.width}×{self.result.height}px | "
                    f"{cols}×{rows}칸 | {self.result.metric.upper()} | {used}종 아이템)"
                )
            )
        else:
            tc, tr = self.selected_tile
            sub, (x0, y0, x1, y1) = self.result.slice_tile(tc, tr, TILE_SIZE)
            used = len(sub.item_summary())
            self.lbl_conv_title.config(
                text=(
                    f"확대: 칸 ({tc + 1}, {tr + 1}) [{sub.width}×{sub.height}px | "
                    f"x:{x0}~{x1 - 1}, y:{y0}~{y1 - 1} | {used}종 아이템]"
                )
            )

    def on_show_item_summary(self) -> None:
        if self.result is None:
            return
        if self.selected_tile is None:
            target = self.result
            title = f"전체 아이템 목록 ({target.width}×{target.height}px)"
        else:
            tc, tr = self.selected_tile
            target, _ = self.result.slice_tile(tc, tr, TILE_SIZE)
            title = f"칸 ({tc + 1}, {tr + 1}) 아이템 목록 ({target.width}×{target.height}px)"

        win = tk.Toplevel(self.root)
        win.title(title)
        win.geometry("520x440")
        win.transient(self.root)

        tree = ttk.Treeview(
            win, columns=("item", "total", "shades"), show="headings", height=18
        )
        tree.heading("item", text="아이템")
        tree.heading("total", text="총 픽셀")
        tree.heading("shades", text="단계별 개수")
        tree.column("item", width=230)
        tree.column("total", width=70, anchor=tk.E)
        tree.column("shades", width=190)

        sb = ttk.Scrollbar(win, orient=tk.VERTICAL, command=tree.yview)
        tree.configure(yscrollcommand=sb.set)
        tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(8, 0), pady=8)
        sb.pack(side=tk.RIGHT, fill=tk.Y, padx=(0, 8), pady=8)

        for row in target.item_summary():
            shades_str = ", ".join(f"{k}단계:{v}" for k, v in row["shades"].items())
            tree.insert("", tk.END, values=(row["item_name"], row["total_pixels"], shades_str))

    def on_save_result(self) -> None:
        if self.result is None:
            return

        if self.selected_tile is not None:
            tc, tr = self.selected_tile
            save_tile = messagebox.askyesnocancel(
                "저장 범위 선택",
                f"현재 확대 중인 칸 ({tc + 1}, {tr + 1})만 저장할까요?\n\n"
                "• 예(Yes): 현재 32×32 칸만 저장\n"
                "• 아니오(No): 전체 이미지 저장",
            )
            if save_tile is None:
                return
            if save_tile:
                sub, _ = self.result.slice_tile(tc, tr, TILE_SIZE)
                stem = self.source_path.stem if self.source_path else "converted"
                default_name = f"{stem}_tile_{tc + 1}_{tr + 1}_{sub.width}x{sub.height}.png"
                target_img = sub.to_image(scale=1)
            else:
                stem = self.source_path.stem if self.source_path else "converted"
                default_name = f"{stem}_{self.result.width}x{self.result.height}_{self.result.metric}.png"
                target_img = self.result.to_image(scale=1)
        else:
            stem = self.source_path.stem if self.source_path else "converted"
            default_name = f"{stem}_{self.result.width}x{self.result.height}_{self.result.metric}.png"
            target_img = self.result.to_image(scale=1)

        save_path = filedialog.asksaveasfilename(
            title="변환 이미지 저장",
            defaultextension=".png",
            initialfile=default_name,
            filetypes=[("PNG 이미지", "*.png")],
        )
        if not save_path:
            return
        target_img.save(save_path)
        self.var_status.set(f"저장 완료: {save_path} ({target_img.width}×{target_img.height}px)")

    # --- Rendering ---
    def _render_all_previews(self) -> None:
        self._render_original_preview()
        self._render_converted_preview()

    def _render_original_preview(self) -> None:
        self.canvas_orig.delete("all")
        self._orig_box = None
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

        x0 = (cw - disp_w) // 2
        y0 = (ch - disp_h) // 2
        self._orig_box = (x0, y0, disp_w, disp_h)

        self._orig_photo = ImageTk.PhotoImage(preview)
        self.canvas_orig.create_image(x0, y0, image=self._orig_photo, anchor=tk.NW)

        # Draw 32x32 overlay & active tile highlight on original when converted result exists
        if self.result is not None:
            rw, rh = self.result.width, self.result.height
            if self.var_tile_lines.get():
                for gx in range(TILE_SIZE, rw, TILE_SIZE):
                    lx = x0 + gx * disp_w / rw
                    self.canvas_orig.create_line(lx, y0, lx, y0 + disp_h, fill="#FFD43B", dash=(3, 3))
                for gy in range(TILE_SIZE, rh, TILE_SIZE):
                    ly = y0 + gy * disp_h / rh
                    self.canvas_orig.create_line(x0, ly, x0 + disp_w, ly, fill="#FFD43B", dash=(3, 3))

            if self.selected_tile is not None:
                tc, tr = self.selected_tile
                _, (tx0, ty0, tx1, ty1) = self.result.slice_tile(tc, tr, TILE_SIZE)
                rx0 = x0 + tx0 * disp_w / rw
                ry0 = y0 + ty0 * disp_h / rh
                rx1 = x0 + tx1 * disp_w / rw
                ry1 = y0 + ty1 * disp_h / rh
                self.canvas_orig.create_rectangle(
                    rx0, ry0, rx1, ry1, outline="#FFEA00", width=3
                )

    def _render_converted_preview(self) -> None:
        self.canvas_conv.delete("all")
        self._conv_view = None
        if self.result is None:
            return

        cw = max(1, self.canvas_conv.winfo_width())
        ch = max(1, self.canvas_conv.winfo_height())
        if cw < 10 or ch < 10:
            return

        if self.selected_tile is None:
            self._render_full_converted(cw, ch)
        else:
            self._render_zoomed_tile(cw, ch, self.selected_tile[0], self.selected_tile[1])

    def _render_full_converted(self, cw: int, ch: int) -> None:
        assert self.result is not None
        res = self.result
        scale_f = min((cw - 16) / res.width, (ch - 16) / res.height)
        if scale_f >= 1.0:
            int_scale = max(1, int(scale_f))
            preview = res.to_image(scale=int_scale, grid=False)
        else:
            disp_w = max(1, int(res.width * scale_f))
            disp_h = max(1, int(res.height * scale_f))
            preview = res.to_image(scale=1).resize(
                (disp_w, disp_h), resample=Image.Resampling.NEAREST
            )

        pw, ph = preview.size
        x0 = (cw - pw) // 2
        y0 = (ch - ph) // 2
        self._conv_view = (x0, y0, pw, ph, 0, 0, res.width, res.height)

        self._conv_photo = ImageTk.PhotoImage(preview)
        self.canvas_conv.create_image(x0, y0, image=self._conv_photo, anchor=tk.NW)

        if self.var_tile_lines.get():
            cols, rows = res.tile_grid_shape(TILE_SIZE)
            for gx in range(TILE_SIZE, res.width, TILE_SIZE):
                lx = x0 + gx * pw / res.width
                self.canvas_conv.create_line(lx, y0, lx, y0 + ph, fill="#FFD43B", width=2)
            for gy in range(TILE_SIZE, res.height, TILE_SIZE):
                ly = y0 + gy * ph / res.height
                self.canvas_conv.create_line(x0, ly, x0 + pw, ly, fill="#FFD43B", width=2)

            # Show compact tile coordinate badges in each 32x32 cell
            for tr in range(rows):
                for tc in range(cols):
                    bx = x0 + (tc * TILE_SIZE) * pw / res.width + 4
                    by = y0 + (tr * TILE_SIZE) * ph / res.height + 4
                    label = f"{tc + 1},{tr + 1}"
                    self.canvas_conv.create_rectangle(
                        bx - 2, by - 1, bx + 24, by + 13, fill="#111114", outline="#FFD43B"
                    )
                    self.canvas_conv.create_text(
                        bx + 11,
                        by + 6,
                        text=label,
                        fill="#FFD43B",
                        font=("Consolas", 8, "bold"),
                    )

    def _render_zoomed_tile(self, cw: int, ch: int, tc: int, tr: int) -> None:
        assert self.result is not None
        sub, (tx0, ty0, _tx1, _ty1) = self.result.slice_tile(tc, tr, TILE_SIZE)

        ruler_pad = 24
        avail_w = max(32, cw - ruler_pad - 16)
        avail_h = max(32, ch - ruler_pad - 16)
        cell_px = max(1, int(min(avail_w / sub.width, avail_h / sub.height)))

        preview = sub.to_image(scale=cell_px, grid=False)
        if self.var_pixel_grid.get() and cell_px >= 4:
            draw = ImageDraw.Draw(preview)
            pw, ph = preview.size
            for x in range(sub.width + 1):
                px = min(x * cell_px, pw - 1)
                color = (255, 220, 80) if (x % 8 == 0 and 0 < x < sub.width) else (55, 55, 62)
                draw.line([(px, 0), (px, ph - 1)], fill=color, width=1)
            for y in range(sub.height + 1):
                py = min(y * cell_px, ph - 1)
                color = (255, 220, 80) if (y % 8 == 0 and 0 < y < sub.height) else (55, 55, 62)
                draw.line([(0, py), (pw - 1, py)], fill=color, width=1)

        pw, ph = preview.size
        x0 = ruler_pad + (cw - ruler_pad - pw) // 2
        y0 = ruler_pad + (ch - ruler_pad - ph) // 2
        self._conv_view = (x0, y0, pw, ph, tx0, ty0, sub.width, sub.height)

        self._conv_photo = ImageTk.PhotoImage(preview)
        self.canvas_conv.create_image(x0, y0, image=self._conv_photo, anchor=tk.NW)
        self.canvas_conv.create_rectangle(
            x0 - 1, y0 - 1, x0 + pw, y0 + ph, outline="#FFD43B", width=2
        )

        # Draw 1-based coordinate rulers (every 4 pixels, or every pixel when large)
        step = 1 if cell_px >= 18 else (4 if cell_px >= 8 else 8)
        for lx in range(0, sub.width, step):
            cx = x0 + (lx + 0.5) * cell_px
            self.canvas_conv.create_text(
                cx,
                y0 - 10,
                text=str(lx + 1),
                fill="#D0D0D8",
                font=("Consolas", 8),
            )
        for ly in range(0, sub.height, step):
            cy = y0 + (ly + 0.5) * cell_px
            self.canvas_conv.create_text(
                x0 - 12,
                cy,
                text=str(ly + 1),
                fill="#D0D0D8",
                font=("Consolas", 8),
            )

    # --- Mouse Interaction ---
    def _on_click_converted(self, event: tk.Event) -> None:
        if self.result is None or self._conv_view is None:
            return
        # In full overview, clicking a pixel zooms directly into its 32x32 tile
        if self.selected_tile is not None:
            return
        x0, y0, pw, ph, src_x0, src_y0, src_w, src_h = self._conv_view
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            return
        gx = src_x0 + min(src_w - 1, int(rx * src_w / pw))
        gy = src_y0 + min(src_h - 1, int(ry * src_h / ph))
        self.select_tile((gx // TILE_SIZE, gy // TILE_SIZE))

    def _on_click_original(self, event: tk.Event) -> None:
        if self.result is None or self._orig_box is None:
            return
        x0, y0, pw, ph = self._orig_box
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            return
        gx = min(self.result.width - 1, int(rx * self.result.width / pw))
        gy = min(self.result.height - 1, int(ry * self.result.height / ph))
        self.select_tile((gx // TILE_SIZE, gy // TILE_SIZE))

    def _on_hover_converted(self, event: tk.Event) -> None:
        if self.result is None or self._conv_view is None:
            return
        x0, y0, pw, ph, src_x0, src_y0, src_w, src_h = self._conv_view
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            self.var_hover.set("")
            return

        lx = min(src_w - 1, int(rx * src_w / pw))
        ly = min(src_h - 1, int(ry * src_h / ph))
        gx = src_x0 + lx
        gy = src_y0 + ly

        tc, tr = gx // TILE_SIZE, gy // TILE_SIZE
        in_tile_x, in_tile_y = (gx % TILE_SIZE) + 1, (gy % TILE_SIZE) + 1

        idx = int(self.result.palette_indices[gy, gx])
        if idx < 0:
            self.var_hover.set(
                f"칸({tc + 1},{tr + 1}) 내부({in_tile_x},{in_tile_y}) / 전체({gx},{gy}) | 투명"
            )
            return

        color = self.palette[idx]
        de = float(self.result.ciede2000_errors[gy, gx])
        self.var_hover.set(
            f"칸({tc + 1},{tr + 1}) 내부({in_tile_x},{in_tile_y}) / 전체({gx},{gy}) | "
            f"{color.display_label} | {color.hex_code} rgb{color.rgb} | ΔE00={de:.2f}"
        )


def main() -> None:
    root = tk.Tk()
    DrawingHelperApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
