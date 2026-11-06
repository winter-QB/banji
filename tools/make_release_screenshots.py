from PIL import Image
import os

SRC = r"logs\clean"
DST = r"assets\screenshots"

# 本机全屏截图 2520x1680；底部任务栏约 84px，统一裁掉（全屏覆盖类提示除外）
CONTENT_H = 1584

def save(im, out):
    im.save(os.path.join(DST, out))
    print("saved", out, im.size)

def crop_right(name, out, x0, y0=0):
    im = Image.open(os.path.join(SRC, name)).crop((x0, y0, 2520, CONTENT_H))
    save(im, out)

def crop_full(name, out, cut_taskbar=True):
    im = Image.open(os.path.join(SRC, name))
    if cut_taskbar:
        im = im.crop((0, 0, 2520, CONTENT_H))
    save(im, out)

# 右侧竖条：宠物 / 气泡 / 功能区 / 菜单 / 抽签气泡
crop_right("clean-pet.png", "app-pet.png", 1950, 200)
crop_right("clean-bubble.png", "app-bubble.png", 1350, 0)
crop_right("clean-functions.png", "app-functions.png", 1620, 80)
crop_right("clean-menu.png", "app-menu.png", 1350, 0)
crop_right("clean-pick.png", "app-pick.png", 1620, 0)

# 窗口/全屏：裁掉任务栏
for name, out in [
    ("clean-settings.png", "app-settings.png"),
    ("clean-dashboard.png", "app-dashboard.png"),
    ("clean-grouping.png", "app-grouping.png"),
    ("clean-ring-calibration.png", "app-calibration.png"),
    ("clean-rosters.png", "app-rosters.png"),
    ("clean-timetable.png", "app-timetable.png"),
    ("clean-notice-end.png", "app-notice-end.png"),
]:
    crop_full(name, out)

# 上课提示 / 计时器超时：全屏覆盖，保留完整（含底部小字）
crop_full("clean-notice-start.png", "app-notice-start.png", cut_taskbar=False)
crop_full("clean-timeout.png", "app-timer-timeout.png", cut_taskbar=False)
