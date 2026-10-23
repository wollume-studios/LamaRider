"""Summarizes the fix self-test reports (fix-selftest-BatchNN.json, from FixesUiTest).

Usage: python tools/fix-selftest-summary.py <folder with the reports>
"""
import glob
import json
import os
import sys

reports = []
for path in sorted(glob.glob(os.path.join(sys.argv[1], "fix-selftest-Batch*.json"))):
    reports += json.load(open(path, encoding="utf-8"))

errors = [r for r in reports if r["Error"]]
own = [r for r in reports if r["Id"] in r["Reported"]]
ran = [r for r in reports if r["Fixes"]]
matching = [r for r in ran if any(f["Matches"] for f in r["Fixes"])]
print(f"samples: {len(reports)} | report their own id: {len(own)} | fixes ran: {len(ran)} | a fix matches the expected code: {len(matching)}")
print("fix exceptions:", sum(1 for r in ran for f in r["Fixes"] if f["Error"]))
print("sample errors:", [(r["Id"], r["Error"][:100]) for r in errors][:6])
print("own id reported, no fix offered:", " ".join(r["Id"] for r in own if not r["Fixes"] and not r["Error"]))
print("fixes ran, none matches:", " ".join(r["Id"] for r in ran if r not in matching))
print("fix exceptions by sample:", {r["Id"]: [f["Error"][:80] for f in r["Fixes"] if f["Error"]][:1] for r in ran if any(f["Error"] for f in r["Fixes"])})
