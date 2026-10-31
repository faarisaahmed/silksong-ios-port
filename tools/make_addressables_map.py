"""Works out the game's Addressables layout so the iOS build can recreate it.

Usage: make_addressables_map.py <catalog.jsonl> <bundle_contents.tsv> <Unity project dir> [map output]

catalog.jsonl comes from tools/catdump (the PC game's catalog.bin) and bundle_contents.tsv from
tools/bundledump (what's inside each PC bundle). This does three things:

1. GUID fixes: the catalog finds some assets by their original GUID, and AssetRipper gives a few of
   them new ones. Those GUIDs are put back in the .meta files and everything that references them.
2. Writes Assets/Editor/IOSPort/addressables_map.tsv, which AddressablesSetup.cs reads. Each line:
   asset path, address, labels, group, and 1 if the group is looked up through the catalog.
3. Adds the sprite atlas sheets AssetRipper unpacked (sactx-*.png) to the group of their atlas.
"""
import collections
import json
import os
import re
import subprocess
import sys

CATALOG, BUNDLES, PROJECT = sys.argv[1:4]
ASSETS = os.path.join(PROJECT, "Assets")
MAP = sys.argv[4] if len(sys.argv) > 4 else os.path.join(ASSETS, "Editor", "IOSPort", "addressables_map.tsv")


def ishex(k):
    return len(k) == 32 and all(c in "0123456789abcdef" for c in k)


def group(bundle):
    return re.sub(r"(_[0-9a-f]{32})?\.bundle$", "", bundle).replace("/", "_")


def resolve(p):
    # AssetRipper sometimes exports with a different extension than the original asset had.
    f = os.path.join(PROJECT, p)
    if os.path.exists(f):
        return p
    d, b = os.path.split(f)
    stem = os.path.splitext(b)[0]
    if os.path.isdir(d):
        for x in os.listdir(d):
            if os.path.splitext(x)[0] == stem and not x.endswith(".meta"):
                return os.path.relpath(os.path.join(d, x), PROJECT)
    return None


cat = [json.loads(l) for l in open(CATALOG, encoding="utf-8")]
assets = [r for r in cat if r["provider"].endswith("BundledAssetProvider")]
keycount = collections.Counter(k for k, _ in {(r["key"], r["id"]) for r in assets})

# 1. GUID fixes
remap = []
seen = set()
for r in assets:
    if not ishex(r["key"]) or r["id"] in seen:
        continue
    seen.add(r["id"])
    rp = resolve(r["id"])
    if not rp or not os.path.exists(os.path.join(PROJECT, rp + ".meta")):
        continue
    old = [l.split()[1] for l in open(os.path.join(PROJECT, rp + ".meta"), encoding="utf-8") if l.startswith("guid:")][0]
    if old != r["key"]:
        remap.append((old, r["key"]))
if remap:
    args = ["grep", "-rlF"] + [x for old, _ in remap for x in ("-e", old)] + [ASSETS]
    files = subprocess.run(args, capture_output=True, text=True).stdout.splitlines()
    for f in files:
        t = open(f, encoding="utf-8", errors="surrogateescape").read()
        o = t
        for old, new in remap:
            t = t.replace(old, new)
        if t != o:
            open(f, "w", encoding="utf-8", errors="surrogateescape").write(t)
print(f"  GUID fixes: {len(remap)}")

# 2. The map
info = collections.defaultdict(lambda: {"addr": None, "labels": set(), "group": None})
for r in assets:
    e = info[r["id"]]
    k = r["key"]
    e["group"] = group(re.sub(r".*[\\/]StandaloneWindows64[\\/]", "", r["bundle"] or "misc").replace("\\", "/"))
    if ishex(k):
        continue
    if keycount[k] > 1:
        e["labels"].add(k)
    else:
        e["addr"] = k
catgroups = {e["group"] for e in info.values()}

rows = [l.rstrip("\n").split("\t") for l in open(BUNDLES, encoding="utf-8")]
first = {}
for b, p in rows:
    first.setdefault(p, group(b))
os.makedirs(os.path.dirname(MAP), exist_ok=True)
n = missing = 0
written = set()
with open(MAP, "w", encoding="utf-8") as out:
    for p, g in first.items():
        e = info.get(p)
        if e:
            g = e["group"]  # explicitly loaded assets keep their catalog bundle
        rp = resolve(p)
        if not rp:
            missing += 1
            continue
        labels = ",".join(sorted(e["labels"])) if e else ""
        out.write("\t".join([rp, (e or {}).get("addr") or "", labels, g, "1" if g in catgroups else "0"]) + "\n")
        written.add(rp)
        n += 1

    # 3. Sprite atlas sheets
    atlas = {os.path.splitext(os.path.basename(p))[0].lower(): group(b) for b, p in rows if p.endswith(".spriteatlas")}
    sheets = unmatched = 0
    for root, _, fs in os.walk(ASSETS):
        for f in fs:
            if not (f.startswith("sactx-") and f.endswith(".png")):
                continue
            m = re.match(r"sactx-\d+-\d+x\d+-[^-]+-(.+)-[0-9a-f]{8}\.png$", f)
            g = atlas.get(m.group(1).lower()) if m else None
            rp = os.path.relpath(os.path.join(root, f), PROJECT)
            if g and rp not in written:
                out.write(f"{rp}\t\t\t{g}\t0\n")
                sheets += 1
            elif not g:
                unmatched += 1
print(f"  map: {n} assets, {sheets} atlas sheets ({missing} not found, {unmatched} sheets unmatched)")
