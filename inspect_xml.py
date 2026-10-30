import zipfile, re
from lxml import etree
p = r"C:\Users\qmy\Desktop\《班机》开发文档.docx"
with zipfile.ZipFile(p) as z:
    xml = z.read("word/document.xml")
root = etree.fromstring(xml)
ns = {"w":"http://schemas.openxmlformats.org/wordprocessingml/2006/main"}
# body-level children types
body = root.find("w:body", ns)
for i, ch in enumerate(body):
    tag = etree.QName(ch).localname
    if tag == "p":
        text = "".join(ch.itertext())
        print(f"BODY[{i}] p: {text[:200]!r}")
    else:
        print(f"BODY[{i}] {tag}")
print("=== DRAWING COUNT ===", len(root.findall(".//w:drawing", ns)))
# print textboxes with geometry
for d in root.findall(".//w:pict", ns) + root.findall(".//w:drawing", ns):
    s = etree.tostring(d, encoding="unicode")
    print("----")
    print(s[:1500])
