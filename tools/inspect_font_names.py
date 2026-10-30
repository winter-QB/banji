import struct, sys
def names(path):
    with open(path,'rb') as f: data=f.read()
    numTables = struct.unpack(">H", data[4:6])[0]
    tables={}
    for i in range(numTables):
        off=12+16*i
        tag=data[off:off+4].decode('latin1')
        toff,tlen=struct.unpack(">II", data[off+8:off+16])
        tables[tag]=(toff,tlen)
    if 'name' not in tables: return {}
    off,ln=tables['name']
    fmt,count,strOff=struct.unpack(">HHH", data[off:off+6])
    out={}
    for i in range(count):
        rec=off+6+12*i
        pid,eid,lid,nid,length,o=struct.unpack(">HHHHHH", data[rec:rec+12])
        s=data[off+strOff+o:off+strOff+o+length]
        if pid==3 and lid==0x409:
            out[nid]=s.decode('utf-16-be',errors='replace')
    return out
for w in ["Regular","Bold","Thin","Medium","Light","Black"]:
    p=rf"assets\fonts\HarmonyOS_Sans_SC_{w}.ttf"
    n=names(p)
    print(w, "| ID1:",repr(n.get(1)), "| ID2:",repr(n.get(2)), "| ID4:",repr(n.get(4)), "| ID16:",repr(n.get(16)), "| ID17:",repr(n.get(17)), "| ID6:",repr(n.get(6)))
