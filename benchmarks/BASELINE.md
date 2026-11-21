# Benchmark Baseline (main @ a0ad624)

Captured 2026-11-21 via the `benchmarks.yml` GitHub Actions workflow
([run #44888459274](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/runs/44888459274),
`job=Short`, `filter=*`). This is the current reference for A/B comparisons.
Full raw results are in the `benchmark-results-44888459274` action artifact (90-day retention).

## Environment

- BenchmarkDotNet v0.15.8
- Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
- AMD EPYC 9V74 3.69GHz, 1 CPU, 4 logical and 2 physical cores
- .NET SDK 11.0.100 / .NET 10.0.15, X64 RyuJIT x86-64-v4
- Job: `ShortRun` (IterationCount=3, LaunchCount=1, WarmupCount=3)

Comparison rules (same as `benchmarks/README.md`):

- Only compare runs from this workflow (same ubuntu runner image).
- Never compare against local Windows runs.
- On shared runners, trust `Allocated` more than `Mean` (timings are noisy).

> Note: this run's runner image (Ubuntu 26.04 vs 24.04), CPU (9V74 @ 3.69GHz vs
> 9V45 @ 4.35GHz) and runtime (10.0.15 / SDK 11.0.100 vs 10.0.12 / SDK 10.0.401)
> differ from the previous baseline. Per the rules above, `Mean` deltas are not
> reliably attributable to code changes — use `Allocated` for A/B decisions.

All six benchmark classes were captured this time, including
`Bind.PredicateTranslationBenchmarks`, which was missing from the previous baseline.

---

## PredicateTranslationBenchmarks (newly captured)

| Method                     | Operations | Mean            | Error         | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|----------------------------|-----------:|----------------:|--------------:|-------------:|----------:|----------:|----------:|------------:|
| BindComparisonPredicates   | 10         |       16.85 us |      1.327 us |     0.073 us |    1.8311 |    0.0305 |         - |    30.22 KB |
| BindStartsWithPredicates   | 10         |       13.55 us |     14.933 us |     0.819 us |    1.3428 |         - |         - |    22.41 KB |
| BindClosureCompileFallback | 10         |    1,483.67 us |    169.818 us |     9.308 us |    3.9063 |         - |         - |    69.81 KB |
| BindComparisonPredicates   | 1000       |    1,841.07 us |    779.840 us |    42.746 us |  171.8750 |   93.7500 |         - |  3008.41 KB |
| BindStartsWithPredicates   | 1000       |    1,312.40 us |     83.552 us |     4.580 us |  132.8125 |   78.1250 |         - |  2234.86 KB |
| BindClosureCompileFallback | 1000       |  148,243.18 us | 21,554.386 us | 1,181.469 us |  333.3333 |         - |         - |  6959.79 KB |
| BindComparisonPredicates   | 10000      |   31,849.25 us |  8,946.815 us |   490.405 us | 2200.0000 | 1200.0000 |  400.0000 | 30179.07 KB |
| BindStartsWithPredicates   | 10000      |   20,176.84 us |  1,953.628 us |   107.085 us | 1343.7500 |  687.5000 |  343.7500 | 22444.14 KB |
| BindClosureCompileFallback | 10000      | 1,495,020.71 us | 90,609.370 us | 4,966.605 us | 4000.0000 | 3000.0000 | 1000.0000 | 69782.97 KB |

`BindClosureCompileFallback` (uncached expression-compile path) is ~50x slower than
`BindComparisonPredicates` at 10k operations.

---

## SetExpressionBenchmarks (P2 target)

