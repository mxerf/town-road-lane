# Subscriber stats — Town Road Lane (PDX Mods id 150863)

`history.csv` — time series of the mod's subscriber/like counts from the public
Paradox Mods API (`https://api.paradox-interactive.com/mods?modId=150863&os=windows`,
fields `modDetail.subscriptions` / `modDetail.ratingsTotal`).

Columns:

- `timestamp` — UTC, ISO 8601
- `subscribers`, `likes` — counts at that moment (empty = unknown)
- `source` — `api` = automated snapshot (GitHub Actions, every 6 h, see
  `.github/workflows/stats.yml` on `main`); `seed` = reconstructed by hand from
  screenshots taken before automation existed (approximate where not exact)

This branch is data-only and has no common history with `main`.
