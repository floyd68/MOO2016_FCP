import sys, zlib, re
p=sys.argv[1]; d=open(p,'rb').read()
sig=d[:3]
if sig==b'CWS': body=zlib.decompress(d[8:])
elif sig==b'FWS': body=d[8:]
else:
    # Scaleform GFX/CFX
    body = zlib.decompress(d[8:]) if sig==b'CFX' else d[8:]
print(sig, len(body), file=sys.stderr)
pat=re.compile(sys.argv[2].encode())
for m in sorted(set(x.group(0) for x in re.finditer(rb'[\x20-\x7e]{4,}', body))):
    if pat.search(m): print(m.decode())
