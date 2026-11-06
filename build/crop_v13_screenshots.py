# -*- coding: utf-8 -*-
"""把 v1.3 QA 截图整理成 README 用的应用截图（第三轮）。

v1.3 截图来源（pet-ui / core-schedule 的窗口级/局部截图）：
- logs/v13-notice-bubble-qa-start.png / -end.png  → 从全屏截图中裁出宠物旁的提醒气泡
- logs/v13-notice-timeout-qa.png                  → 计时器超时全屏（保留）
- logs/v13-arc-win.png                            → 宠物收进侧边成粗弧线
- logs/v13-folded-timetable-win.png               → 课表自动折叠态
- logs/v13-rightclick-menu.png                    → v1.3 菜单（右击打开）

输出到 assets/screenshots/app-*.png。
用法：<workspace-python> build/crop_v13_screenshots.py
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


def region_crop(src: Path, dst_name: str, rel_box, max_width: int) -> None:
    img = Image.open(src).convert("RGB")
    w, h = img.size
    box = (round(rel_box[0] * w), round(rel_box[1] * h), round(rel_box[2] * w), round(rel_box[3] * h))
    _finish(img.crop(box), dst_name, max_width)


def scale_copy(src: Path, dst_name: str, max_width: int) -> None:
    _finish(Image.open(src).convert("RGB"), dst_name, max_width)


def main() -> None:
    # 上课 / 下课气泡：从全屏截图右侧裁出气泡区域
    bubble_box = (0.846, 0.470, 0.993, 0.585)
    start = LOGS / "v13-notice-bubble-qa-start.png"
    end = LOGS / "v13-notice-bubble-qa-end.png"
    if start.exists():
        region_crop(start, "app-notice-start.png", bubble_box, 720)
    if end.exists():
        region_crop(end, "app-notice-end.png", bubble_box, 720)

    timeout = LOGS / "v13-notice-timeout-qa.png"
    if timeout.exists():
        scale_copy(timeout, "app-timer-timeout.png", 1400)

    arc = LOGS / "v13-arc-win.png"
    if arc.exists():
        scale_copy(arc, "app-pet-collapsed.png", 520)

    folded = LOGS / "v13-folded-timetable-win.png"
    if folded.exists():
        scale_copy(folded, "app-timetable-folded.png", 520)

    menu = LOGS / "v13-rightclick-menu.png"
    if menu.exists():
        scale_copy(menu, "app-menu.png", 640)


if __name__ == "__main__":
    main()