| Method                                | Operations | Mean            | Error        | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|---------------------------------------|-----------:|----------------:|-------------:|-------------:|----------:|----------:|----------:|------------:|
| BindComputedColumnArithmetic          | 10         |       16.49 us |     14.35 us |     0.787 us |    1.7090 |         - |         - |    28.37 KB |
| BindComputedWithCapturedSubexpression | 10         |    1,134.62 us |     54.63 us |     2.995 us |    3.9063 |    1.9531 |         - |    74.97 KB |
| BindComputedColumnArithmetic          | 1000       |    2,163.10 us |  7,610.14 us |   417.137 us |  171.8750 |   78.1250 |         - |  2961.55 KB |
| BindComputedWithCapturedSubexpression | 1000       |  114,373.32 us |  8,005.71 us |   438.820 us |  333.3333 |         - |         - |  7474.16 KB |
| BindComputedColumnArithmetic          | 10000      |   26,090.78 us |  3,389.19 us |   185.773 us | 1875.0000 | 1093.7500 |  562.5000 | 28304.21 KB |
| BindComputedWithCapturedSubexpression | 10000      | 1,149,639.47 us | 28,446.29 us | 1,559.237 us | 4000.0000 | 2000.0000 | 1000.0000 | 74924.01 KB |

Captured-subexpression binding remains the slow path: ~44x slower than
arithmetic-only binding at 10k operations, with ~2.6x the allocations.

---

## SqliteExecuteBenchmarks (end-to-end)

| Method                         | Operations | Mean        | Error        | StdDev     | Gen0     | Gen1     | Allocated  |
|--------------------------------|-----------:|------------:|-------------:|-----------:|---------:|---------:|-----------:|
| ExecuteUpdateWhere             | 10         |    59.00 us |     6.986 us |   0.383 us |   2.9297 |        - |   48.67 KB |
| ExecuteEntityRowPrimaryKeyMatch | 10         |    52.78 us |     6.087 us |   0.334 us |   2.1362 |  0.0610 |   35.62 KB |
| ExecuteLargeInList             | 10         |    18.09 us |    25.309 us |   1.387 us |   0.6104 |        - |   10.25 KB |
| ExecuteUpdateWhere             | 100        |   567.21 us |    41.576 us |   2.279 us |  27.3438 |  3.9063 |  469.91 KB |
| ExecuteEntityRowPrimaryKeyMatch | 100        |   525.26 us |   124.303 us |   6.813 us |  19.5313 |  3.9063 |  337.31 KB |
| ExecuteLargeInList             | 100        |    54.03 us |     0.720 us |   0.039 us |   0.7324 |        - |   13.32 KB |
| ExecuteUpdateWhere             | 1000       | 5,994.73 us | 5,432.920 us | 297.797 us | 281.2500 | 125.0000 | 4680.09 KB |
| ExecuteEntityRowPrimaryKeyMatch | 1000       | 5,448.65 us |   256.600 us |  14.065 us | 203.1250 | 156.2500 | 3352.07 KB |
| ExecuteLargeInList             | 1000       |   392.13 us |    47.646 us |   2.612 us |   3.9063 |        - |   66.39 KB |

---

## EntityRowMatchBenchmarks (P3 target)

| Method                         | Rows  | Mean           | Error           | StdDev      | Gen0      | Gen1      | Gen2      | Allocated   |
|--------------------------------|------:|---------------:|----------------:|------------:|----------:|----------:|----------:|------------:|
| BindAndGeneratePrimaryKeyMatch | 10    |      7.653 us |      0.3748 us |   0.0205 us |    1.2894 |    0.0763 |         - |    21.09 KB |
| BindAndGenerateCustomMatch     | 10    |     10.474 us |      1.1232 us |   0.0616 us |    1.5106 |    0.0763 |         - |    24.69 KB |
| BindAndGeneratePrimaryKeyMatch | 1000  |  1,154.197 us |    947.2142 us |  51.9200 us |  216.7969 |  216.7969 |  216.7969 |  2014.63 KB |
| BindAndGenerateCustomMatch     | 1000  |  1,551.874 us |    171.8818 us |   9.4214 us |  248.0469 |  248.0469 |  248.0469 |  2243.52 KB |
| BindAndGeneratePrimaryKeyMatch | 10000 | 20,754.103 us |  4,425.4253 us | 242.5725 us | 2125.0000 | 2125.0000 | 1375.0000 | 20298.86 KB |
| BindAndGenerateCustomMatch     | 10000 | 25,420.949 us | 14,853.8832 us | 814.1915 us | 2281.2500 | 2281.2500 | 1406.2500 |    22589 KB |

