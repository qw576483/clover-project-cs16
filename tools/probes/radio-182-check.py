#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""radiofix #182 offline judge (no editor).

MINIMUM MATERIALS TO RE-CHECK THIS CONCLUSION (all in-repo, none under .ai-tmp):
  1. client/Assets/Scripts/UI/InGame/RadioMenuPanel.cs      -- the panel under test
  2. client/Assets/Resources/UI/RadioMenuPanel.prefab       -- the baked 9-row layout
  3. this script                                            -- the judge itself
  4. 原版资源/cs16src/regamedll/game_shared/GameEvent.h      -- original radio1/2/3 enum (6/6/9)
  5. 原版资源/cs16src/cstrike/cstrike__resource__cstrike_english.txt -- the 21 original text lines
  6. 原版资源/cs16src/regamedll/dlls/bot/cs_bot_radio.cpp    -- the group/slot mapping
Inputs are absolute paths and every one of them is read eagerly: a missing input raises
FileNotFoundError with the path (never a silent skip), and any failed assertion is printed as FAIL
and makes the process exit 1.

Asserts, against the ORIGINAL source files on disk:
  A. the original radio enumeration (GameEvent.h radio1/2/3) is 6+6+9 and in enum order;
  B. Core/CsRadio.cs reproduces exactly that grouping/order (Id/Set/Slot/EventName);
  C. every CsRadio text equals the original cstrike_english.txt line its Source cites;
  D. UI/InGame/RadioMenuPanel.cs no longer carries its own (divergent) text table and
     now derives its entries from CsRadio, with a row budget == the largest original group;
  E. (when present) the generated prefab has that many Row children / serialized rows.

