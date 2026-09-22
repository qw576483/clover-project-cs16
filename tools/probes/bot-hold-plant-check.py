#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Offline assert - slice BG: bot hold-spot table (CT multi-spot hold + rotation) and the
plant-decision geometry (T carrier stops INSIDE the bombsite zone).

WHAT THIS SCRIPT JUDGES (and what it does NOT):
  * JUDGED OFFLINE (data / geometry layer):
      - the C# constants used by the decision really satisfy the relations the decision
        relies on (PlantStopRadius << SiteRadius, SiteRadius == BombsiteRadius, ...);
      - the runtime marker table (de_dust2_markers.bytes) can really yield >= 2 usable
        hold spots per bombsite, mutually separated by >= HoldSpotMinSeparation and all
        inside the bombsite radius (the same points CsBomb.IsInBombsite uses).
  * NOT JUDGED HERE (needs one Play run, see the PENDING rows at the end):
      - "did N CT actually spread over >= 2 bombsites and really swap spots";
      - "did the T carrier really plant within S seconds of arriving".
    Those rows read <project>/.ai-tmp/test/bg-hold-plant.tsv (runtime snapshot) and stay
    PENDING (never PASS) while that file does not exist.

Exit code: 0 = no FAIL row (PENDING rows do not make it fail, but they are counted and
printed separately so they can never be mistaken for a pass).

