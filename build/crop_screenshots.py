# -*- coding: utf-8 -*-
"""把 QA 全屏截图裁剪成 README 用的应用截图。

- 窗口类截图：自动检测浅色窗口区域（班机窗口都是浅色主题，QA 桌面背景是深色）；
- 全屏提示：直接等比缩小；
- 宠物类截图：按固定区域裁剪屏幕右侧。

输出到 assets/screenshots/app-*.png。
用法：<workspace-python> build/crop_screenshots.py
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
LOGS = ROOT / "logs"
OUT = ROOT / "assets" / "screenshots"


def window_crop(src: Path, dst_name: str, max_width: int = 1100, pad: int = 10) -> None:
    """检测截图中最大的一块浅色矩形（班机窗口）并裁剪。"""
    import numpy as np

    img = Image.open(src).convert("RGB")
    arr = np.asarray(img).astype(np.int16)
    light = arr.min(axis=2) > 170  # 三通道都亮 → 浅色窗口/白色背景

    def longest_run(mask_1d, min_count):
        idx = np.where(mask_1d > min_count)[0]
        if len(idx) == 0:
            return None
        best = (idx[0], idx[0])
        start = prev = idx[0]
        for i in idx[1:]:
            if i == prev + 1:
                prev = i
            else:
                if prev - start > best[1] - best[0]:
                    best = (start, prev)
                start = prev = i
        if prev - start > best[1] - best[0]:
            best = (start, prev)
        return best

    rows = light.sum(axis=1)
    cols = light.sum(axis=0)
    r = longest_run(rows, 150)
    c = longest_run(cols, 150)
    if r is None or c is None:
        raise SystemExit(f"未检测到窗口区域：{src}")
    box = (max(0, c[0] - pad), max(0, r[0] - pad), min(img.width, c[1] + pad), min(img.height, r[1] + pad))
    crop = img.crop(box)
    if crop.width > max_width:
        crop = crop.resize((max_width, round(crop.height * max_width / crop.width)), Image.LANCZOS)
    crop.save(OUT / dst_name)
    print(f"{dst_name}: {box} -> {crop.size}")


def region_crop(src: Path, dst_name: str, rel_box, max_width: int = 760) -> None:
    """按相对坐标 (x0,y0,x1,y1) 裁剪。"""
    img = Image.open(src).convert("RGB")
    w, h = img.size
    box = (round(rel_box[0] * w), round(rel_box[1] * h), round(rel_box[2] * w), round(rel_box[3] * h))
    crop = img.crop(box)
    if crop.width > max_width:
        crop = crop.resize((max_width, round(crop.height * max_width / crop.width)), Image.LANCZOS)
    crop.save(OUT / dst_name)
    print(f"{dst_name}: {box} -> {crop.size}")


def scale_copy(src: Path, dst_name: str, max_width: int = 1400) -> None:
    img = Image.open(src).convert("RGB")
    if img.width > max_width:
        img = img.resize((max_width, round(img.height * max_width / img.width)), Image.LANCZOS)
    img.save(OUT / dst_name)
    print(f"{dst_name}: {img.size}")


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    logs = {
        "settings": LOGS / "features-04-settings-general.png",
        "dashboard": LOGS / "features-06-dashboard.png",
        "rosters": LOGS / "roster-01-manager.png",
        "timetable": LOGS / "schedule-timetable-week.png",
    }
    for name, path in logs.items():
        if path.exists():
            window_crop(path, f"app-{name}.png")

    notices = {
        "notice-start": (LOGS / "acceptance" / "10-notice-start.png", 1400),
        "notice-end": (LOGS / "acceptance" / "11-notice-end.png", 1400),
        "timer-timeout": (LOGS / "acceptance" / "12-timer-timeout.png", 1400),
    }
    for name, (path, width) in notices.items():
        if path.exists():
            scale_copy(path, f"app-{name}.png", width)

    # 宠物相关：屏幕右侧区域（宠物、课表面板、信息框、气泡、菜单都在右侧）
    pet_sources = {
        "pet": (LOGS / "acceptance" / "01-pet-default.png", (0.80, 0.33, 1.0, 0.98)),
        "pick": (LOGS / "acceptance" / "03-pet-pick-bubble.png", (0.72, 0.40, 1.0, 0.98)),
        "menu": (LOGS / "pet-14-menu.png", (0.62, 0.22, 1.0, 0.98)),
        "timer": (LOGS / "pet-13-timer-functions.png", (0.62, 0.30, 1.0, 0.98)),
    }
    for name, (path, box) in pet_sources.items():
        if path.exists():
            region_crop(path, f"app-{name}.png", box, 720)


if __name__ == "__main__":
    main()
