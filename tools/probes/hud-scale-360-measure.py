import numpy as np
from PIL import Image
src=r"C:\Work\Server\f-v2\clover-project-cs16\原版资源\参照物\hud\hud_ingame_full.png"
img=Image.open(src).convert("RGB"); a=np.asarray(img).astype(np.int16); h,w,_=a.shape
print("size",w,"x",h)
r,g,b=a[:,:,0],a[:,:,1],a[:,:,2]
ink=(r-b>25)&(r>85)
# 找游戏的上下黑边（整行全暗）
rowlum=(0.299*r+0.587*g+0.114*b).mean(axis=1)
dark=np.where(rowlum<25)[0]
print("dark rows head:",dark[:6],"tail:",dark[-6:] if len(dark) else None)
band=ink[h-40:h,:]
cc=band.sum(axis=0)
runs=[];inr=False
for x in range(w):
    if cc[x]>0 and not inr: st=x;inr=True
    elif cc[x]==0 and inr:
        if x-st>=2: runs.append((st,x-1))
        inr=False
if inr: runs.append((st,w-1))
print("col runs(640x360):",runs[:12])
