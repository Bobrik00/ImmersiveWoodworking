import math
def rotY(box, deg, origin):
    ox,oy,oz = origin
    r = math.radians(deg); cos,sin = math.cos(r), math.sin(r)
    corners=[]
    for X in (box[0],box[3]):
        for Y in (box[1],box[4]):
            for Z in (box[2],box[5]):
                x,y,z = X-ox, Y-oy, Z-oz
                xr = x*cos + z*sin
                zr = -x*sin + z*cos
                corners.append((xr+ox, y+oy, zr+oz))
    xs=[c[0] for c in corners]; ys=[c[1] for c in corners]; zs=[c[2] for c in corners]
    return (min(xs),min(ys),min(zs),max(xs),max(ys),max(zs))
def off(b,x,y,z): return (b[0]+x,b[1]+y,b[2]+z,b[3]+x,b[4]+y,b[5]+z)
def union(a,b): return (min(a[0],b[0]),min(a[1],b[1]),min(a[2],b[2]),max(a[3],b[3]),max(a[4],b[4]),max(a[5],b[5]))
TrestleBox=(-0.115,0,0.388,1.049,1.005,0.675)
LogBox=(0.146,0.580,-0.812,0.854,1.287,0.812)
BC=(0.5,0.5,0.5)
def pair(axis, isMaster, hasLog):
    ax = 1 if axis=='X' else 0; az = 1 if axis=='Z' else 0
    tm = rotY(TrestleBox,90,BC) if axis=='X' else tuple(TrestleBox)
    combined = union(tm, off(tm,ax,0,az))
    if hasLog:
        lb = rotY(LogBox,90,BC) if axis=='X' else tuple(LogBox)
        lb = off(lb,ax,0,az); combined = union(combined,lb)
    if not isMaster: combined = off(combined,-ax,0,-az)
    return combined
def rnd(b): return tuple(round(v,3) for v in b)
for axis in ('Z','X'):
    ax = 1 if axis=='X' else 0; az = 1 if axis=='Z' else 0
    for hasLog in (False,True):
        m = pair(axis,True,hasLog); s = pair(axis,False,hasLog)
        mw = m; sw = off(s, ax, 0, az)
        print("axis=%s log=%s" % (axis,hasLog))
        print("  master-rel =", rnd(m))
        print("  slave-rel  =", rnd(s))
        print("  master-wld =", rnd(mw))
        print("  slave-wld  =", rnd(sw), "same:", rnd(mw)==rnd(sw))
