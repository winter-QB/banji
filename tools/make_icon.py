from PIL import Image, ImageDraw
S = 512
img = Image.new("RGBA", (S, S), (0,0,0,0))
d = ImageDraw.Draw(img)
# gradient body
grad = Image.new("RGBA", (S, S))
gd = ImageDraw.Draw(grad)
for y in range(S):
    t = y / S
    r = int(0x31 + (0x00-0x31)*t)
    g = int(0x7F + (0x51-0x7F)*t)
    b = int(0xFF + (0xD6-0xFF)*t)
    gd.line([(0,y),(S,y)], fill=(r,g,b,255))
mask = Image.new("L", (S,S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([16,16,S-16,S-16], radius=120, fill=255)
img.paste(grad, (0,0), mask)
# eyes: rounded rects, white with dark pupils, cute
# left eye
d.rounded_rectangle([120,150,232,330], radius=56, fill=(255,255,255,255))
d.rounded_rectangle([280,150,392,330], radius=56, fill=(255,255,255,255))
d.ellipse([162,205,226,290], fill=(43,58,103,255))
d.ellipse([322,205,386,290], fill=(43,58,103,255))
# eye highlights
d.ellipse([174,218,198,242], fill=(255,255,255,255))
d.ellipse([334,218,358,242], fill=(255,255,255,255))
# small blush dots
d.ellipse([92,320,132,352], fill=(255,180,150,120))
d.ellipse([380,320,420,352], fill=(255,180,150,120))
img = img.resize((256,256), Image.LANCZOS)
img.save(r"src\Banji\Assets\banji.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
img.save(r"assets\icons\banji_256.png")
print("icon ok")
