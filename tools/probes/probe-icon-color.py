import numpy as np
from PIL import Image
def probe(path, regions, label):
    img=Image.open(path).convert("RGB"); a=np.asarray(img).astype(np.int16)
    print("==",label,img.size)
    for name,(x0,y0,x1,y1) in regions.items():
        sub=a[y0:y1+1,x0:x1+1].reshape(-1,3)
        lum=0.299*sub[:,0]+0.587*sub[:,1]+0.114*sub[:,2]
        k=np.argsort(lum)[-12:]
        top=sub[k]
        print(f"  {name}: 最亮12像素均值 RGB={top.mean(axis=0).round(1)} max={sub[lum.argmax()]}")
p1=r"C:\Work\Server\f-v2\clover-project-cs16\原版资源\参照物\viewmodel\viewmodel_deagle_640x480.png"
probe(p1,{"healthIcon":(8,455,24,474),"healthNum":(28,455,60,475),"armorIcon":(126,455,144,474),"moneyIcon":(280,455,296,474),"ammoNum":(574,455,632,474)},"640x480 FPS HUD")
p2=r"C:\Work\Server\f-v2\clover-project-cs16\原版资源\参照物\hud\hud_ingame_full.png"
probe(p2,{"healthIcon":(6,320,18,336),"healthNum":(22,320,46,336),"ammoNum":(560,320,600,336)},"640x360 FPS HUD")