Custom match is ~23% slower than PK match at 10k rows (per-row rewrite path), down
from ~2x in the previous baseline.

---

## LargeInListBenchmarks

| Method                 | ListSize | Mean         | Error      | StdDev     | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------|---------:|-------------:|-----------:|-----------:|---------:|---------:|---------:|-----------:|
| BindAndGenerateLargeIn | 500      |    25.77 us |  29.66 us |   1.626 us |   1.9531 |        - |        - |   32.28 KB |
| BindAndGenerateLargeIn | 5000     |   214.69 us |  17.13 us |   0.939 us |  20.5078 |  6.8359 |        - |  335.98 KB |
| BindAndGenerateLargeIn | 20000    | 1,260.79 us | 232.62 us |  12.751 us | 234.3750 | 234.3750 | 234.3750 | 1355.16 KB |

---

## SqlGenerationBenchmarks (P4 target)

| Method                          | Operations | Mean          | Error          | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|---------------------------------|-----------:|--------------:|---------------:|-------------:|----------:|----------:|----------:|------------:|
| BindAndGenerateUpdateStatements | 10         |     32.19 us |      3.904 us |     0.214 us |    3.1738 |         - |         - |    55.79 KB |
| BindAndGenerateUpdateStatements | 1000       |  3,622.65 us |  1,214.301 us |    66.560 us |  421.8750 |  375.0000 |  203.1250 |  5397.2 KB |
| BindAndGenerateUpdateStatements | 10000      | 58,026.81 us | 24,462.103 us | 1,340.850 us | 4000.0000 | 2000.0000 | 1333.3333 | 54910.86 KB |

---

## Comparison vs. previous baseline (pre-optimization, 2026-09-23)

Representative largest workload per benchmark. `Mean` deltas are indicative only
because of the runner/runtime change (see note above); `Allocated` is the reliable signal.

| Benchmark (largest case)              | Mean: old → new             | Δ Mean  | Allocated: old → new        | Δ Alloc |
|-------------------------------------- |---------------------------- |--------:|---------------------------- |--------:|
| SetExpression / Arithmetic (10k)     | 23,835.51 → 26,090.78 us   | +9%     | 28304.33 → 28304.21 KB     | ~0%     |
| SetExpression / Captured (10k)       | 380,676.09 → 1,149,639.47 us | +202% | 75385.34 → 74924.01 KB     | -0.6%   |
| SqliteExecute / UpdateWhere (1000)   | 4,481.87 → 5,994.73 us     | +34%    | 4727.01 → 4680.09 KB       | -1%     |
| SqliteExecute / PKMatch (1000)       | 4,118.46 → 5,448.65 us     | +32%    | 3398.95 → 3352.07 KB       | -1.4%   |
| SqliteExecute / LargeInList (1000)   | 245.29 → 392.13 us         | +60%    | 66.52 → 66.39 KB           | ~0%     |
| EntityRowMatch / PK (10k)            | 19,905.67 → 20,754.10 us   | +4%     | 20767.62 → 20298.86 KB     | -2.3%   |
| EntityRowMatch / Custom (10k)        | 39,735.38 → 25,420.95 us   | -36%    | 30906.83 → 22589 KB        | -27%    |
| LargeInList (20000)                  | 876.89 → 1,260.79 us       | +44%    | 1355.39 → 1355.16 KB       | ~0%     |
| SqlGeneration / UpdateStatements (10k) | 50,304.61 → 58,026.81 us | +15%    | 56158.76 → 54910.86 KB     | -2.2%   |
| PredicateTranslation                 | not captured previously    | —       | newly captured above       | —       |

Takeaways:

- The only clear allocation reduction is `EntityRowMatch / CustomMatch`
  (~27% less, ~36% lower Mean at 10k rows), consistent with
  "Hoist entity row match plan and add cross-provider tests" (#26).
- Allocations elsewhere are flat to ~2% lower; the broadly higher `Mean` values
  track the slower new runner CPU rather than a code-level regression.
- `BindComputedWithCapturedSubexpression` shows the largest `Mean` increase
  (3x at 10k) despite flat allocations — re-check on a pinned runner before
  treating that as a real regression.

<details>
<summary>Previous baseline tables (pre-optimization, 2026-09-23, run environment: Ubuntu 24.04.5 / EPYC 9V45 4.35GHz / .NET 10.0.12)</summary>

### SetExpressionBenchmarks

| Method                               | Operations | Mean          | Error         | StdDev       | Gen0      | Gen1      | Gen2     | Allocated   |
|--------------------------------------|----------- |--------------:|--------------:|-------------:|----------:|----------:|---------:|------------:|
| BindComputedColumnArithmetic         | 10         |      13.36 us |      25.58 us |     1.402 us |    1.7090 |         - |         - |    28.37 KB |
| BindComputedWithCapturedSubexpression | 10        |     365.83 us |      72.26 us |     3.961 us |    3.9063 |    2.9297 |         - |    75.44 KB |
| BindComputedColumnArithmetic         | 1000       |   1,278.09 us |     370.05 us |    20.283 us |  171.8750 |  117.1875 |         - |  2820.93 KB |
| BindComputedWithCapturedSubexpression | 1000      |  36,624.77 us |   3,658.79 us |   200.551 us |  428.5714 |  357.1429 |         - |  7538.3 KB  |
| BindComputedColumnArithmetic         | 10000      |  23,835.51 us |  11,186.79 us |   613.186 us | 1875.0000 | 1093.7500 | 562.5000 | 28304.33 KB |
| BindComputedWithCapturedSubexpression | 10000     | 380,676.09 us | 130,615.12 us | 7,159.455 us | 4000.0000 | 2000.0000 |         - | 75385.34 KB |

### SqliteExecuteBenchmarks

| Method                         | Operations | Mean        | Error        | StdDev    | Gen0     | Gen1     | Allocated  |
|--------------------------------|----------- |------------:|-------------:|----------:|---------:|---------:|-----------:|
| ExecuteUpdateWhere             | 10         |    52.88 us |    99.700 us |  5.465 us |   2.9297 |        - |    49.14 KB |
| ExecuteEntityRowPrimaryKeyMatch | 10         |    40.30 us |     5.063 us |  0.278 us |   2.1973 |   0.0610 |    36.09 KB |
| ExecuteLargeInList             | 10         |    11.62 us |    21.842 us |  1.197 us |   0.6104 |        - |     10.7 KB |
| ExecuteUpdateWhere             | 100        |   439.19 us |   322.347 us | 17.669 us |  27.3438 |   3.9063 |   474.6 KB |
| ExecuteEntityRowPrimaryKeyMatch | 100        |   384.76 us |    58.970 us |  3.232 us |  19.5313 |   3.9063 |     342 KB |
| ExecuteLargeInList             | 100        |    32.23 us |    23.128 us |  1.268 us |   0.7324 |        - |    13.59 KB |
| ExecuteUpdateWhere             | 1000       | 4,481.87 us |   341.509 us | 18.719 us | 281.2500 | 125.0000 | 4727.01 KB |
| ExecuteEntityRowPrimaryKeyMatch | 1000       | 4,118.46 us | 1,194.371 us | 65.468 us | 203.1250 | 125.0000 | 3398.95 KB |
| ExecuteLargeInList             | 1000       |   245.29 us |    55.610 us |  3.048 us |   3.9063 |   0.4883 |    66.52 KB |

### EntityRowMatchBenchmarks

| Method                        | Rows  | Mean          | Error          | StdDev      | Gen0      | Gen1      | Gen2      | Allocated   |
|-------------------------------|------ |--------------:|---------------:|------------:|----------:|----------:|----------:|------------:|
| BindAndGeneratePrimaryKeyMatch | 10   |      6.112 us |      0.8104 us |   0.0444 us |    1.3123 |    0.0763 |         - |    21.55 KB |
| BindAndGenerateCustomMatch     | 10   |     14.207 us |     14.4249 us |   0.7907 us |    1.9531 |         - |         - |    32.77 KB |
| BindAndGeneratePrimaryKeyMatch | 1000 |    998.578 us |     76.2241 us |   4.1781 us |  216.7969 |  216.7969 |  216.7969 |   2061.5 KB |
| BindAndGenerateCustomMatch     | 1000 |  2,411.032 us | 9,082.0728 us | 497.8191 us |  242.1875 |  242.1875 |  242.1875 |   3074.9 KB |
| BindAndGeneratePrimaryKeyMatch | 10000 | 19,905.665 us | 10,620.4845 us | 582.1446 us | 2156.2500 | 2125.0000 | 1375.0000 | 20767.62 KB |
| BindAndGenerateCustomMatch     | 10000 | 39,735.377 us | 15,638.6631 us | 857.2079 us | 2769.2308 | 2230.7692 | 1384.6154 | 30906.83 KB |

### LargeInListBenchmarks

| Method                 | ListSize | Mean      | Error     | StdDev    | Median    | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------|--------- |----------:|----------:|----------:|----------:|---------:|---------:|---------:|-----------:|
| BindAndGenerateLargeIn | 500      |  25.31 us | 266.75 us | 14.621 us |  17.06 us |   1.9531 |        - |        - |   32.45 KB |
| BindAndGenerateLargeIn | 5000     | 142.02 us | 108.69 us |  5.958 us | 144.87 us |  20.5078 |   6.8359 |        - |  336.15 KB |
| BindAndGenerateLargeIn | 20000    | 876.89 us | 294.07 us | 16.119 us | 872.65 us | 234.3750 | 234.3750 | 234.3750 | 1355.39 KB |

### SqlGenerationBenchmarks

| Method                        | Operations | Mean         | Error        | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|-------------------------------|----------- |-------------:|-------------:|-------------:|----------:|----------:|----------:|------------:|
| BindAndGenerateUpdateStatements | 10        |     26.37 us |     58.79 us |     3.222 us |    3.4180 |         - |         - |    56.56 KB |
| BindAndGenerateUpdateStatements | 1000      |  2,794.51 us |  2,549.13 us |   139.727 us |  421.8750 |  375.0000 |  203.1250 |  5598.87 KB |
| BindAndGenerateUpdateStatements | 10000     | 50,304.61 us | 74,424.62 us | 4,079.465 us | 4000.0000 | 2500.0000 | 1500.0000 | 56158.76 KB |

</details>

---

## CapturedValueBenchmarks (direct evaluation A/B, 2026-11-21)

Six caller-realistic shapes, measured locally on Windows with `job=Short`,
.NET SDK 11.0.100, before (`6f729af`) and after the direct-evaluation change.
Both runs used the identical benchmark file. Local timings are directional;
`Allocated` is deterministic.

| Method (10,000 ops)        | Mean before | Mean after | Speedup | Allocated before | Allocated after | Δ Alloc |
|----------------------------|------------:|-----------:|--------:|-----------------:|----------------:|--------:|
| BindCapturedConstant       |    28,263 us |  23,184 us |   1.2x  |     21,273.89 KB |     21,273.74 KB | ~0     |
| BindCapturedArithmetic     | 1,076,894 us |  35,678 us |  30.2x  |     74,638.41 KB |     31,820.71 KB | -57%   |
| BindCapturedStringConcat   |    28,436 us |  24,555 us |   1.2x  |     22,748.78 KB |     22,748.57 KB | ~0     |
| BindInterpolatedString     |    26,190 us |  23,238 us |   1.1x  |     21,117.59 KB |     21,117.50 KB | ~0     |
| BindCapturedMethodArgument |   874,434 us |  37,347 us |  23.4x  |     75,089.98 KB |     33,999.05 KB | -55%   |
| BindCapturedDecimalFactor  | 1,207,417 us |  34,883 us |  34.6x  |     74,005.95 KB |     30,804.57 KB | -58%   |

Arithmetic, method arguments and decimal factors used to compile a lambda per
operation; they now evaluate operands directly and call a delegate compiled once
per operator shape. Constants, string concat and interpolated strings were never
on the compile path and are unchanged within noise.