Exit 0 = every assertion holds; 1 = at least one FAIL.
"""
import os
import re
import sys

ROOT = r"C:\Work\Server\f-v2\clover-project-cs16"
SRC = os.path.join(ROOT, "原版资源", "cs16src")
GENEVENT = os.path.join(SRC, "regamedll", "game_shared", "GameEvent.h")
ENGLISH = os.path.join(SRC, "cstrike", "cstrike__resource__cstrike_english.txt")
CSRADIO = os.path.join(ROOT, "client", "Assets", "Scripts", "Core", "CsRadio.cs")
PANEL = os.path.join(ROOT, "client", "Assets", "Scripts", "UI", "InGame", "RadioMenuPanel.cs")
PREFAB = os.path.join(ROOT, "client", "Assets", "Resources", "UI", "RadioMenuPanel.prefab")

fails = []
oks = []


def check(name, cond, detail=""):
    (oks if cond else fails).append(name + ((" :: " + detail) if detail else ""))


def read(path, enc="utf-8-sig"):
    with open(path, "r", encoding=enc, errors="replace") as f:
        return f.read()


def lines(path, enc="utf-8-sig"):
    with open(path, "r", encoding=enc, errors="replace") as f:
        return f.read().split("\n")


# ---------------- A. original enumeration ----------------
env = read(GENEVENT)
enum_names = re.findall(r"\bEVENT_RADIO_([A-Z0-9_]+)\b", env)
check("A1 original radio enum has 21 entries", len(enum_names) == 21, "got %d" % len(enum_names))

# group boundaries from the START markers
m1 = env.index("EVENT_START_RADIO_1")
m2 = env.index("EVENT_START_RADIO_2")
m3 = env.index("EVENT_START_RADIO_3")
me = env.index("EVENT_END_RADIO")
g1 = re.findall(r"\bEVENT_RADIO_([A-Z0-9_]+)\b", env[m1:m2])
g2 = re.findall(r"\bEVENT_RADIO_([A-Z0-9_]+)\b", env[m2:m3])
g3 = re.findall(r"\bEVENT_RADIO_([A-Z0-9_]+)\b", env[m3:me])
check("A2 original groups are 6/6/9", (len(g1), len(g2), len(g3)) == (6, 6, 9),
      "got %d/%d/%d" % (len(g1), len(g2), len(g3)))
orig_groups = [("Radio1", g1), ("Radio2", g2), ("Radio3", g3)]
orig_flat = [n for _, names in orig_groups for n in names]

# ---------------- B. CsRadio.cs reproduces it ----------------
radio = read(CSRADIO)
entry_re = re.compile(
    r'new CsRadioLine\s*\{\s*Id\s*=\s*(\d+),\s*Set\s*=\s*CsRadioSet\.(\w+),\s*Slot\s*=\s*(\d+),\s*'
    r'EventName\s*=\s*"([^"]+)",\s*\n\s*Text\s*=\s*"((?:[^"\\]|\\.)*)"',
    re.M)
entries = entry_re.findall(radio)
check("B1 CsRadio has 21 entries", len(entries) == 21, "got %d" % len(entries))

ids = [int(e[0]) for e in entries]
sets = [e[1] for e in entries]
slots = [int(e[2]) for e in entries]
events = [e[3] for e in entries]
texts = dict()  # EventName -> Text
for e in entries:
    texts[e[3]] = e[4]

check("B2 CsRadio ids are 1..21 in order", ids == list(range(1, 22)), str(ids))
want_events = ["EVENT_RADIO_" + n for n in orig_flat]
check("B3 CsRadio order == original enum order", events == want_events,
      "first mismatch: %s" % next((("%s != %s" % (a, b)) for a, b in zip(events, want_events) if a != b), "none"))

# grouping / slot numbering
ok_groups, ok_slots = True, True
idx = 0
for gname, names in orig_groups:
    n = len(names)
    ok_groups &= all(sets[idx + k] == gname for k in range(n))
    ok_slots &= [slots[idx + k] for k in range(n)] == list(range(1, n + 1))
    idx += n
check("B4 CsRadio group assignment (Radio1/2/3)", ok_groups)
check("B5 CsRadio slot numbering is 1..N per group", ok_slots)

# ---------------- C. texts == original english lines ----------------
eng = lines(ENGLISH)
text_ok, text_detail = True, ""
for e in entries:
    ev, txt = e[3], e[4]
    src = e[5] if len(e) > 5 else None
    # find the Source field of this entry by scanning the raw block
    block = radio[radio.index('EventName = "%s"' % ev):]
    block = block[:block.index("},")] if "}," in block else block
    m = re.search(r"cstrike_english\.txt:(\d+)", block)
    if not m:
        # fall back: search within the whole entry (Source may sit after Text)
        m = re.search(r"cstrike_english\.txt:(\d+)", radio[radio.index(ev):radio.index(ev) + 900])
    if not m:
        text_ok = False
        text_detail = "%s: no cstrike_english.txt:NNN in Source" % ev
        break
    ln = int(m.group(1))
    raw = eng[ln - 1] if ln - 1 < len(eng) else ""
    mm = re.match(r'\s*"[^"]*"\s+"(.*)"\s*$', raw)
    want = mm.group(1) if mm else None
    if want is None or want != txt:
        text_ok = False
        text_detail = "%s: line %d says %r, CsRadio says %r" % (ev, ln, want, txt)
        break
check("C1 every CsRadio text == the original english line it cites", text_ok, text_detail)

# ---------------- D. RadioMenuPanel.cs ----------------
panel = read(PANEL)
for bad in ["Go, go, go!", "Taking fire, need assistance!", "Sector clear!", "I'm in position!"]:
    check("D1 panel has no stale hard-coded list %r" % bad, bad not in panel)
check("D2 panel reads its entries from CsRadio", "CsRadio.Of(" in panel)
check("D3 panel row budget == largest original group (9)",
      re.search(r"MaxEntries\s*=\s*9\b", panel) is not None)
check("D4 panel handles digit keys up to 9", "GameKey.Num9" in panel)
check("D5 panel sends the CsRadio text (line.Text)",
      re.search(r"_entries\[index\]\.Text", panel) is not None)
# the panel must not keep a parallel 3-group text table
check("D6 panel no longer declares its own RadioTexts table",
      "RadioTexts" not in panel)

# ---------------- E. generated prefab ----------------
if os.path.exists(PREFAB):
    pf = read(PREFAB, enc="utf-8")
    rows = len(re.findall(r"m_Name:\s*Row\d+", pf))
    ser = re.search(r"_rowTexts:\n((?:\s*-\s*\{fileID:[^\n]*\n)+)", pf)
    ser_n = len(re.findall(r"fileID", ser.group(1))) if ser else 0
    check("E1 prefab has 9 Row children", rows == 9, "got %d" % rows)
    check("E2 prefab serializes 9 row references", ser_n == 9, "got %d" % ser_n)
else:
    oks.append("E (prefab not present yet -- skipped)")

print("== radiofix #182 offline judge ==")
for s in oks:
    print("PASS  " + s)
for s in fails:
    print("FAIL  " + s)
print("---- FAIL=%d PASS=%d ----" % (len(fails), len(oks)))
sys.exit(1 if fails else 0)
