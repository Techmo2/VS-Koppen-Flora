# Köppen Flora

Places Vintage Story's trees, shrubs, wild fruit trees and plants by the real-world climates they
grow in, using the Köppen class and seasonal climate [Terrain Diffusion](../VSDiffusionTerrain)
generates. Server side only. On a world Terrain Diffusion is not generating, vanilla's rules apply.

## How it decides

Each plant has a profile in `assets/koppenflora/config/flora/*.json`. Its suitability at a column,
0 to 1, is the Köppen fit times every range:

| Field | Meaning |
| ----- | ------- |
| `treegens` / `patches` / `fruitTrees` | What the profile governs: tree or shrub generator names, block patch code wildcards, wild fruit tree types |
| `koppen` | Weight per Köppen class, 0–1; unlisted classes are 0. Blended across class boundaries by Terrain Diffusion's soft memberships |
| `temperatureC` | Annual mean, °C |
| `precipitationMm` | Annual rain, mm |
| `coldestMonthC`, `warmestMonthC` | Monthly mean extremes, °C: frost tolerance, winter chill, summer heat |
| `winterNightC` | Coldest month mean minus half the diurnal range: frost on winter nights |
| `driestMonthMm` | Rain in the driest month: dry-season tolerance |
| `diurnalRangeC` | Day-night swing, °C |
| `mode` | `replace` (default): decides instead of vanilla's climate windows. `multiply`: thins what vanilla allows |
| `weight` | Multiplies vanilla's weight in the tree mix |

A range is `[min, lowOptimum, highOptimum, max]`: nothing at `min`, a smooth rise to full strength
at `lowOptimum`, full to `highOptimum`, gone by `max`. `null` leaves a side open.

- **Trees and shrubs:** suitability scales each species' weight in vanilla's pick; species under 0.05
  are left out, and the best suitability present thins the attempt, so forests fade at the edge of
  their climate.
- **Plants:** a deterministic roll against suitability.
- Vanilla's temperature, rain, fertility and height windows are dropped for profiled plants (fertility
  is derived from rain and temperature, height stands in for altitude cold, and the profile already
  covers both). Placement, forest and shrub density rules stay.

Anything without a profile keeps vanilla's rules: mushrooms, water plants, coral and non-plants.
Other mods can add or replace profiles by shipping `config/flora/*.json` in their own assets (a later
profile with the same `id` wins). Some vanilla trees stand in for missing relatives (Mongolian oak,
spruce, Mexican cypress); each is noted in its profile.

## Building

`./build.sh` builds against `VSTerrainDiffusion.dll`
(set `TERRAIN_DIFFUSION` to use another) and packs `dist/koppenflora_<version>.zip`.
