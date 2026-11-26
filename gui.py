"""Interactive Tkinter prototype UI for Minecraft Drawing Helper.

Includes a 2-tab workflow designed for Split View alongside Minecraft:
1. '변환 설정' tab: load image, configure size/metric/dithering, preview full result.
2. '32×32 그리기' tab: vertical split-view layout with large 32x32 tile canvas,
   per-color / per-item highlight isolation, crosshair guides, completion checklists,
   Always-On-Top (Topmost), opacity control, and progress save/load.
"""

from __future__ import annotations

import json
from pathlib import Path
import threading
import time
import tkinter as tk
from tkinter import filedialog, messagebox, ttk

import numpy as np
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
    """Prototype Python UI testing split-view Minecraft painting ergonomics before C# port."""

    def __init__(self, root: tk.Tk, palette_path: Path = DEFAULT_PALETTE_PATH) -> None:
        self.root = root
        self.root.title("Minecraft Drawing Helper - 스플릿 뷰 테스트 UI")
        self.root.geometry("1160x760")
        self.root.minsize(460, 600)

        self.palette = Palette.from_markdown(palette_path)
        self.source_path: Path | None = None
        self.source_image: Image.Image | None = None
        self.result: ConversionResult | None = None

        # 32x32 tile state
        self.selected_tile: tuple[int, int] = (0, 0)
        self._tile_list: list[tuple[int, int]] = []

        # Highlight & progress tracking state
        # highlighted_palette_idx: exact color (item + shade), or None
        self.highlighted_palette_idx: int | None = None
        # highlighted_item_name: whole item across all shades, or None
        self.highlighted_item_name: str | None = None
        self.completed_tiles: set[tuple[int, int]] = set()
        # Maps (tile_col, tile_row) -> set of completed palette_indices
        self.completed_colors: dict[tuple[int, int], set[int]] = {}
        self._hover_tile_pixel: tuple[int, int] | None = None

        # Tk image reference caches
        self._orig_photo: ImageTk.PhotoImage | None = None
        self._overview_photo: ImageTk.PhotoImage | None = None
        self._paint_photo: ImageTk.PhotoImage | None = None
        self._swatch_cache: dict[str, tk.PhotoImage] = {}
        self._tree_row_to_pidx: dict[str, int] = {}

        # Canvas display bounds for mouse mapping
        self._overview_box: tuple[int, int, int, int] | None = None
        self._paint_view: tuple[int, int, int, int, int, int, int, int, int] | None = None
        self._is_compact_window = False
        self._saved_wide_geometry = "1160x760"

        self._build_ui()
        self._bind_shortcuts()

    # ------------------------------------------------------------------
    # UI Construction
    # ------------------------------------------------------------------
    def _build_ui(self) -> None:
        # --- Global top utility bar (window mode, topmost, opacity) ---
        topbar = ttk.Frame(self.root, padding=(8, 6, 8, 2))
        topbar.pack(fill=tk.X)

        self.btn_compact = ttk.Button(
            topbar,
            text="스플릿 뷰 크기",
            command=self.toggle_compact_window,
            width=13,
        )
        self.btn_compact.pack(side=tk.LEFT, padx=(0, 8))

        self.var_topmost = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            topbar,
            text="📌 항상 위",
            variable=self.var_topmost,
            command=self._apply_topmost,
        ).pack(side=tk.LEFT, padx=(0, 8))

        ttk.Label(topbar, text="투명도:").pack(side=tk.LEFT)
        self.var_alpha = tk.DoubleVar(value=1.0)
        ttk.Scale(
            topbar,
            from_=0.35,
            to=1.0,
            variable=self.var_alpha,
            command=self._apply_alpha,
            length=85,
        ).pack(side=tk.LEFT, padx=(4, 4))
        self.lbl_alpha = ttk.Label(topbar, text="100%", width=4)
        self.lbl_alpha.pack(side=tk.LEFT, padx=(0, 10))

        self.btn_save_prog = ttk.Button(
            topbar,
            text="진행 저장",
            command=self.on_save_progress,
            width=9,
            state=tk.DISABLED,
        )
        self.btn_save_prog.pack(side=tk.RIGHT, padx=(4, 0))

        self.btn_load_prog = ttk.Button(
            topbar,
            text="진행 불러오기",
            command=self.on_load_progress,
            width=11,
            state=tk.DISABLED,
        )
        self.btn_load_prog.pack(side=tk.RIGHT)

        # --- Main 2-tab Notebook ---
        self.notebook = ttk.Notebook(self.root)
        self.notebook.pack(fill=tk.BOTH, expand=True, padx=6, pady=(2, 4))

        self.tab_setup = ttk.Frame(self.notebook)
        self.tab_paint = ttk.Frame(self.notebook)
        self.notebook.add(self.tab_setup, text="  1. 변환 설정 / 전체 보기  ")
        self.notebook.add(self.tab_paint, text="  2. 32×32 그리기 (스플릿 뷰)  ")
        self.notebook.bind("<<NotebookTabChanged>>", self._on_tab_changed)

        self._build_setup_tab()
        self._build_paint_tab()

        # --- Global bottom status bar ---
        status = ttk.Frame(self.root, padding=(8, 2, 8, 6))
        status.pack(fill=tk.X)
        self.var_status = tk.StringVar(
            value=f"팔레트 로드 완료: {len(self.palette.item_names)}종 ({len(self.palette)}색) — '이미지 삽입'으로 시작하세요."
        )
        ttk.Label(status, textvariable=self.var_status).pack(side=tk.LEFT)

    def _build_setup_tab(self) -> None:
        # Row 1: file & size & algorithm
        ctrl1 = ttk.Frame(self.tab_setup, padding=(6, 6, 6, 2))
        ctrl1.pack(fill=tk.X)

        self.btn_open = ttk.Button(
            ctrl1, text="이미지 삽입", command=self.on_open_image, width=11
        )
        self.btn_open.pack(side=tk.LEFT, padx=(0, 8))

        ttk.Label(ctrl1, text="크기:").pack(side=tk.LEFT)
        self.var_width = tk.StringVar(value="64")
        self.var_height = tk.StringVar(value="64")
        ttk.Entry(ctrl1, textvariable=self.var_width, width=5).pack(side=tk.LEFT, padx=(3, 1))
        ttk.Label(ctrl1, text="×").pack(side=tk.LEFT)
        ttk.Entry(ctrl1, textvariable=self.var_height, width=5).pack(side=tk.LEFT, padx=(1, 6))

        self.var_mode = tk.StringVar(value="fit")
        ttk.Combobox(
            ctrl1, textvariable=self.var_mode, values=RESIZE_MODES, state="readonly", width=7
        ).pack(side=tk.LEFT, padx=(0, 6))

        ttk.Label(ctrl1, text="매칭:").pack(side=tk.LEFT)
        self.var_metric = tk.StringVar(value="ciede2000")
        ttk.Combobox(
            ctrl1,
            textvariable=self.var_metric,
            values=SUPPORTED_METRICS,
            state="readonly",
            width=10,
        ).pack(side=tk.LEFT, padx=(3, 6))

        ttk.Label(ctrl1, text="디더링:").pack(side=tk.LEFT)
        self.var_dither = tk.StringVar(value="none")
        ttk.Combobox(
            ctrl1,
            textvariable=self.var_dither,
            values=DITHER_OPTIONS,
            state="readonly",
            width=13,
        ).pack(side=tk.LEFT, padx=(3, 8))

        self.btn_convert = ttk.Button(
            ctrl1,
            text="이미지 변환",
            command=self.on_convert_image,
            width=11,
            state=tk.DISABLED,
        )
        self.btn_convert.pack(side=tk.LEFT, padx=(0, 6))

        self.btn_save = ttk.Button(
            ctrl1,
            text="PNG 저장",
            command=self.on_save_result,
            width=9,
            state=tk.DISABLED,
        )
        self.btn_save.pack(side=tk.LEFT)

        # Row 2: helper hint
        ctrl2 = ttk.Frame(self.tab_setup, padding=(6, 0, 6, 4))
        ctrl2.pack(fill=tk.X)

        self.var_overview_grid = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            ctrl2,
            text="32×32 칸 경계선 표시",
            variable=self.var_overview_grid,
            command=self._render_overview_preview,
        ).pack(side=tk.LEFT)

        self.btn_go_paint = ttk.Button(
            ctrl2,
            text="32×32 그리기 시작 →",
            command=lambda: self.open_tile_in_paint_tab(self.selected_tile),
            state=tk.DISABLED,
        )
        self.btn_go_paint.pack(side=tk.RIGHT)

        ttk.Label(
            ctrl2,
            text="우측 변환 결과에서 원하는 32×32 칸을 클릭하면 바로 그리기 모드로 열립니다.",
            foreground="#666666",
        ).pack(side=tk.LEFT, padx=(12, 0))

        # Split viewer (Original | Full Converted Overview)
        viewer = ttk.Frame(self.tab_setup, padding=(6, 2, 6, 6))
        viewer.pack(fill=tk.BOTH, expand=True)
        viewer.columnconfigure(0, weight=1)
        viewer.columnconfigure(1, weight=1)
        viewer.rowconfigure(1, weight=1)

        self.lbl_orig_title = ttk.Label(
            viewer, text="원본 이미지", font=("Malgun Gothic", 9, "bold")
        )
        self.lbl_orig_title.grid(row=0, column=0, sticky=tk.W, pady=(0, 3))

        self.lbl_overview_title = ttk.Label(
            viewer, text="전체 변환 결과 (32×32 칸 클릭 = 그리기)", font=("Malgun Gothic", 9, "bold")
        )
        self.lbl_overview_title.grid(row=0, column=1, sticky=tk.W, padx=(6, 0), pady=(0, 3))

        self.canvas_orig = tk.Canvas(
            viewer, bg="#1E1E22", highlightthickness=1, highlightbackground="#3A3A44"
        )
        self.canvas_orig.grid(row=1, column=0, sticky=tk.NSEW, padx=(0, 3))

        self.canvas_overview = tk.Canvas(
            viewer, bg="#1E1E22", highlightthickness=1, highlightbackground="#3A3A44"
        )
        self.canvas_overview.grid(row=1, column=1, sticky=tk.NSEW, padx=(3, 0))

        self.canvas_orig.bind("<Configure>", lambda _e: self._render_original_preview())
        self.canvas_overview.bind("<Configure>", lambda _e: self._render_overview_preview())
        self.canvas_overview.bind("<Button-1>", self._on_click_overview)

    def _build_paint_tab(self) -> None:
        """Vertical compact layout optimized for Split View next to Minecraft."""
        # --- Tile Navigation Bar ---
        nav = ttk.Frame(self.tab_paint, padding=(6, 6, 6, 4))
        nav.pack(fill=tk.X)

        self.btn_prev_tile = ttk.Button(
            nav, text="◀", command=lambda: self.step_tile(-1), width=3, state=tk.DISABLED
        )
        self.btn_prev_tile.pack(side=tk.LEFT)

        self.var_tile_select = tk.StringVar(value="변환된 이미지 없음")
        self.cmb_tile = ttk.Combobox(
            nav,
            textvariable=self.var_tile_select,
            values=(),
            state="disabled",
            width=24,
        )
        self.cmb_tile.pack(side=tk.LEFT, padx=4)
        self.cmb_tile.bind("<<ComboboxSelected>>", self._on_tile_combo_selected)

        self.btn_next_tile = ttk.Button(
            nav, text="▶", command=lambda: self.step_tile(1), width=3, state=tk.DISABLED
        )
        self.btn_next_tile.pack(side=tk.LEFT, padx=(0, 8))

        self.var_tile_done = tk.BooleanVar(value=False)
        self.chk_tile_done = ttk.Checkbutton(
            nav,
            text="✓ 이 칸 완료",
            variable=self.var_tile_done,
            command=self._on_toggle_tile_done,
            state=tk.DISABLED,
        )
        self.chk_tile_done.pack(side=tk.LEFT, padx=(0, 6))

        self.btn_save_tile = ttk.Button(
            nav,
            text="칸 PNG",
            command=self.on_save_current_tile,
            width=7,
            state=tk.DISABLED,
        )
        self.btn_save_tile.pack(side=tk.RIGHT)

        # --- Visual Guide Controls ---
        opts = ttk.Frame(self.tab_paint, padding=(6, 0, 6, 4))
        opts.pack(fill=tk.X)

        self.var_paint_grid = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            opts,
            text="격자",
            variable=self.var_paint_grid,
            command=self._render_paint_canvas,
        ).pack(side=tk.LEFT, padx=(0, 6))

        self.var_crosshair = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            opts,
            text="십자선",
            variable=self.var_crosshair,
            command=self._render_paint_canvas,
        ).pack(side=tk.LEFT, padx=(0, 6))

        self.var_dim_done = tk.BooleanVar(value=True)
        ttk.Checkbutton(
            opts,
            text="완료색 흐리게",
            variable=self.var_dim_done,
            command=self._render_paint_canvas,
        ).pack(side=tk.LEFT, padx=(0, 8))

        self.var_hl_scope = tk.StringVar(value="shade")
        ttk.Radiobutton(
            opts,
            text="단계별",
            value="shade",
            variable=self.var_hl_scope,
            command=self._on_hl_scope_changed,
        ).pack(side=tk.LEFT)
        ttk.Radiobutton(
            opts,
            text="아이템전체",
            value="item",
            variable=self.var_hl_scope,
            command=self._on_hl_scope_changed,
        ).pack(side=tk.LEFT, padx=(3, 6))

        ttk.Label(opts, text="배경:").pack(side=tk.LEFT)
        self.var_hl_bg = tk.StringVar(value="자동")
        cmb_bg = ttk.Combobox(
            opts,
            textvariable=self.var_hl_bg,
            values=("자동", "밝게", "어둡게"),
            state="readonly",
            width=5,
        )
        cmb_bg.pack(side=tk.LEFT, padx=(2, 6))
        cmb_bg.bind("<<ComboboxSelected>>", lambda _e: self._render_paint_canvas())

        self.btn_clear_hl = ttk.Button(
            opts,
            text="강조 해제",
            command=self.clear_highlight,
            width=9,
            state=tk.DISABLED,
        )
        self.btn_clear_hl.pack(side=tk.RIGHT)

        # --- Resizable Vertical PanedWindow: Top 32x32 Canvas | Bottom Pixel Info + Checklist ---
        paned = ttk.PanedWindow(self.tab_paint, orient=tk.VERTICAL)
        paned.pack(fill=tk.BOTH, expand=True, padx=6, pady=(0, 4))

        # Top pane: 32x32 canvas
        canvas_frame = ttk.Frame(paned)
        self.canvas_paint = tk.Canvas(
            canvas_frame,
            bg="#18181C",
            highlightthickness=1,
            highlightbackground="#3A3A44",
            height=360,
        )
        self.canvas_paint.pack(fill=tk.BOTH, expand=True)
        self.canvas_paint.bind("<Configure>", lambda _e: self._render_paint_canvas())
        self.canvas_paint.bind("<Motion>", self._on_hover_paint)
        self.canvas_paint.bind("<Leave>", self._on_leave_paint)
        self.canvas_paint.bind("<Button-1>", self._on_click_paint)
        self.canvas_paint.bind("<Button-3>", lambda _e: self.clear_highlight())
        paned.add(canvas_frame, weight=3)

        # Bottom pane: Live pixel readout + interactive item checklist
        bottom_frame = ttk.Frame(paned, padding=(0, 4, 0, 0))

        # Large readable pixel readout card
        card = ttk.Frame(bottom_frame, padding=(4, 2, 4, 4))
        card.pack(fill=tk.X)

        self.lbl_swatch = tk.Label(
            card, text="  ", bg="#333338", width=4, relief=tk.SOLID, borderwidth=1
        )
        self.lbl_swatch.pack(side=tk.LEFT, padx=(0, 8), fill=tk.Y)

        self.var_pixel_info = tk.StringVar(
            value="캔버스에 마우스를 올리면 좌표와 아이템이 표시됩니다. (클릭: 해당 색 강조)"
        )
        ttk.Label(
            card,
            textvariable=self.var_pixel_info,
            font=("Malgun Gothic", 10, "bold"),
        ).pack(side=tk.LEFT, fill=tk.X, expand=True)

        # Checklist action row
        list_bar = ttk.Frame(bottom_frame, padding=(2, 2))
        list_bar.pack(fill=tk.X)

        self.lbl_checklist_title = ttk.Label(
            list_bar,
            text="현재 32×32 칸 아이템 목록 (클릭=강조, 더블클릭/Space=완료 체크)",
            font=("Malgun Gothic", 9, "bold"),
        )
        self.lbl_checklist_title.pack(side=tk.LEFT)

        self.btn_next_color = ttk.Button(
            list_bar,
            text="다음 미완료 색 (E)",
            command=lambda: self.step_checklist_color(1),
            width=15,
            state=tk.DISABLED,
        )
        self.btn_next_color.pack(side=tk.RIGHT, padx=(4, 0))

        self.btn_toggle_color_done = ttk.Button(
            list_bar,
            text="선택 색 완료 ✓",
            command=self.toggle_selected_color_done,
            width=13,
            state=tk.DISABLED,
        )
        self.btn_toggle_color_done.pack(side=tk.RIGHT)

        # Scrollable checklist Treeview with real color swatches
        tree_wrap = ttk.Frame(bottom_frame)
        tree_wrap.pack(fill=tk.BOTH, expand=True, pady=(2, 0))

        self.tree_colors = ttk.Treeview(
            tree_wrap,
            columns=("done", "item", "shade", "count", "hex"),
            show="tree headings",
            selectmode="browse",
            height=8,
        )
        self.tree_colors.heading("#0", text="색")
        self.tree_colors.heading("done", text="완료")
        self.tree_colors.heading("item", text="아이템")
        self.tree_colors.heading("shade", text="명암 단계")
        self.tree_colors.heading("count", text="픽셀")
        self.tree_colors.heading("hex", text="HEX")

        self.tree_colors.column("#0", width=36, minwidth=36, stretch=False, anchor=tk.CENTER)
        self.tree_colors.column("done", width=42, minwidth=40, stretch=False, anchor=tk.CENTER)
        self.tree_colors.column("item", width=180, minwidth=120)
        self.tree_colors.column("shade", width=95, minwidth=75)
        self.tree_colors.column("count", width=52, minwidth=45, stretch=False, anchor=tk.E)
        self.tree_colors.column("hex", width=72, minwidth=65, stretch=False, anchor=tk.CENTER)

        self.tree_colors.tag_configure("completed", foreground="#888888")

        sb = ttk.Scrollbar(tree_wrap, orient=tk.VERTICAL, command=self.tree_colors.yview)
        self.tree_colors.configure(yscrollcommand=sb.set)
        self.tree_colors.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        sb.pack(side=tk.RIGHT, fill=tk.Y)

        self.tree_colors.bind("<<TreeviewSelect>>", self._on_tree_select)
        self.tree_colors.bind("<Double-1>", lambda _e: self.toggle_selected_color_done())
        self.tree_colors.bind("<space>", self._on_tree_space)

        paned.add(bottom_frame, weight=2)

    def _bind_shortcuts(self) -> None:
        self.root.bind("<Escape>", lambda _e: self.clear_highlight())
        self.root.bind("<Left>", lambda _e: self._on_arrow_key(-1, 0))
        self.root.bind("<Right>", lambda _e: self._on_arrow_key(1, 0))
        self.root.bind("<Up>", lambda _e: self._on_arrow_key(0, -1))
        self.root.bind("<Down>", lambda _e: self._on_arrow_key(0, 1))
        self.root.bind("q", lambda _e: self._on_hotkey_step_color(-1))
        self.root.bind("Q", lambda _e: self._on_hotkey_step_color(-1))
        self.root.bind("e", lambda _e: self._on_hotkey_step_color(1))
        self.root.bind("E", lambda _e: self._on_hotkey_step_color(1))

    # ------------------------------------------------------------------
    # Window Convenience Controls (Split View / Topmost / Opacity)
    # ------------------------------------------------------------------
    def toggle_compact_window(self) -> None:
        if not self._is_compact_window:
            self._saved_wide_geometry = self.root.geometry()
            sh = self.root.winfo_screenheight()
            sw = self.root.winfo_screenwidth()
            w = 540
            h = max(680, sh - 80)
            x = max(0, sw - w - 12)
            self.root.geometry(f"{w}x{h}+{x}+20")
            self._is_compact_window = True
            self.btn_compact.config(text="넓은 창 복귀")
            if self.result is not None:
                self.notebook.select(self.tab_paint)
        else:
            self.root.geometry(self._saved_wide_geometry)
            self._is_compact_window = False
            self.btn_compact.config(text="스플릿 뷰 크기")

    def _apply_topmost(self) -> None:
        self.root.attributes("-topmost", bool(self.var_topmost.get()))

    def _apply_alpha(self, _val: str) -> None:
        alpha = float(self.var_alpha.get())
        self.root.attributes("-alpha", alpha)
        self.lbl_alpha.config(text=f"{int(round(alpha * 100))}%")

    # ------------------------------------------------------------------
    # Load & Convert
    # ------------------------------------------------------------------
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
        self.completed_tiles.clear()
        self.completed_colors.clear()
        self.clear_highlight(redraw=False)

        self.btn_convert.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.DISABLED)
        self.btn_go_paint.config(state=tk.DISABLED)
        self._set_paint_controls_enabled(False)
        self.canvas_overview.delete("all")
        self.canvas_paint.delete("all")
        self.tree_colors.delete(*self.tree_colors.get_children())

        w, h = img.size
        self.lbl_orig_title.config(text=f"원본 이미지 ({self.source_path.name} — {w}×{h})")
        self.var_status.set(f"불러옴: {self.source_path.name} ({w}×{h}) — '이미지 변환'을 누르세요.")
        self._render_original_preview()

    def on_convert_image(self) -> None:
        if self.source_image is None:
            messagebox.showwarning("이미지 없음", "먼저 이미지를 삽입하세요.")
            return

        try:
            w_str = self.var_width.get().strip()
            h_str = self.var_height.get().strip()
            width = int(w_str) if w_str else None
            height = int(h_str) if h_str else None
            if (width is not None and width <= 0) or (height is not None and height <= 0):
                raise ValueError("크기는 1 이상의 정수여야 합니다.")
        except ValueError as exc:
            messagebox.showerror("잘못된 크기 입력", str(exc))
            return

        metric = self.var_metric.get()
        dithering = self.var_dither.get()
        mode = self.var_mode.get()
        src_copy = self.source_image.copy()

        self.btn_convert.config(state=tk.DISABLED)
        self.btn_open.config(state=tk.DISABLED)
        self.var_status.set(f"변환 중... ({metric}, {dithering})")

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
        self.selected_tile = (0, 0)
        self.completed_tiles.clear()
        self.completed_colors.clear()
        self.clear_highlight(redraw=False)

        self.btn_convert.config(state=tk.NORMAL)
        self.btn_open.config(state=tk.NORMAL)
        self.btn_save.config(state=tk.NORMAL)
        self.btn_go_paint.config(state=tk.NORMAL)

        cols, rows = res.tile_grid_shape(TILE_SIZE)
        self._tile_list = [(c, r) for r in range(rows) for c in range(cols)]
        self._refresh_tile_combobox()
        self._set_paint_controls_enabled(True)

        used = len(res.item_summary())
        self.lbl_overview_title.config(
            text=f"전체 변환 결과 ({res.width}×{res.height}px | {cols}×{rows}칸 | {res.metric.upper()})"
        )
        self.var_status.set(
            f"변환 완료 ({elapsed_ms:.0f}ms) | {res.width}×{res.height}px ({cols}×{rows}칸) "
            f"| 평균 ΔE00={res.mean_delta_e2000:.2f} | 사용 아이템 {used}종"
        )
        self._render_overview_preview()
        self._load_selected_tile()

    def _set_paint_controls_enabled(self, enabled: bool) -> None:
        state = tk.NORMAL if enabled else tk.DISABLED
        for widget in (
            self.btn_prev_tile,
            self.btn_next_tile,
            self.chk_tile_done,
            self.btn_save_tile,
            self.btn_clear_hl,
            self.btn_next_color,
            self.btn_toggle_color_done,
            self.btn_save_prog,
            self.btn_load_prog,
        ):
            widget.config(state=state)
        self.cmb_tile.config(state="readonly" if enabled else "disabled")
        if not enabled:
            self.cmb_tile.config(values=())
            self.var_tile_select.set("변환된 이미지 없음")

    # ------------------------------------------------------------------
    # Tile Selection & Checklist
    # ------------------------------------------------------------------
    def _format_tile_label(self, col: int, row: int) -> str:
        assert self.result is not None
        x0 = col * TILE_SIZE
        y0 = row * TILE_SIZE
        x1 = min(x0 + TILE_SIZE, self.result.width) - 1
        y1 = min(y0 + TILE_SIZE, self.result.height) - 1
        done_mark = "✓ " if (col, row) in self.completed_tiles else ""
        return f"{done_mark}칸 ({col + 1}, {row + 1}) [{x0}~{x1}, {y0}~{y1}]"

    def _refresh_tile_combobox(self) -> None:
        if self.result is None:
            return
        labels = [self._format_tile_label(c, r) for c, r in self._tile_list]
        self.cmb_tile.config(values=labels)
        if self.selected_tile in self._tile_list:
            idx = self._tile_list.index(self.selected_tile)
            self.var_tile_select.set(labels[idx])

    def open_tile_in_paint_tab(self, tile: tuple[int, int]) -> None:
        if self.result is None:
            return
        self.select_tile(tile)
        self.notebook.select(self.tab_paint)

    def select_tile(self, tile: tuple[int, int]) -> None:
        if self.result is None or tile not in self._tile_list:
            return
        self.selected_tile = tile
        self.clear_highlight(redraw=False)
        self._load_selected_tile()

    def _load_selected_tile(self) -> None:
        if self.result is None:
            return
        self._refresh_tile_combobox()
        self.var_tile_done.set(self.selected_tile in self.completed_tiles)
        self._rebuild_tile_checklist()
        self._render_paint_canvas()
        self._render_overview_preview()

    def step_tile(self, delta: int) -> None:
        if self.result is None or not self._tile_list:
            return
        idx = self._tile_list.index(self.selected_tile)
        self.select_tile(self._tile_list[(idx + delta) % len(self._tile_list)])

    def _on_tile_combo_selected(self, _event: tk.Event) -> None:
        idx = self.cmb_tile.current()
        if 0 <= idx < len(self._tile_list):
            self.select_tile(self._tile_list[idx])

    def _on_toggle_tile_done(self) -> None:
        if self.result is None:
            return
        if self.var_tile_done.get():
            self.completed_tiles.add(self.selected_tile)
        else:
            self.completed_tiles.discard(self.selected_tile)
        self._refresh_tile_combobox()
        self._render_overview_preview()
        done_cnt = len(self.completed_tiles)
        total_cnt = len(self._tile_list)
        self.var_status.set(f"전체 진행률: {done_cnt}/{total_cnt} 칸 완료")

    def _get_swatch_photo(self, hex_code: str) -> tk.PhotoImage:
        photo = self._swatch_cache.get(hex_code)
        if photo is None:
            photo = tk.PhotoImage(width=14, height=14)
            photo.put(hex_code, to=(0, 0, 14, 14))
            self._swatch_cache[hex_code] = photo
        return photo

    def _rebuild_tile_checklist(self) -> None:
        self.tree_colors.delete(*self.tree_colors.get_children())
        self._tree_row_to_pidx.clear()
        if self.result is None:
            return

        tc, tr = self.selected_tile
        sub, _ = self.result.slice_tile(tc, tr, TILE_SIZE)
        done_set = self.completed_colors.get(self.selected_tile, set())

        # Count exact palette indices in this tile
        valid = sub.palette_indices[sub.palette_indices >= 0]
        uniq_idx, counts = np.unique(valid, return_counts=True)
        order = np.argsort(-counts)

        done_count = 0
        for pos in order:
            pidx = int(uniq_idx[pos])
            cnt = int(counts[pos])
            color = self.palette[pidx]
            is_done = pidx in done_set
            if is_done:
                done_count += 1
            swatch = self._get_swatch_photo(color.hex_code)
            iid = self.tree_colors.insert(
                "",
                tk.END,
                image=swatch,
                values=(
                    "☑" if is_done else "☐",
                    color.korean_name,
                    f"{color.shade}단계 ({color.shade_label})",
                    cnt,
                    color.hex_code,
                ),
                tags=("completed",) if is_done else (),
            )
            self._tree_row_to_pidx[iid] = pidx

        total_colors = len(order)
        self.lbl_checklist_title.config(
            text=f"칸 ({tc + 1}, {tr + 1}) 색상 목록 — {done_count}/{total_colors} 완료 (클릭=강조, 더블클릭=완료)"
        )

    # ------------------------------------------------------------------
    # Color / Item Highlight Isolation
    # ------------------------------------------------------------------
    def highlight_palette_index(self, pidx: int) -> None:
        if self.result is None:
            return
        color = self.palette[pidx]
        if self.var_hl_scope.get() == "item":
            self.highlighted_palette_idx = None
            self.highlighted_item_name = color.item_name
            msg = f"강조 중 (아이템 전체): {color.korean_name} (1~4단계)"
        else:
            self.highlighted_palette_idx = pidx
            self.highlighted_item_name = None
            msg = f"강조 중: {color.display_label} ({color.hex_code})"

        # Sync Treeview selection without re-triggering loop
        for iid, row_pidx in self._tree_row_to_pidx.items():
            if row_pidx == pidx:
                if self.tree_colors.selection() != (iid,):
                    self.tree_colors.selection_set(iid)
                    self.tree_colors.see(iid)
                break

        self.var_status.set(msg)
        self._render_paint_canvas()

    def clear_highlight(self, *, redraw: bool = True) -> None:
        self.highlighted_palette_idx = None
        self.highlighted_item_name = None
        if hasattr(self, "tree_colors"):
            sel = self.tree_colors.selection()
            if sel:
                self.tree_colors.selection_remove(*sel)
        if redraw and self.result is not None:
            self._render_paint_canvas()
            self.var_status.set("색상 강조 해제됨")

    def _on_hl_scope_changed(self) -> None:
        sel = self.tree_colors.selection()
        if sel and sel[0] in self._tree_row_to_pidx:
            self.highlight_palette_index(self._tree_row_to_pidx[sel[0]])

    def _on_tree_select(self, _event: tk.Event) -> None:
        sel = self.tree_colors.selection()
        if not sel:
            return
        pidx = self._tree_row_to_pidx.get(sel[0])
        if pidx is not None:
            self.highlight_palette_index(pidx)

    def _on_tree_space(self, _event: tk.Event) -> str:
        self.toggle_selected_color_done()
        return "break"

    def toggle_selected_color_done(self) -> None:
        if self.result is None:
            return
        sel = self.tree_colors.selection()
        if not sel:
            return
        iid = sel[0]
        pidx = self._tree_row_to_pidx.get(iid)
        if pidx is None:
            return

        done_set = self.completed_colors.setdefault(self.selected_tile, set())
        if pidx in done_set:
            done_set.remove(pidx)
            is_done = False
        else:
            done_set.add(pidx)
            is_done = True

        # Update row in-place
        vals = list(self.tree_colors.item(iid, "values"))
        vals[0] = "☑" if is_done else "☐"
        self.tree_colors.item(iid, values=vals, tags=("completed",) if is_done else ())

        total = len(self._tree_row_to_pidx)
        done_cnt = sum(1 for p in self._tree_row_to_pidx.values() if p in done_set)
        tc, tr = self.selected_tile
        self.lbl_checklist_title.config(
            text=f"칸 ({tc + 1}, {tr + 1}) 색상 목록 — {done_cnt}/{total} 완료 (클릭=강조, 더블클릭=완료)"
        )

        # Auto-mark tile done if every color in this tile is completed
        if total > 0 and done_cnt == total:
            self.completed_tiles.add(self.selected_tile)
            self.var_tile_done.set(True)
            self._refresh_tile_combobox()
            self._render_overview_preview()
        elif not is_done and self.selected_tile in self.completed_tiles:
            self.completed_tiles.discard(self.selected_tile)
            self.var_tile_done.set(False)
            self._refresh_tile_combobox()
            self._render_overview_preview()

        self._render_paint_canvas()

    def step_checklist_color(self, delta: int) -> None:
        """Jump to next/previous uncompleted color in the current tile."""
        items = list(self.tree_colors.get_children())
        if not items:
            return
        done_set = self.completed_colors.get(self.selected_tile, set())
        sel = self.tree_colors.selection()
        start_idx = items.index(sel[0]) if sel and sel[0] in items else -1

        n = len(items)
        # Prefer uncompleted colors; fall back to any color if all completed
        for step in range(1, n + 1):
            cand_iid = items[(start_idx + delta * step) % n]
            pidx = self._tree_row_to_pidx[cand_iid]
            if pidx not in done_set:
                self.tree_colors.selection_set(cand_iid)
                self.tree_colors.see(cand_iid)
                self.highlight_palette_index(pidx)
                return

        next_iid = items[(start_idx + delta) % n]
        self.tree_colors.selection_set(next_iid)
        self.tree_colors.see(next_iid)
        self.highlight_palette_index(self._tree_row_to_pidx[next_iid])

    # ------------------------------------------------------------------
    # Keyboard Shortcuts
    # ------------------------------------------------------------------
    def _is_typing_in_entry(self) -> bool:
        return isinstance(self.root.focus_get(), (ttk.Entry, tk.Entry))

    def _on_arrow_key(self, dx: int, dy: int) -> None:
        if self.result is None or self._is_typing_in_entry():
            return
        # Only hijack arrows when on Paint tab and Treeview isn't focused
        if self.notebook.index(self.notebook.select()) != 1:
            return
        if self.root.focus_get() is self.tree_colors:
            return
        cols, rows = self.result.tile_grid_shape(TILE_SIZE)
        tc, tr = self.selected_tile
        nc = max(0, min(cols - 1, tc + dx))
        nr = max(0, min(rows - 1, tr + dy))
        if (nc, nr) != (tc, tr):
            self.select_tile((nc, nr))

    def _on_hotkey_step_color(self, delta: int) -> None:
        if self.result is None or self._is_typing_in_entry():
            return
        if self.notebook.index(self.notebook.select()) == 1:
            self.step_checklist_color(delta)

    def _on_tab_changed(self, _event: tk.Event) -> None:
        if self.notebook.index(self.notebook.select()) == 0:
            self._render_original_preview()
            self._render_overview_preview()
        else:
            self._render_paint_canvas()

    # ------------------------------------------------------------------
    # Rendering
    # ------------------------------------------------------------------
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
        resample = Image.Resampling.NEAREST if scale >= 2.0 else Image.Resampling.LANCZOS
        preview = img.resize((disp_w, disp_h), resample=resample)

        self._orig_photo = ImageTk.PhotoImage(preview)
        self.canvas_orig.create_image(cw // 2, ch // 2, image=self._orig_photo, anchor=tk.CENTER)

    def _render_overview_preview(self) -> None:
        self.canvas_overview.delete("all")
        self._overview_box = None
        if self.result is None:
            return

        cw = max(1, self.canvas_overview.winfo_width())
        ch = max(1, self.canvas_overview.winfo_height())
        if cw < 10 or ch < 10:
            return

        res = self.result
        scale_f = min((cw - 16) / res.width, (ch - 16) / res.height)
        if scale_f >= 1.0:
            preview = res.to_image(scale=max(1, int(scale_f)), grid=False)
        else:
            preview = res.to_image(scale=1).resize(
                (max(1, int(res.width * scale_f)), max(1, int(res.height * scale_f))),
                resample=Image.Resampling.NEAREST,
            )

        pw, ph = preview.size
        x0 = (cw - pw) // 2
        y0 = (ch - ph) // 2
        self._overview_box = (x0, y0, pw, ph)

        self._overview_photo = ImageTk.PhotoImage(preview)
        self.canvas_overview.create_image(x0, y0, image=self._overview_photo, anchor=tk.NW)

        if self.var_overview_grid.get():
            cols, rows = res.tile_grid_shape(TILE_SIZE)
            for gx in range(TILE_SIZE, res.width, TILE_SIZE):
                lx = x0 + gx * pw / res.width
                self.canvas_overview.create_line(lx, y0, lx, y0 + ph, fill="#FFD43B", width=2)
            for gy in range(TILE_SIZE, res.height, TILE_SIZE):
                ly = y0 + gy * ph / res.height
                self.canvas_overview.create_line(x0, ly, x0 + pw, ly, fill="#FFD43B", width=2)

            for tr in range(rows):
                for tc in range(cols):
                    bx = x0 + (tc * TILE_SIZE) * pw / res.width + 4
                    by = y0 + (tr * TILE_SIZE) * ph / res.height + 4
                    is_done = (tc, tr) in self.completed_tiles
                    label = f"{tc + 1},{tr + 1}✓" if is_done else f"{tc + 1},{tr + 1}"
                    outline = "#4ADE80" if is_done else "#FFD43B"
                    self.canvas_overview.create_rectangle(
                        bx - 2, by - 1, bx + (32 if is_done else 24), by + 13,
                        fill="#111114", outline=outline,
                    )
                    self.canvas_overview.create_text(
                        bx + (15 if is_done else 11),
                        by + 6,
                        text=label,
                        fill=outline,
                        font=("Consolas", 8, "bold"),
                    )

            # Highlight currently active tile
            tc, tr = self.selected_tile
            _, (tx0, ty0, tx1, ty1) = res.slice_tile(tc, tr, TILE_SIZE)
            self.canvas_overview.create_rectangle(
                x0 + tx0 * pw / res.width,
                y0 + ty0 * ph / res.height,
                x0 + tx1 * pw / res.width,
                y0 + ty1 * ph / res.height,
                outline="#38BDF8",
                width=3,
            )

    def _build_tile_highlight_mask(self, sub: ConversionResult) -> np.ndarray | None:
        """Return (H, W) bool mask of highlighted pixels, or None if no highlight active."""
        if self.highlighted_palette_idx is not None:
            return sub.palette_indices == self.highlighted_palette_idx
        if self.highlighted_item_name is not None:
            matching_indices = [
                i
                for i, c in enumerate(self.palette)
                if c.item_name == self.highlighted_item_name
            ]
            return np.isin(sub.palette_indices, matching_indices)
        return None

    def _render_paint_canvas(self) -> None:
        self.canvas_paint.delete("all")
        self._paint_view = None
        if self.result is None:
            return

        cw = max(1, self.canvas_paint.winfo_width())
        ch = max(1, self.canvas_paint.winfo_height())
        if cw < 20 or ch < 20:
            return

        tc, tr = self.selected_tile
        sub, (tx0, ty0, _tx1, _ty1) = self.result.slice_tile(tc, tr, TILE_SIZE)

        ruler_pad = 22
        avail_w = max(32, cw - ruler_pad - 12)
        avail_h = max(32, ch - ruler_pad - 12)
        cell_px = max(2, int(min(avail_w / sub.width, avail_h / sub.height)))

        # Apply contrast-aware color isolation and/or completed-color fading
        rgb = sub.pixel_rgb.astype(np.float64)
        hl_mask = self._build_tile_highlight_mask(sub)
        done_set = self.completed_colors.get(self.selected_tile, set())
        use_light_bg = False

        if hl_mask is not None:
            # Determine whether highlighted pixels are dark -> use bright washed background
            bg_mode = self.var_hl_bg.get()
            if bg_mode == "밝게":
                use_light_bg = True
            elif bg_mode == "어둡게":
                use_light_bg = False
            else:
                if np.any(hl_mask):
                    hl_rgb = rgb[hl_mask]
                    hl_lum = float(
                        np.mean(
                            0.299 * hl_rgb[:, 0]
                            + 0.587 * hl_rgb[:, 1]
                            + 0.114 * hl_rgb[:, 2]
                        )
                        / 255.0
                    )
                else:
                    hl_lum = 0.5
                use_light_bg = hl_lum < 0.45

            # Desaturate non-highlighted pixels and wash toward light or dark neutral
            # so contours stay visible while target pixels pop with high contrast
            gray = (
                0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
            )[..., None]
            if use_light_bg:
                # Light neutral wash (~#D5D9E2..#ECEFF5) -> dark colors stand out sharply
                faded = gray * 0.28 + 185.0
            else:
                # Dark neutral wash (~#20232A..#484D58) -> bright colors stand out sharply
                faded = gray * 0.25 + 26.0
            rgb = np.where(hl_mask[..., None], rgb, faded)
        elif self.var_dim_done.get() and done_set:
            done_mask = np.isin(sub.palette_indices, list(done_set))
            gray = (
                0.299 * rgb[..., 0] + 0.587 * rgb[..., 1] + 0.114 * rgb[..., 2]
            )[..., None]
            faded_done = gray * 0.45 + 65.0
            rgb = np.where(done_mask[..., None], faded_done, rgb)

        disp_arr = np.rint(np.clip(rgb, 0, 255)).astype(np.uint8)
        preview = Image.fromarray(disp_arr, mode="RGB").resize(
            (sub.width * cell_px, sub.height * cell_px),
            resample=Image.Resampling.NEAREST,
        )

        draw = ImageDraw.Draw(preview)
        pw, ph = preview.size

        if self.var_paint_grid.get() and cell_px >= 4:
            grid_1px = (172, 178, 190) if use_light_bg else (58, 60, 70)
            grid_8px = (217, 119, 6) if use_light_bg else (255, 215, 70)
            for x in range(sub.width + 1):
                px = min(x * cell_px, pw - 1)
                color = grid_8px if (x % 8 == 0 and 0 < x < sub.width) else grid_1px
                draw.line([(px, 0), (px, ph - 1)], fill=color, width=1)
            for y in range(sub.height + 1):
                py = min(y * cell_px, ph - 1)
                color = grid_8px if (y % 8 == 0 and 0 < y < sub.height) else grid_1px
                draw.line([(0, py), (pw - 1, py)], fill=color, width=1)

        # Draw crisp high-contrast border around highlighted pixels
        if hl_mask is not None and cell_px >= 6:
            outline_rgb = (255, 46, 99) if use_light_bg else (56, 220, 255)
            ys, xs = np.nonzero(hl_mask)
            for py_i, px_i in zip(ys.tolist(), xs.tolist()):
                rx0 = px_i * cell_px
                ry0 = py_i * cell_px
                rx1 = rx0 + cell_px - 1
                ry1 = ry0 + cell_px - 1
                draw.rectangle([rx0, ry0, rx1, ry1], outline=outline_rgb, width=1)

        x0 = ruler_pad + (cw - ruler_pad - pw) // 2
        y0 = ruler_pad + (ch - ruler_pad - ph) // 2
        self._paint_view = (x0, y0, pw, ph, tx0, ty0, sub.width, sub.height, cell_px)

        self._paint_photo = ImageTk.PhotoImage(preview)
        self.canvas_paint.create_image(x0, y0, image=self._paint_photo, anchor=tk.NW)
        self.canvas_paint.create_rectangle(
            x0 - 1, y0 - 1, x0 + pw, y0 + ph, outline="#FFD43B", width=2
        )

        # Coordinate rulers (1..32)
        step = 1 if cell_px >= 16 else (4 if cell_px >= 8 else 8)
        for lx in range(0, sub.width, step):
            self.canvas_paint.create_text(
                x0 + (lx + 0.5) * cell_px,
                y0 - 10,
                text=str(lx + 1),
                fill="#D4D4DC",
                font=("Consolas", 8),
            )
        for ly in range(0, sub.height, step):
            self.canvas_paint.create_text(
                x0 - 11,
                y0 + (ly + 0.5) * cell_px,
                text=str(ly + 1),
                fill="#D4D4DC",
                font=("Consolas", 8),
            )

        self._draw_crosshair()

    def _draw_crosshair(self) -> None:
        self.canvas_paint.delete("crosshair")
        if (
            not self.var_crosshair.get()
            or self._paint_view is None
            or self._hover_tile_pixel is None
        ):
            return
        x0, y0, pw, ph, _tx0, _ty0, _sw, _sh, cell_px = self._paint_view
        lx, ly = self._hover_tile_pixel
        px0 = x0 + lx * cell_px
        py0 = y0 + ly * cell_px
        # Subtle row/column band + bright target cell outline
        self.canvas_paint.create_rectangle(
            x0, py0, x0 + pw, py0 + cell_px, outline="#94A3B8", dash=(2, 2), tags="crosshair"
        )
        self.canvas_paint.create_rectangle(
            px0, y0, px0 + cell_px, y0 + ph, outline="#94A3B8", dash=(2, 2), tags="crosshair"
        )
        self.canvas_paint.create_rectangle(
            px0, py0, px0 + cell_px, py0 + cell_px, outline="#FFFFFF", width=2, tags="crosshair"
        )

    # ------------------------------------------------------------------
    # Mouse Events
    # ------------------------------------------------------------------
    def _on_click_overview(self, event: tk.Event) -> None:
        if self.result is None or self._overview_box is None:
            return
        x0, y0, pw, ph = self._overview_box
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            return
        gx = min(self.result.width - 1, int(rx * self.result.width / pw))
        gy = min(self.result.height - 1, int(ry * self.result.height / ph))
        self.open_tile_in_paint_tab((gx // TILE_SIZE, gy // TILE_SIZE))

    def _paint_event_to_local_pixel(self, event: tk.Event) -> tuple[int, int] | None:
        if self.result is None or self._paint_view is None:
            return None
        x0, y0, pw, ph, _tx0, _ty0, sw, sh, _cell = self._paint_view
        rx = event.x - x0
        ry = event.y - y0
        if not (0 <= rx < pw and 0 <= ry < ph):
            return None
        lx = min(sw - 1, int(rx * sw / pw))
        ly = min(sh - 1, int(ry * sh / ph))
        return lx, ly

    def _on_hover_paint(self, event: tk.Event) -> None:
        loc = self._paint_event_to_local_pixel(event)
        if loc is None or self.result is None or self._paint_view is None:
            self._hover_tile_pixel = None
            self.canvas_paint.delete("crosshair")
            return

        if loc != self._hover_tile_pixel:
            self._hover_tile_pixel = loc
            self._draw_crosshair()

        lx, ly = loc
        _, _, _, _, tx0, ty0, _, _, _ = self._paint_view
        gx, gy = tx0 + lx, ty0 + ly
        pidx = int(self.result.palette_indices[gy, gx])
        if pidx < 0:
            self.lbl_swatch.config(bg="#333338")
            self.var_pixel_info.set(f"좌표 ({lx + 1}, {ly + 1}) | 투명")
            return

        color = self.palette[pidx]
        de = float(self.result.ciede2000_errors[gy, gx])
        self.lbl_swatch.config(bg=color.hex_code)
        self.var_pixel_info.set(
            f"({lx + 1:2d}, {ly + 1:2d})  {color.korean_name} [{color.shade}단계: {color.shade_label}]  "
            f"{color.hex_code}  ΔE00={de:.1f}"
        )

    def _on_leave_paint(self, _event: tk.Event) -> None:
        self._hover_tile_pixel = None
        self.canvas_paint.delete("crosshair")

    def _on_click_paint(self, event: tk.Event) -> None:
        loc = self._paint_event_to_local_pixel(event)
        if loc is None or self.result is None or self._paint_view is None:
            return
        lx, ly = loc
        _, _, _, _, tx0, ty0, _, _, _ = self._paint_view
        pidx = int(self.result.palette_indices[ty0 + ly, tx0 + lx])
        if pidx < 0:
            return

        # Toggle off if clicking the already-highlighted color/item
        color = self.palette[pidx]
        if (
            self.highlighted_palette_idx == pidx
            or self.highlighted_item_name == color.item_name
        ):
            self.clear_highlight()
        else:
            self.highlight_palette_index(pidx)

    # ------------------------------------------------------------------
    # Save Images & Progress JSON
    # ------------------------------------------------------------------
    def on_save_result(self) -> None:
        if self.result is None:
            return
        stem = self.source_path.stem if self.source_path else "converted"
        default_name = f"{stem}_{self.result.width}x{self.result.height}_{self.result.metric}.png"
        save_path = filedialog.asksaveasfilename(
            title="전체 변환 이미지 저장",
            defaultextension=".png",
            initialfile=default_name,
            filetypes=[("PNG 이미지", "*.png")],
        )
        if not save_path:
            return
        self.result.to_image(scale=1).save(save_path)
        self.var_status.set(f"전체 PNG 저장 완료: {save_path}")

    def on_save_current_tile(self) -> None:
        if self.result is None:
            return
        tc, tr = self.selected_tile
        sub, _ = self.result.slice_tile(tc, tr, TILE_SIZE)
        stem = self.source_path.stem if self.source_path else "converted"
        default_name = f"{stem}_tile_{tc + 1}_{tr + 1}_{sub.width}x{sub.height}.png"
        save_path = filedialog.asksaveasfilename(
            title=f"칸 ({tc + 1}, {tr + 1}) 저장",
            defaultextension=".png",
            initialfile=default_name,
            filetypes=[("PNG 이미지", "*.png")],
        )
        if not save_path:
            return
        sub.to_image(scale=1).save(save_path)
        self.var_status.set(f"칸 ({tc + 1}, {tr + 1}) PNG 저장 완료: {save_path}")

    def export_progress_dict(self) -> dict:
        return {
            "width": self.result.width if self.result else 0,
            "height": self.result.height if self.result else 0,
            "selected_tile": list(self.selected_tile),
            "completed_tiles": [list(t) for t in sorted(self.completed_tiles)],
            "completed_colors": {
                f"{tc},{tr}": sorted(pidxs)
                for (tc, tr), pidxs in self.completed_colors.items()
                if pidxs
            },
        }

    def import_progress_dict(self, data: dict) -> None:
        self.completed_tiles = {tuple(t) for t in data.get("completed_tiles", [])}
        self.completed_colors = {}
        for key, pidxs in data.get("completed_colors", {}).items():
            tc_s, tr_s = key.split(",")
            self.completed_colors[(int(tc_s), int(tr_s))] = {int(p) for p in pidxs}
        sel = tuple(data.get("selected_tile", [0, 0]))
        if sel in self._tile_list:
            self.selected_tile = sel
        self._load_selected_tile()

    def on_save_progress(self) -> None:
        if self.result is None:
            return
        stem = self.source_path.stem if self.source_path else "drawing"
        save_path = filedialog.asksaveasfilename(
            title="작업 진행 상태 저장",
            defaultextension=".json",
            initialfile=f"{stem}_progress.json",
            filetypes=[("JSON 파일", "*.json")],
        )
        if not save_path:
            return
        Path(save_path).write_text(
            json.dumps(self.export_progress_dict(), ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        self.var_status.set(f"진행 상태 저장됨: {Path(save_path).name}")

    def on_load_progress(self) -> None:
        if self.result is None:
            return
        load_path = filedialog.askopenfilename(
            title="작업 진행 상태 불러오기",
            filetypes=[("JSON 파일", "*.json")],
        )
        if not load_path:
            return
        try:
            data = json.loads(Path(load_path).read_text(encoding="utf-8"))
            self.import_progress_dict(data)
        except Exception as exc:
            messagebox.showerror("불러오기 실패", str(exc))
            return
        self.var_status.set(
            f"진행 상태 불러옴 ({len(self.completed_tiles)}/{len(self._tile_list)} 칸 완료)"
        )


def main() -> None:
    root = tk.Tk()
    DrawingHelperApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()
