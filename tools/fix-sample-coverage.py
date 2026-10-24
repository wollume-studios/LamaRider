"""Which fix samples (samples/MetalamaFixSamples) report their own diagnostic, from a worker model.json.

Usage: python tools/fix-sample-coverage.py <model.json>
"""
import json
import os
import re
import sys

model = json.load(open(sys.argv[1], encoding="utf-8"))
diagnostics = model["diagnostics"]
root = os.path.join(os.path.dirname(__file__), "..", "samples", "MetalamaFixSamples")
samples = sorted(x for x in os.listdir(root) if re.fullmatch(r"LAMA[0-9]{4}", x))


def in_sample(d, sample):
    path = (d.get("file") or "").replace(os.sep, "/").replace("\\", "/")
    return d.get("project") == sample or "/" + sample + "/" in path


own, other, none = [], {}, []
for sample in samples:
    ids = sorted({d["id"] for d in diagnostics if in_sample(d, sample)})
    if sample in ids:
        own.append(sample)
    elif ids:
        other[sample] = ids
    else:
        none.append(sample)
located = [d for d in diagnostics if d["id"] in own and in_sample(d, d["id"])]
print(f"own: {len(own)}  other only: {len(other)}  none: {len(none)}  (spans: {sum(1 for d in located if d.get('start') is not None)}/{len(located)})")
print("other:", other)
print("none:", " ".join(none))
