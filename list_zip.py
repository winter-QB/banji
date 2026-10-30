import zipfile
p = r"C:\Users\qmy\Desktop\《班机》开发文档.docx"
with zipfile.ZipFile(p) as z:
    names = z.namelist()
    print(len(names))
    for n in names: print(n)
