import zipfile, os, shutil
p = r"C:\Users\qmy\Desktop\《班机》开发文档.docx"
out = "docx_media"
os.makedirs(out, exist_ok=True)
with zipfile.ZipFile(p) as z:
    for n in z.namelist():
        if n.startswith("word/media/"):
            z.extract(n, out)
            print(n, z.getinfo(n).file_size)
print("---")
for n in z.namelist():
    if n.startswith("word/") and n.endswith(".xml") and ("document" in n):
        pass
