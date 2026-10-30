# -*- coding: utf-8 -*-
"""生成《班机》内置样例模板（openpyxl）。

输出到 src/Banji/Assets/Templates/：
- 花名册示例.xlsx：sheet1 带表头（学号/姓名/成绩）、sheet2 无表头仅姓名；
- 课程表示例.xlsx：4 个 sheet 分别演示 4 种课表布局。

用法（工作目录为仓库根目录）：
    <workspace-python> build/gen_templates.py
"""
from datetime import time
from pathlib import Path

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

ROOT = Path(__file__).resolve().parents[1]
OUT_DIR = ROOT / "src" / "Banji" / "Assets" / "Templates"

ORANGE = "F2762C"
ORANGE_LIGHT = "FFE3D3"
BLUE = "317FFF"
BLUE_LIGHT = "DCE8FF"
GREY = "F2F4F7"

header_fill = PatternFill("solid", fgColor=ORANGE)
header_fill2 = PatternFill("solid", fgColor=BLUE)
header_font = Font(name="微软雅黑", bold=True, color="FFFFFF", size=11)
body_font = Font(name="微软雅黑", size=10.5)
thin = Side(style="thin", color="D0D5DD")
border = Border(left=thin, right=thin, top=thin, bottom=thin)


# =============================================================================
# 花名册示例
# =============================================================================

STUDENTS = [
    ("2025030101", "王子涵", 92), ("2025030102", "李思远", 85), ("2025030103", "张雨晴", 78),
    ("2025030104", "刘一鸣", 88), ("2025030105", "陈嘉怡", 95), ("2025030106", "杨浩然", 73),
    ("2025030107", "赵欣妍", 81), ("2025030108", "黄子轩", 69), ("2025030109", "周雨桐", 90),
    ("2025030110", "吴俊杰", 76), ("2025030111", "徐佳琪", 87), ("2025030112", "孙铭泽", 83),
    ("2025030113", "马晓彤", 79), ("2025030114", "朱天宇", 91), ("2025030115", "胡诗涵", 84),
    ("2025030116", "郭子豪", 72), ("2025030117", "何雨欣", 89), ("2025030118", "高文博", 77),
    ("2025030119", "林可欣", 93), ("2025030120", "罗浩宇", 68), ("2025030121", "郑雅琪", 86),
    ("2025030122", "梁晨曦", 80), ("2025030123", "谢明轩", 74), ("2025030124", "宋佳颖", 82),
    ("2025030125", "唐子墨", 94), ("2025030126", "许若曦", 71), ("2025030127", "邓宇辰", 75),
    ("2025030128", "冯思琪", 88), ("2025030129", "曹阳阳", 67), ("2025030130", "彭雨萌", 90),
    ("2025030131", "陆一凡", 85), ("2025030132", "苏念慈", 96), ("2025030133", "卢天佑", 70),
    ("2025030134", "蒋雨婷", 83), ("2025030135", "蔡子健", 78), ("2025030136", "余思彤", 92),
    ("2025030137", "杜明远", 74), ("2025030138", "叶欣然", 87), ("2025030139", "程子航", 81),
    ("2025030140", "戴雨薇", 91),
]


def style_header_row(ws, row: int, fill=header_fill):
    for cell in ws[row]:
        cell.fill = fill
        cell.font = header_font
        cell.alignment = Alignment(horizontal="center", vertical="center")
        cell.border = border


def style_body(ws, row_from: int, row_to: int, col_from: int, col_to: int):
    for r in range(row_from, row_to + 1):
        for c in range(col_from, col_to + 1):
            cell = ws.cell(row=r, column=c)
            cell.font = body_font
            cell.border = border
            cell.alignment = Alignment(horizontal="center", vertical="center")


def build_roster(path: Path):
    wb = Workbook()

    # --- sheet1：带表头（学号 / 姓名 / 成绩） ---
    ws1 = wb.active
    ws1.title = "带表头-学号姓名成绩"
    ws1.append(["学号", "姓名", "成绩"])
    style_header_row(ws1, 1)
    for row in STUDENTS:
        ws1.append(list(row))
    style_body(ws1, 2, 1 + len(STUDENTS), 1, 3)
    # 成绩按分数从高到低着色，只是让模板好看一点，导入时只读数值
    for r in range(2, 2 + len(STUDENTS)):
        score = ws1.cell(row=r, column=3).value
        if score >= 90:
            ws1.cell(row=r, column=3).fill = PatternFill("solid", fgColor="D9F5DF")
        elif score < 75:
            ws1.cell(row=r, column=3).fill = PatternFill("solid", fgColor="FFE0E0")
    widths = {"A": 16, "B": 12, "C": 9}
    for col, w in widths.items():
        ws1.column_dimensions[col].width = w
    ws1.freeze_panes = "A2"

    # --- sheet2：无表头，仅姓名（用来试试“自动识别”） ---
    ws2 = wb.create_sheet("无表头-仅姓名")
    for _, name, _ in STUDENTS[::2]:  # 隔一个取一个，模拟另一种名单
        ws2.append([name])
    style_body(ws2, 1, ws2.max_row, 1, 1)
    ws2.column_dimensions["A"].width = 14

    path.parent.mkdir(parents=True, exist_ok=True)
    wb.save(path)
    print(f"生成 {path.relative_to(ROOT)}  ({ws1.max_row - 1} 名学生)")


