"""Exact-match block replacer that keeps a file's line endings and BOM.

usage: python edit.py <target> <pairs-file>
The pairs file holds blocks separated by lines '@@@OLD', '@@@NEW', '@@@END'.
"""
import io
import sys

target, pairs_path = sys.argv[1], sys.argv[2]
raw = io.open(target, 'rb').read()
bom = raw.startswith(b'\xef\xbb\xbf')
s = raw.decode('utf-8-sig')
nl = '\r\n' if '\r\n' in s else '\n'

text = io.open(pairs_path, 'r', encoding='utf-8').read().replace('\r\n', '\n')
blocks = text.split('@@@OLD\n')[1:]
for b in blocks:
    old, rest = b.split('@@@NEW\n', 1)
    new = rest.split('@@@END', 1)[0]
    old = old.replace('\n', nl)
    new = new.replace('\n', nl)
    n = s.count(old)
    if n != 1:
        sys.exit('%s: %d matches for:\n%s' % (target, n, old[:200]))
    s = s.replace(old, new)

io.open(target, 'wb').write((b'\xef\xbb\xbf' if bom else b'') + s.encode('utf-8'))
print('%s: %d edits' % (target, len(blocks)))
