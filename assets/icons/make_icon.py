# -*- coding: utf-8 -*-
"""生成《班机》应用图标。

设计（按最终用户要求）：
- 橙色系、圆润的简单几何身体；
- 眼睛是两个纯黑实心小圆点；
- 无腮红、无鼻子、无嘴巴、无眼白/高光/瞳孔；
- 透明背景（圆润身体本身即轮廓）。

输出：
- src/Banji/Assets/banji.ico          （多尺寸 16~256，主程序 csproj 引用）
- src/Banji.Installer/Assets/banji.ico（安装器同款，构建脚本会同步；不存在目录时跳过）
- assets/icons/banji_256.png / banji_512.png（README 等展示用）

用法：python assets/icons/make_icon.py（工作目录为仓库根目录）
"""
from pathlib import Path

from PIL import Image, ImageDraw

# 512 母版尺寸（再按需缩放）
S = 1024

# 品牌橙（与气泡/按钮的蓝色系形成暖色对比）
ORANGE_TOP = (255, 166, 90, 255)    # #FFA65A
ORANGE_BOTTOM = (242, 118, 44, 255)  # #F2762C
ORANGE_EDGE = (214, 94, 28, 255)     # #D65E1C 极细描边，保证小尺寸也清晰
EYE_BLACK = (0, 0, 0, 255)


def make_master() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # 身体：圆润的竖向胶囊（上圆下圆），边缘留白适中
    body = (162, 132, 862, 892)  # 700 x 760
    radius = 330

    # 逐行渐变（只作用于身体内部）
    body_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(body_mask).rounded_rectangle(body, radius=radius, fill=255)
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    y0, y1 = body[1], body[3]
    for y in range(y0, y1 + 1):
        t = (y - y0) / max(1, (y1 - y0))
        r = round(ORANGE_TOP[0] + (ORANGE_BOTTOM[0] - ORANGE_TOP[0]) * t)
        g = round(ORANGE_TOP[1] + (ORANGE_BOTTOM[1] - ORANGE_TOP[1]) * t)
        b = round(ORANGE_TOP[2] + (ORANGE_BOTTOM[2] - ORANGE_TOP[2]) * t)
        gd.line([(0, y), (S, y)], fill=(r, g, b, 255))
    img.paste(grad, (0, 0), body_mask)

    # 极细的深橙描边（不算高光，只增强小尺寸轮廓辨识度）
    draw.rounded_rectangle(body, radius=radius, outline=ORANGE_EDGE, width=10)

    # 眼睛：两个纯黑实心小圆点（简化到极致，无任何高光/瞳孔）
    eye_r = 86
    eye_y = 445
    eye_dx = 128
    for cx in (S // 2 - eye_dx, S // 2 + eye_dx):
        draw.ellipse([cx - eye_r, eye_y - eye_r, cx + eye_r, eye_y + eye_r], fill=EYE_BLACK)

    return img


def save_all(master: Image.Image) -> None:
    root = Path(__file__).resolve().parents[2]
    ico_sizes = [(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48),
                 (64, 64), (96, 96), (128, 128), (256, 256)]

    ico_paths = [
        root / "src" / "Banji" / "Assets" / "banji.ico",
        root / "src" / "Banji.Installer" / "Assets" / "banji.ico",
        root / "assets" / "icons" / "banji.ico",
    ]
    for path in ico_paths:
        if path.name == "banji.ico" and path.parent.name == "Assets" and "Installer" in str(path) \
                and not path.parent.parent.exists():
            continue  # 安装器工程尚未创建时跳过
        path.parent.mkdir(parents=True, exist_ok=True)
        # 直接从母版缩放各尺寸后合成 ICO，避免中间尺寸模糊
        master.save(path, format="ICO", sizes=ico_sizes)
        print(f"ico  -> {path.relative_to(root)}")

    png_dir = root / "assets" / "icons"
    for size in (16, 32, 48, 256, 512):
        out = png_dir / f"banji_{size}.png"
        master.resize((size, size), Image.LANCZOS).save(out)
        print(f"png  -> {out.relative_to(root)}")

    # 安装器工程里也放一份 256px png（窗口/欢迎页用），目录不存在时跳过
    installer_assets = root / "src" / "Banji.Installer" / "Assets"
    if installer_assets.exists():
        master.resize((256, 256), Image.LANCZOS).save(installer_assets / "banji_256.png")
        print("png  -> src/Banji.Installer/Assets/banji_256.png")


if __name__ == "__main__":
    save_all(make_master())
    print("图标生成完成")
