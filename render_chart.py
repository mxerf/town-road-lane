#!/usr/bin/env python3
"""Render history.csv into chart-light.svg / chart-dark.svg for the README.

Stdlib only — runs on a bare GitHub Actions runner. The two variants share the
series color (#d97706, validated for contrast on both GitHub surfaces); only
ink and grid colors differ. The README embeds them via <picture> with a
prefers-color-scheme source.
"""
import csv
from datetime import datetime, timezone

W, H = 720, 260
ML, MR, MT, MB = 46, 78, 16, 30  # right margin fits the last-value label
LINE = "#d97706"
FONT = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif"
THEMES = {
    "light": {"ink": "#1f2328", "muted": "#57606a", "grid": "#d0d7de"},
    "dark":  {"ink": "#e6edf3", "muted": "#8b949e", "grid": "#30363d"},
}


def parse_rows(path="history.csv"):
    rows = []
    with open(path, newline="") as f:
        for row in csv.DictReader(f):
            subs = row["subscribers"].strip()
            if not subs:
                continue
            ts = datetime.fromisoformat(row["timestamp"].replace("Z", "+00:00"))
            rows.append((ts, int(subs)))
    rows.sort(key=lambda r: r[0])
    return rows


def y_step(vmax):
    for step in (1000, 2000, 5000, 10000, 20000, 50000, 100000):
        if vmax <= step * 4:
            return step
    return 200000


def fmt_k(v):
    return "0" if v == 0 else (f"{v // 1000}k" if v % 1000 == 0 else str(v))


def build(theme, rows):
    ink, muted, grid = (THEMES[theme][k] for k in ("ink", "muted", "grid"))
    t0, t1 = rows[0][0], rows[-1][0]
    span = max((t1 - t0).total_seconds(), 1.0)
    vmax = max(v for _, v in rows)
    step = y_step(vmax)
    top = ((vmax * 21 // 20) // step + 1) * step  # ceiling above max*1.05

    def x(ts):
        return ML + (W - ML - MR) * (ts - t0).total_seconds() / span

    def y(v):
        return MT + (H - MT - MB) * (1 - v / top)

    parts = [
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" '
        f'width="{W}" height="{H}" role="img" aria-label="Subscribers over time">'
    ]
    # horizontal grid + y labels; baseline slightly stronger than the rest
    v = 0
    while v <= top:
        yy = y(v)
        op = "0.9" if v == 0 else "0.5"
        parts.append(
            f'<line x1="{ML}" y1="{yy:.1f}" x2="{W - MR}" y2="{yy:.1f}" '
            f'stroke="{grid}" stroke-width="1" opacity="{op}"/>'
        )
        parts.append(
            f'<text x="{ML - 8}" y="{yy + 3.5:.1f}" text-anchor="end" '
            f'font-family="{FONT}" font-size="11" fill="{muted}">{fmt_k(v)}</text>'
        )
        v += step
    # x ticks: 5 evenly spaced dates, DD.MM (locale-neutral)
    for i in range(5):
        ts = t0 + (t1 - t0) * i / 4
        parts.append(
            f'<text x="{x(ts):.1f}" y="{H - 8}" text-anchor="middle" '
            f'font-family="{FONT}" font-size="11" fill="{muted}">{ts.day:02d}.{ts.month:02d}</text>'
        )
    # the series
    pts = " ".join(f"{x(ts):.1f},{y(v):.1f}" for ts, v in rows)
    parts.append(
        f'<polyline points="{pts}" fill="none" stroke="{LINE}" '
        f'stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>'
    )
    # last value: dot + direct label in text ink (never the series color)
    lts, lv = rows[-1]
    parts.append(f'<circle cx="{x(lts):.1f}" cy="{y(lv):.1f}" r="3.5" fill="{LINE}"/>')
    parts.append(
        f'<text x="{x(lts) + 9:.1f}" y="{y(lv) + 4:.1f}" font-family="{FONT}" '
        f'font-size="12" font-weight="600" fill="{ink}">{lv:,}</text>'.replace(",", " ")
    )
    parts.append("</svg>")
    return "\n".join(parts)


def main():
    rows = parse_rows()
    if len(rows) < 2:
        raise SystemExit("need at least 2 datapoints")
    for theme in THEMES:
        out = f"chart-{theme}.svg"
        with open(out, "w") as f:
            f.write(build(theme, rows))
        print(f"wrote {out} ({len(rows)} points)")


if __name__ == "__main__":
    main()
