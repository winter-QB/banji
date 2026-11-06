# -*- coding: utf-8 -*-
"""生成《班机》应用图标（v1.1 方形宠物版）。

设计（按用户第二轮要求）：
- 方形、扁扁的方块小生物：身体是宽 > 高的圆角小方块；
- 两只小手、两只小脚都是小正方形；
- 两只眼睛是纯黑实心小圆点；
- 没有腮红、鼻子、嘴巴、高光；
- 配色不用橙色，用雾蓝色系：身体 #8FB6F2、手足/阴影 #6E96DA；
- 透明背景，多尺寸 ico（16~256），主程序和安装器同款。

输出：
- src/Banji/Assets/banji.ico           （主程序 csproj 引用）
- src/Banji.Installer/Assets/banji.ico （安装器同款）
- assets/icons/banji.ico / banji_*.png （README/展示/安装器欢迎页）

用法：python assets/icons/make_icon.py（工作目录为仓库根目录）
"""
from pathlib import Path

from PIL import Image, ImageDraw

S = 1024

BODY_TOP = (159, 194, 245, 255)     # #9FC2F5（比主色略亮的顶部）
BODY = (143, 182, 242, 255)         # #8FB6F2 身体主色
SHADE = (110, 150, 218, 255)        # #6E96DA 手足 / 描边 / 底部阴影
EYE_BLACK = (0, 0, 0, 255)


def make_master() -> Image.Image:
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 身体：宽 > 高的圆角小方块（扁扁的）
    body = (172, 262, 852, 742)      # 680 x 480
    radius = 130

    # 底部阴影层（先画在身体后面，露出一点深色底边）
    ImageDraw.Draw(img).rounded_rectangle(
        (body[0], body[1] + 26, body[2], body[3] + 26), radius=radius, fill=SHADE
    )

    # 身体渐变（上亮下暗，幅度很小，保持扁平感）
    body_mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(body_mask).rounded_rectangle(body, radius=radius, fill=255)
    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    y0, y1 = body[1], body[3]
    for y in range(y0, y1 + 1):
        t = (y - y0) / max(1, (y1 - y0))
        r = round(BODY_TOP[0] + (BODY[0] - BODY_TOP[0]) * t)
        g = round(BODY_TOP[1] + (BODY[1] - BODY_TOP[1]) * t)
        b = round(BODY_TOP[2] + (BODY[2] - BODY_TOP[2]) * t)
        gd.line([(0, y), (S, y)], fill=(r, g, b, 255))
    img.paste(grad, (0, 0), body_mask)

    draw = ImageDraw.Draw(img)

    # 两侧的小方块手（略低于身体中线，像垂着的小手）
    hand = 118
    hand_y = 508
    for x in (body[0] - 74, body[2] + 74 - hand):
        draw.rounded_rectangle((x, hand_y, x + hand, hand_y + hand), radius=30, fill=SHADE)

    # 身体正面的两只小方块脚（在身体下方，露出下缘）
    foot_w, foot_h = 150, 112
    foot_y = body[3] - 42
    for cx in (S // 2 - 168, S // 2 + 168):
        draw.rounded_rectangle(
            (cx - foot_w // 2, foot_y, cx + foot_w // 2, foot_y + foot_h), radius=30, fill=SHADE
        )

    # 身体描边：与手足同色，保证 16px 小图标也有清晰轮廓
    draw.rounded_rectangle(body, radius=radius, outline=SHADE, width=14)

    # 眼睛：两个纯黑实心圆点（无高光/无眼白/无瞳孔）
    eye_r = 58
    eye_y = 452
    eye_dx = 118
    for cx in (S // 2 - eye_dx, S // 2 + eye_dx):
        draw.ellipse((cx - eye_r, eye_y - eye_r, cx + eye_r, eye_y + eye_r), fill=EYE_BLACK)

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
        if not path.parent.exists():
            continue  # 对应工程目录尚未创建时跳过
        path.parent.mkdir(parents=True, exist_ok=True)
        master.save(path, format="ICO", sizes=ico_sizes)
        print(f"ico  -> {path.relative_to(root)}")

    png_dir = root / "assets" / "icons"
    for size in (16, 32, 48, 256, 512):
        out = png_dir / f"banji_{size}.png"
        master.resize((size, size), Image.LANCZOS).save(out)
        print(f"png  -> {out.relative_to(root)}")

    # 安装器工程里也放一份 256px png（窗口/欢迎页用）
    installer_assets = root / "src" / "Banji.Installer" / "Assets"
    if installer_assets.exists():
        master.resize((256, 256), Image.LANCZOS).save(installer_assets / "banji_256.png")
        print("png  -> src/Banji.Installer/Assets/banji_256.png")


if __name__ == "__main__":
    save_all(make_master())
    print("图标生成完成（v1.1 方形雾蓝宠物）")
