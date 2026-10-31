"""Lists the textures that standalone Sprite assets point at, so TextureOptimizer.cs doesn't halve them
(those sprites store pixel rects that would no longer line up).

Usage: sprite_textures.py <project Assets dir> <output .txt>
"""
import os
import re
import sys

ASSETS, OUT = sys.argv[1:3]
pat = re.compile(rb"texture: \{fileID: 2800000, guid: ([0-9a-f]{32})")
guids = set()
for d in ("Sprite", "Sprites"):
    for root, _, files in os.walk(os.path.join(ASSETS, d)):
        for f in files:
            if f.endswith(".asset"):
                with open(os.path.join(root, f), "rb") as fh:
                    guids.update(m.decode() for m in pat.findall(fh.read()))
open(OUT, "w").write("\n".join(sorted(guids)) + "\n")
print(f"  {len(guids)} sprite textures protected")