# =============================================================================
# 课程表示例
# =============================================================================

PERIOD_TIMES = [
    (time(8, 0), time(8, 45), "第1节"),
    (time(8, 55), time(9, 40), "第2节"),
    (time(10, 0), time(10, 45), "第3节"),
    (time(10, 55), time(11, 40), "第4节"),
    (time(14, 0), time(14, 45), "第5节"),
    (time(14, 55), time(15, 40), "第6节"),
    (time(16, 0), time(16, 45), "第7节"),
    (time(16, 55), time(17, 40), "第8节"),
]

DAYS = ["周一", "周二", "周三", "周四", "周五"]

# 每天 8 节课的课程安排（与 user 学校常见设置接近）
COURSES = {
    "周一": ["语文", "数学", "英语", "物理", "化学", "生物", "体育", "班会"],
    "周二": ["数学", "语文", "英语", "化学", "物理", "历史", "信息技术", "自习"],
    "周三": ["英语", "数学", "语文", "生物", "地理", "思想政治", "音乐", "体育"],
    "周四": ["物理", "化学", "数学", "语文", "英语", "通用技术", "历史", "自习"],
    "周五": ["数学", "英语", "语文", "地理", "思想政治", "生物", "美术", "心理健康"],
}

TIME_FMT = "hh:mm"


def _fill_layout1(ws):
    """布局 1：首行是星期（横表头），首列是节次，带开始/结束时间列。"""
    ws.append(["节次", "开始时间", "结束时间", *DAYS])
    style_header_row(ws, 1)
    for i, (start, end, label) in enumerate(PERIOD_TIMES):
        ws.append([label, start, end, *(COURSES[d][i] for d in DAYS)])
        r = ws.max_row
        ws.cell(row=r, column=2).number_format = TIME_FMT
        ws.cell(row=r, column=3).number_format = TIME_FMT
    style_body(ws, 2, ws.max_row, 1, 3 + len(DAYS))
    for col, w in {"A": 10, "B": 11, "C": 11}.items():
        ws.column_dimensions[col].width = w
    for c in range(4, 4 + len(DAYS)):
        ws.column_dimensions[get_column_letter(c)].width = 11
    ws.freeze_panes = "D2"


def _fill_layout2(ws):
    """布局 2：首列是星期（纵表头），首行是节次（转置布局）。"""
    ws.append(["星期", *(p[2] for p in PERIOD_TIMES)])
    style_header_row(ws, 1, header_fill2)
    for d in DAYS:
        ws.append([d, *COURSES[d]])
    style_body(ws, 2, ws.max_row, 1, 1 + len(PERIOD_TIMES))
    ws.column_dimensions["A"].width = 10
    for c in range(2, 2 + len(PERIOD_TIMES)):
        ws.column_dimensions[get_column_letter(c)].width = 11
    ws.freeze_panes = "B2"


def _fill_layout3(ws):
    """布局 3：长表，每行一节课，适合从教务系统导出的明细表。"""
    ws.append(["星期", "节次", "课程", "开始时间", "结束时间"])
    style_header_row(ws, 1)
    for d in DAYS:
        for i, (start, end, label) in enumerate(PERIOD_TIMES):
            ws.append([d, label, COURSES[d][i], start, end])
            r = ws.max_row
            ws.cell(row=r, column=4).number_format = TIME_FMT
            ws.cell(row=r, column=5).number_format = TIME_FMT
    style_body(ws, 2, ws.max_row, 1, 5)
    for col, w in {"A": 9, "B": 9, "C": 12, "D": 11, "E": 11}.items():
        ws.column_dimensions[col].width = w
    ws.freeze_panes = "A2"


def _fill_layout4(ws):
    """布局 4：表头带时间区间（如 08:00-08:45），首列是星期。"""
    ws.append(["节次", *(f"{p[0].strftime('%H:%M')}-{p[1].strftime('%H:%M')}" for p in PERIOD_TIMES)])
    style_header_row(ws, 1, header_fill2)
    for d in DAYS:
        ws.append([d, *COURSES[d]])
    style_body(ws, 2, ws.max_row, 1, 1 + len(PERIOD_TIMES))
    ws.column_dimensions["A"].width = 10
    for c in range(2, 2 + len(PERIOD_TIMES)):
        ws.column_dimensions[get_column_letter(c)].width = 16
    ws.freeze_panes = "B2"


def build_timetable(path: Path):
    wb = Workbook()
    ws1 = wb.active
    ws1.title = "布局1-节次行×星期列"
    _fill_layout1(ws1)

    ws2 = wb.create_sheet("布局2-星期行×节次列")
    _fill_layout2(ws2)

    ws3 = wb.create_sheet("布局3-长表每行一节")
    _fill_layout3(ws3)

    ws4 = wb.create_sheet("布局4-时间表头")
    _fill_layout4(ws4)

    path.parent.mkdir(parents=True, exist_ok=True)
    wb.save(path)
    print(f"生成 {path.relative_to(ROOT)}  ({len(wb.sheetnames)} 种布局)")


if __name__ == "__main__":
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    build_roster(OUT_DIR / "花名册示例.xlsx")
    build_timetable(OUT_DIR / "课程表示例.xlsx")
