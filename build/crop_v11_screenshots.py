# -*- coding: utf-8 -*-
"""把 v1.1 QA 全屏截图裁剪成 README 用的应用截图（第二轮）。

- 窗口类截图：自动检测浅色窗口区域（班机窗口浅色主题、QA 桌面深色）；
- 全屏提示：等比缩小；
- 宠物类截图：按相对坐标裁剪屏幕右侧区域。

输出到 assets/screenshots/app-*.png。
用法：<workspace-python> build/crop_v11_screenshots.py
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
LOGS = ROOT / "logs"
OUT = ROOT / "assets" / "screenshots"


def _finish(img: Image.Image, dst_name: str, max_width: int) -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    if img.width > max_width:
        img = img.resize((max_width, round(img.height * max_width / img.width)), Image.LANCZOS)
    img.save(OUT / dst_name)
    print(f"{dst_name}: {img.size}")


def window_crop(src: Path, dst_name: str, max_width: int = 1100, pad: int = 10) -> None:
    """检测截图中最大的一块浅色矩形（班机窗口）并裁剪。"""
    import numpy as np

    img = Image.open(src).convert("RGB")
    arr = np.asarray(img).astype(np.int16)
    light = arr.min(axis=2) > 170
    rows = np.where(light.sum(axis=1) > 500)[0]
    cols = np.where(light.sum(axis=0) > 500)[0]
    if len(rows) == 0 or len(cols) == 0:
        print(f"[跳过] 没检测到窗口：{src.name}")
        return
    box = (max(0, int(cols.min()) - pad), max(0, int(rows.min()) - pad),
           min(img.width, int(cols.max()) + pad), min(img.height, int(rows.max()) + pad))
    _finish(img.crop(box), dst_name, max_width)


def region_crop(src: Path, dst_name: str, rel_box, max_width: int = 760) -> None:
    img = Image.open(src).convert("RGB")
    w, h = img.size
    box = (round(rel_box[0] * w), round(rel_box[1] * h), round(rel_box[2] * w), round(rel_box[3] * h))
    _finish(img.crop(box), dst_name, max_width)


def scale_copy(src: Path, dst_name: str, max_width: int = 1400) -> None:
    _finish(Image.open(src).convert("RGB"), dst_name, max_width)


def main() -> None:
    acc = LOGS / "acceptance-v11"
    # 优先使用 Lead 的最终验收截图；没有时回退到队友 QA 图
    jobs = {
        "app-pet": (acc / "01-pet-default.png", "region", (0.848, 0.385, 0.995, 0.885)),
        "app-bubble": (acc / "04-pet-long-quip.png", "region", (0.50, 0.52, 0.995, 0.89)),
        "app-menu": (acc / "07-pet-menu.png", "region", (0.040, 0.050, 0.235, 0.720)),
        "app-functions": (LOGS / "zoom-v11-functions.png", "scale", 720),
        "app-settings": (acc / "08b-settings-backup.png", "window", None),
        "app-dashboard": (acc / "09-dashboard.png", "window", None),
        "app-rosters": (acc / "10-rosters.png", "window", None),
        "app-grouping": (acc / "11-grouping.png", "window", None),
        "app-calibration": (acc / "12-ring-calibration.png", "window", None),
        "app-notice-start": (acc / "13-notice-start.png", "scale", 1400),
        "app-notice-end": (acc / "14-notice-end.png", "scale", 1400),
        "app-timer-timeout": (acc / "15-timer-timeout.png", "scale", 1400),
    }
    for name, (path, kind, arg) in jobs.items():
        if not path.exists():
            print(f"[跳过] 源文件不存在：{path.name}")
            continue
        if kind == "window":
            window_crop(path, f"{name}.png")
        elif kind == "region":
            region_crop(path, f"{name}.png", arg)
        else:
            scale_copy(path, f"{name}.png", arg)


if __name__ == "__main__":
    main()
