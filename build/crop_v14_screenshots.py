# -*- coding: utf-8 -*-
"""把 v1.4 QA 截图整理成 README 用的应用截图（v1.4：值周值日 + 单双周）。

来源：logs/v14-*.png（pet-ui / features-ui / core-schedule 的窗口级截图，Lead 已整理）
输出：assets/screenshots/app-*.png
用法：<workspace-python> build/crop_v14_screenshots.py
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
LOGS = ROOT / "logs"
OUT = ROOT / "assets" / "screenshots"

# (源文件名, 输出文件名, 最大宽度)
MAPPING = [
    ("v14-wizard-step1.png", "app-duty-setup.png", 1000),
    ("v14-duty-week.png", "app-duty-window.png", 1400),
    ("v14-duty-groups.png", "app-duty-groups.png", 1400),
    ("v14-blackboard-top.png", "app-duty-pet.png", 560),
    ("v14-folded-duty.png", "app-duty-folded.png", 560),
    ("v14-afterschool-duty.png", "app-duty-afterschool.png", 560),
    ("v14-parity-timetable-odd.png", "app-parity-timetable.png", 1400),
    ("v14-settings-parity.png", "app-parity-settings.png", 1400),
]


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for src_name, dst_name, max_width in MAPPING:
        src = LOGS / src_name
        if not src.exists():
            print(f"跳过（源不存在）：{src_name}")
            continue
        img = Image.open(src).convert("RGB")
        if img.width > max_width:
            img = img.resize((max_width, round(img.height * max_width / img.width)), Image.LANCZOS)
        img.save(OUT / dst_name)
        print(f"{dst_name}: {img.size}")


if __name__ == "__main__":
    main()