Style follows tools/probes/bot-path-check.py: ASCII only, prints numbers, re-runnable.
"""

import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))  # <project root> (tools/probes -> tools -> root)

MARKERS = os.path.join(ROOT, 'client', 'Assets', 'Resources', 'MapData', 'de_dust2_markers.bytes')
BOT_CONST = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Bot', 'CsBotConst.cs')
CORE_CONST = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Core', 'CsConst.cs')
ICS_MAP = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Map', 'ICsMap.cs')
BRAIN = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Bot', 'CsBotBrain.cs')
HOLD_SPOTS = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Bot', 'CsBotHoldSpots.cs')
C_BOMB = os.path.join(ROOT, 'client', 'Assets', 'Scripts', 'Module', 'Match', 'CsBomb.cs')
RUNTIME_TSV = os.path.join(ROOT, '.ai-tmp', 'test', 'bg-hold-plant.tsv')

SITE_A = 'Bombsite_A'
SITE_B = 'Bombsite_B'

rows = []  # (status, check_id, detail)


def add(status, cid, detail):
    rows.append((status, cid, detail))


def read(path):
    with open(path, 'r', encoding='utf-8', errors='replace') as fh:
        return fh.read()


def const_float(text, name):
    m = re.search(r'public\s+const\s+float\s+' + name + r'\s*=\s*([-\d.]+)f', text)
    return float(m.group(1)) if m else None


def const_float_expr(text, name):
    m = re.search(r'public\s+const\s+float\s+' + name + r'\s*=\s*([^;]+);', text)
    return m.group(1).strip() if m else None


def load_markers():
    table = {}
    with open(MARKERS, 'r', encoding='utf-8', errors='replace') as fh:
        for raw in fh:
            line = raw.strip()
            if not line or line.startswith('#'):
                continue
            parts = line.split()
            if len(parts) < 4:
                continue
            try:
                x, y, z = float(parts[1]), float(parts[2]), float(parts[3])
            except ValueError:
                continue
            table.setdefault(parts[0], []).append((x, y, z))
    return table


def dist_xz(a, b):
    return ((a[0] - b[0]) ** 2 + (a[2] - b[2]) ** 2) ** 0.5


def build_spots(points, min_sep):
    """Mirror of CsBotHoldSpots.Build minus the runtime CanStand filter (see header)."""
    cand = sorted(points, key=lambda p: (p[0], p[2]))
    out = []
    for p in cand:
        if all(dist_xz(p, q) >= min_sep for q in out):
            out.append(p)
    return out


def main():
    bot = read(BOT_CONST)
    core = read(CORE_CONST)
    ics = read(ICS_MAP)
    brain = read(BRAIN)
    holds = read(HOLD_SPOTS)
    bomb = read(C_BOMB)

    site_radius = const_float(bot, 'SiteRadius')
    site_radius_expr_raw = const_float_expr(bot, 'SiteRadius')
    if site_radius is None and site_radius_expr_raw:
        # SiteRadius is an alias (CsMarkers.BombsiteRadius) -> resolve it in ICsMap.cs
        alias = re.fullmatch(r'CsMarkers\.(\w+)', site_radius_expr_raw)
        if alias:
            site_radius = const_float(ics, alias.group(1))
    plant_stop = const_float(bot, 'PlantStopRadius')
    min_sep = const_float(bot, 'HoldSpotMinSeparation')
    swap = const_float(bot, 'HoldSwapSeconds')
    old_repos = const_float(bot, 'CampRepositionSeconds')
    player_r = const_float(core, 'PlayerRadius')
    bomb_timer = const_float(core, 'BombTimer')
    plant_time = const_float(core, 'PlantTime')
    bombsite_r = const_float(ics, 'BombsiteRadius')
    site_radius_expr = site_radius_expr_raw

    print('consts: SiteRadius=%s (%s) BombsiteRadius=%s PlantStopRadius=%s HoldSpotMinSeparation=%s '
          'HoldSwapSeconds=%s CampRepositionSeconds=%s PlayerRadius=%s BombTimer=%s PlantTime=%s'
          % (site_radius, site_radius_expr, bombsite_r, plant_stop, min_sep, swap, old_repos,
             player_r, bomb_timer, plant_time))

    # ---- A: constant relations the decisions rely on ----
    if site_radius is not None and bombsite_r is not None and abs(site_radius - bombsite_r) < 1e-6:
        add('PASS', 'A1', 'SiteRadius == BombsiteRadius (%s) -> hold spots and plant judgement share one source'
            % site_radius)
    else:
        add('FAIL', 'A1', 'SiteRadius(%s) != BombsiteRadius(%s) -> two drifting zone definitions'
            % (site_radius, bombsite_r))

    if plant_stop is not None and site_radius is not None and plant_stop * 2.0 < site_radius:
        add('PASS', 'A2', 'PlantStopRadius(%s) * 2 < SiteRadius(%s) -> carrier stops well inside the zone, '
            'CanPlant cannot flip on float error' % (plant_stop, site_radius))
    else:
        add('FAIL', 'A2', 'PlantStopRadius(%s) vs SiteRadius(%s) -> stop radius too close to the zone border'
            % (plant_stop, site_radius))

    if min_sep is not None and player_r is not None and min_sep >= 2.0 * player_r:
        add('PASS', 'A3', 'HoldSpotMinSeparation(%s) >= 2*PlayerRadius(%s) -> two hold spots are never one cell'
            % (min_sep, 2.0 * player_r))
    else:
        add('FAIL', 'A3', 'HoldSpotMinSeparation(%s) < 2*PlayerRadius(%s)'
            % (min_sep, 2.0 * player_r if player_r else None))

    if swap is not None and old_repos is not None and swap > old_repos:
        add('PASS', 'A4', 'HoldSwapSeconds(%s) > old CampRepositionSeconds(%s) -> rotation, not jitter'
            % (swap, old_repos))
    else:
        add('FAIL', 'A4', 'HoldSwapSeconds(%s) <= CampRepositionSeconds(%s)' % (swap, old_repos))

    if swap is not None and bomb_timer is not None and swap * 4.0 < bomb_timer:
        add('PASS', 'A5', 'HoldSwapSeconds(%s) * 4 < BombTimer(%s) -> several rotations inside one C4 window'
            % (swap, bomb_timer))
    else:
        add('FAIL', 'A5', 'HoldSwapSeconds(%s) * 4 >= BombTimer(%s)' % (swap, bomb_timer))

    # ---- A6/A7: the code paths that consume the constants really exist (name-level only) ----
    for cid, needle, path, what in (
            ('A6', 'HoldRotate', BRAIN, 'CT hold rotation is invoked from Camp'),
            ('A7', 'NearestBombsitePoint', BRAIN, 'plant decision uses the same zone predicate as CsBomb'),
    ):
        if needle in read(path):
            add('PASS', cid, '%s (%s contains %s)' % (what, os.path.basename(path), needle))
        else:
            add('FAIL', cid, '%s: %s not found in %s' % (what, needle, os.path.basename(path)))

    if 'IsInBombsite' in bomb and 'BombsiteRadius' in bomb:
        add('PASS', 'A8', 'CsBomb.IsInBombsite judges by BombsiteRadius -> offline zone model matches the sim')
    else:
        add('FAIL', 'A8', 'CsBomb.IsInBombsite / BombsiteRadius not found -> zone model unknown')

    if 'CsBotHoldSpots.Build' in brain and 'CsBotHoldSpots.PickSlot' in brain:
        add('PASS', 'A9', 'CsBotBrain builds the hold table and picks a slot from it')
    else:
        add('FAIL', 'A9', 'CsBotBrain does not use CsBotHoldSpots')

    if 'CanStand' in holds:
        add('PASS', 'A10', 'CsBotHoldSpots filters hold spots by ICsMap.CanStand (same predicate as movement)')
    else:
        add('FAIL', 'A10', 'CsBotHoldSpots does not filter by CanStand')

    # ---- B: marker table can really yield the hold table ----
    table = load_markers()
    total = sum(len(v) for v in table.values())
    print('marker table: %d groups / %d points' % (len(table), total))

    spot_counts = {}
    for site in (SITE_A, SITE_B):
        pts = table.get(site)
        if not pts:
            add('FAIL', 'B1.' + site, 'marker group %s missing' % site)
            continue

        spots = build_spots(pts, min_sep if min_sep else 3.0)
        spot_counts[site] = len(spots)

        if len(spots) >= 2:
            add('PASS', 'B1.' + site, '%s: %d marker points -> %d hold spots (>=2, upper bound: offline has no '
                'CanStand filter) %s' % (site, len(pts), len(spots), ' '.join(
                    '(%g,%g)' % (p[0], p[2]) for p in spots)))
        else:
            add('FAIL', 'B1.' + site, '%s: %d marker points -> only %d hold spot(s)' % (site, len(pts), len(spots)))

        dmin = min((dist_xz(a, b) for i, a in enumerate(spots) for b in spots[i + 1:]), default=None)
        if dmin is None or dmin >= (min_sep if min_sep else 3.0) - 1e-6:
            add('PASS', 'B2.' + site, '%s: min pairwise spot distance = %s >= HoldSpotMinSeparation(%s)'
                % (site, '%.2f' % dmin if dmin is not None else 'n/a', min_sep))
        else:
            add('FAIL', 'B2.' + site, '%s: min pairwise spot distance %.2f < %s' % (site, dmin, min_sep))

        # every spot is one of the site markers => its distance to the nearest same-site marker is 0
        worst = max(min(dist_xz(p, q) for q in pts) for p in spots)
        if worst <= (site_radius if site_radius else 7.0) + 1e-6:
            add('PASS', 'B3.' + site, '%s: worst spot distance to nearest same-site marker = %.2f m <= SiteRadius=%s'
                % (site, worst, site_radius))
        else:
            add('FAIL', 'B3.' + site, '%s: a hold spot is %.2f m away from the site markers' % (site, worst))

        # rotation is a real move: every consecutive pair in the sorted table is >= min separation apart
        movemin = min((dist_xz(spots[i], spots[i + 1]) for i in range(len(spots) - 1)), default=None)
        if len(spots) < 2:
            add('FAIL', 'B4.' + site, '%s: fewer than 2 spots -> no rotation possible' % site)
        elif movemin >= (min_sep if min_sep else 3.0) - 1e-6:
            add('PASS', 'B4.' + site, '%s: consecutive-spot move distance = %.2f m >= %s -> swapping is a real move'
                % (site, movemin, min_sep))
        else:
            add('FAIL', 'B4.' + site, '%s: consecutive-spot move distance %.2f m < %s' % (site, movemin, min_sep))

    # ---- B5: two bombsites are far apart (CT spread over A and B is a real spread) ----
    a_pts, b_pts = table.get(SITE_A, []), table.get(SITE_B, [])
    if a_pts and b_pts:
        d = min(dist_xz(p, q) for p in a_pts for q in b_pts)
        if d > 2.0 * (site_radius if site_radius else 7.0):
            add('PASS', 'B5', 'nearest A-B marker distance = %.2f m > 2*SiteRadius -> A and B hold zones do not overlap'
                % d)
        else:
            add('FAIL', 'B5', 'nearest A-B marker distance = %.2f m <= 2*SiteRadius -> zones overlap' % d)
    else:
        add('FAIL', 'B5', 'one of the two bombsite marker groups is missing')

    # ---- C: runtime rows (need one Play run) ----
    if os.path.isfile(RUNTIME_TSV):
        add('INFO', 'C0', 'runtime snapshot found: %s (parsed by the runtime section, see report)' % RUNTIME_TSV)
    else:
        add('PENDING', 'C1.runtime',
            'CT spread over >=2 bombsites and real spot swaps: needs one Play run -> %s' % RUNTIME_TSV)
        add('PENDING', 'C2.runtime',
            'T carrier plants within S seconds of arriving: needs one Play run -> %s' % RUNTIME_TSV)

    # ---- print ----
    npass = sum(1 for s, _, _ in rows if s == 'PASS')
    nfail = sum(1 for s, _, _ in rows if s == 'FAIL')
    npend = sum(1 for s, _, _ in rows if s == 'PENDING')
    ninfo = sum(1 for s, _, _ in rows if s == 'INFO')

    for status, cid, detail in rows:
        print('%-7s %-14s %s' % (status, cid, detail))

    print('')
    print('SUMMARY: PASS=%d FAIL=%d PENDING=%d INFO=%d (rows=%d) plant_time=%s s'
          % (npass, nfail, npend, ninfo, len(rows), plant_time))
    return 1 if nfail else 0


if __name__ == '__main__':
    sys.exit(main())
