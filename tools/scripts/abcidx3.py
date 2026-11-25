import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
abc = next(e for e in r.iter() if e.get('type') == 'DoABC2Tag').find('abc')
c = abc.find('constants')
strs = [i.text if i.text is not None else '' for i in c.find('constant_string')]
mns = list(c.find('constant_multiname'))
def mname(i): return strs[int(mns[i].get('name_index'))]
bodies = list(abc.find('bodies'))
body_of = {int(b.get('method_info')): k for k, b in enumerate(bodies)}
for ii in abc.find('instance_info'):
    if mname(int(ii.get('name_index'))) != sys.argv[2]: continue
    print('iinit method', ii.get('iinit_index'), 'body', body_of.get(int(ii.get('iinit_index'))))
    for t in ii.find("instance_traits").iter("item"):
        mi = t.get('method_info')
        if mi is not None: print(mname(int(t.get('name_index'))), 'method', mi, 'body', body_of.get(int(mi)))
