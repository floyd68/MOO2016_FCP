import sys, xml.etree.ElementTree as ET
r = ET.parse(sys.argv[1]).getroot()
abc = next(e for e in r.iter() if e.get('type') == 'DoABC2Tag').find('abc')
c = abc.find('constants')
strs = [i.text if i.text is not None else i.get('value') for i in c.find('constant_string')]
# print a sample string item attrs
s0 = list(c.find('constant_string'))[:3]; print([ (x.attrib, x.text) for x in s0])
mn = list(c.find('constant_multiname')); print(mn[1].attrib)
