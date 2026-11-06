from PIL import Image
import os
SRC = r"logs\clean"; DST = r"assets\screenshots"
def save(im,out):
    im.save(os.path.join(DST,out)); print("saved",out,im.size)
def crop(name,out,box):
    im=Image.open(os.path.join(SRC,name)).crop(box); save(im,out)
# 更紧凑的右侧裁剪
crop("clean-pet.png","app-pet.png",(1880,450,2520,1584))
crop("clean-bubble.png","app-bubble.png",(1300,780,2520,1584))
crop("clean-pick.png","app-pick.png",(1300,780,2520,1584))
crop("clean-functions.png","app-functions.png",(1450,560,2520,1584))
# 菜单窗口位于屏幕左侧：保留全屏，只裁任务栏
save(Image.open(os.path.join(SRC,"clean-menu.png")).crop((0,0,2520,1584)),"app-menu.png")
