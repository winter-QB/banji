from PIL import Image, ImageDraw
import os
src = r"assets\screenshots"
files = sorted(f for f in os.listdir(src) if f.lower().endswith(".png"))
cell_w, cell_h = 420, 320
cols, rows = 3, 5
sheet = Image.new("RGB", (cell_w*cols, cell_h*rows), (18,18,22))
draw = ImageDraw.Draw(sheet)
for i, f in enumerate(files):
    im = Image.open(os.path.join(src,f)).convert("RGB")
    im.thumbnail((cell_w-8, cell_h-30))
    x = (i%cols)*cell_w + (cell_w-im.width)//2
    y = (i//cols)*cell_h + 22
    sheet.paste(im, (x,y))
    draw.text(((i%cols)*cell_w+8, (i//cols)*cell_h+4), f, fill=(220,220,220))
sheet.save(r"logs\release-screens-contact.png")
print("ok", len(files))
