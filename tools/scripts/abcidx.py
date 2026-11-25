import sys, xml.etree.ElementTree as ET
t = ET.parse(sys.argv[1]); r = t.getroot()
abc = None
for e in r.iter():
    if e.get('type') == 'DoABC2Tag': abc = e; break
# print child tag names of abc and nested
def show(e, d=0, maxd=3):
    if d > maxd: return
    kids = list(e)
    tags = {}
    for k in kids: tags.setdefault(k.tag, 0); tags[k.tag] += 1
    print('  '*d + e.tag, e.get('type'), {k:v for k,v in tags.items()})
    seen=set()
    for k in kids:
        if k.tag in seen: continue
        seen.add(k.tag); show(k, d+1, maxd)
show(abc, 0, 3)
