import sys
from docx import Document
p = r"C:\Users\qmy\Desktop\《班机》开发文档.docx"
d = Document(p)
print("PARAS", len(d.paragraphs), "TABLES", len(d.tables), "SECTIONS", len(d.sections))
for i, para in enumerate(d.paragraphs):
    t = para.text
    if t.strip():
        print(f"[{i}] <{para.style.name}> {t}")
print("=== TABLES ===")
for ti, tbl in enumerate(d.tables):
    print(f"--- table {ti} rows={len(tbl.rows)} cols={len(tbl.columns)}")
    for r in tbl.rows:
        print(" | ".join(c.text.replace("\n"," / ") for c in r.cells))
