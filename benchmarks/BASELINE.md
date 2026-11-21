# Benchmark Baseline (pre-optimization)

Captured **2026-09-23** via the `benchmarks.yml` GitHub Actions workflow (`job=Short`)
before P2 (expression compilation caching) landed. Compare future runs against this file.

## Environment

- BenchmarkDotNet v0.15.8
- Linux Ubuntu 24.04.5 LTS (Noble Numbat)
- AMD EPYC 9V45 4.35GHz, 1 CPU, 4 logical and 2 physical cores
- .NET SDK 10.0.401 / .NET 10.0.12, X64 RyuJIT x86-64-v4
- Job: `ShortRun` — IterationCount=3, LaunchCount=1, WarmupCount=3

**Comparison rules** (same as `benchmarks/README.md`):

- Only compare runs from this workflow (same ubuntu runner image).
- Never compare against local Windows runs.
- On shared runners, trust `Allocated` more than `Mean` (timings are noisy).

**Not captured in this run:** `Bind.PredicateTranslationBenchmarks` (filter output missing — re-run with `--filter '*PredicateTranslation*'` and append below).

---

## SetExpressionBenchmarks (P2 target)

| Method                               | Operations | Mean          | Error         | StdDev       | Gen0      | Gen1      | Gen2     | Allocated   |
|--------------------------------------|----------- |--------------:|--------------:|-------------:|----------:|----------:|---------:|------------:|
| BindComputedColumnArithmetic         | 10         |      13.36 us |      25.58 us |     1.402 us |    1.7090 |         - |         - |    28.37 KB |
| BindComputedWithCapturedSubexpression | 10        |     365.83 us |      72.26 us |     3.961 us |    3.9063 |    2.9297 |         - |    75.44 KB |
| BindComputedColumnArithmetic         | 1000       |   1,278.09 us |     370.05 us |    20.283 us |  171.8750 |  117.1875 |         - |  2820.93 KB |
| BindComputedWithCapturedSubexpression | 1000      |  36,624.77 us |   3,658.79 us |   200.551 us |  428.5714 |  357.1429 |         - |  7538.3 KB  |
| BindComputedColumnArithmetic         | 10000      |  23,835.51 us |  11,186.79 us |   613.186 us | 1875.0000 | 1093.7500 | 562.5000 | 28304.33 KB |
| BindComputedWithCapturedSubexpression | 10000     | 380,676.09 us | 130,615.12 us | 7,159.455 us | 4000.0000 | 2000.0000 |         - | 75385.34 KB |

Headline: captured-subexpression bind is ~16× slower and ~2.7× more allocating than
arithmetic-only at 10k — this is the uncached `SetExpressionTranslator.Evaluate`
`Compile()` path P2 eliminates.

---

## SqliteExecuteBenchmarks (end-to-end)

| Method                         | Operations | Mean        | Error        | StdDev    | Gen0     | Gen1     | Allocated  |
|--------------------------------|----------- |------------:|-------------:|----------:|---------:|---------:|-----------:|
| ExecuteUpdateWhere             | 10         |    52.88 us |    99.700 us |  5.465 us |   2.9297 |        - |    49.14 KB |
| ExecuteEntityRowPrimaryKeyMatch | 10        |    40.30 us |     5.063 us |  0.278 us |   2.1973 |   0.0610 |    36.09 KB |
| ExecuteLargeInList             | 10         |    11.62 us |    21.842 us |  1.197 us |   0.6104 |        - |     10.7 KB |
| ExecuteUpdateWhere             | 100        |   439.19 us |   322.347 us | 17.669 us |  27.3438 |   3.9063 |   474.6 KB |
| ExecuteEntityRowPrimaryKeyMatch | 100       |   384.76 us |    58.970 us |  3.232 us |  19.5313 |   3.9063 |     342 KB |
| ExecuteLargeInList             | 100        |    32.23 us |    23.128 us |  1.268 us |   0.7324 |        - |    13.59 KB |
| ExecuteUpdateWhere             | 1000       | 4,481.87 us |   341.509 us | 18.719 us | 281.2500 | 125.0000 | 4727.01 KB |
| ExecuteEntityRowPrimaryKeyMatch | 1000      | 4,118.46 us | 1,194.371 us | 65.468 us | 203.1250 | 125.0000 | 3398.95 KB |
| ExecuteLargeInList             | 1000       |   245.29 us |    55.610 us |  3.048 us |   3.9063 |   0.4883 |    66.52 KB |

---

## EntityRowMatchBenchmarks (P3 target)

| Method                        | Rows  | Mean          | Error          | StdDev      | Gen0      | Gen1      | Gen2      | Allocated   |
|-------------------------------|------ |--------------:|---------------:|------------:|----------:|----------:|----------:|------------:|
| BindAndGeneratePrimaryKeyMatch | 10   |      6.112 us |      0.8104 us |   0.0444 us |    1.3123 |    0.0763 |         - |    21.55 KB |
| BindAndGenerateCustomMatch     | 10   |     14.207 us |     14.4249 us |   0.7907 us |    1.9531 |         - |         - |    32.77 KB |
| BindAndGeneratePrimaryKeyMatch | 1000 |    998.578 us |     76.2241 us |   4.1781 us |  216.7969 |  216.7969 |  216.7969 |   2061.5 KB |
| BindAndGenerateCustomMatch     | 1000 |  2,411.032 us |  9,082.0728 us | 497.8191 us |  242.1875 |  242.1875 |  242.1875 |   3074.9 KB |
| BindAndGeneratePrimaryKeyMatch | 10000 | 19,905.665 us | 10,620.4845 us | 582.1446 us | 2156.2500 | 2125.0000 | 1375.0000 | 20767.62 KB |
| BindAndGenerateCustomMatch     | 10000 | 39,735.377 us | 15,638.6631 us | 857.2079 us | 2769.2308 | 2230.7692 | 1384.6154 | 30906.83 KB |

Headline: custom match ≈ 2× PK match at 10k (per-row rewrite path — P3).

---

## LargeInListBenchmarks

| Method                 | ListSize | Mean      | Error     | StdDev    | Median    | Gen0     | Gen1     | Gen2     | Allocated  |
|----------------------- |--------- |----------:|----------:|----------:|----------:|---------:|---------:|---------:|-----------:|
| BindAndGenerateLargeIn | 500      |  25.31 us | 266.75 us | 14.621 us |  17.06 us |   1.9531 |        - |        - |   32.45 KB |
| BindAndGenerateLargeIn | 5000     | 142.02 us | 108.69 us |  5.958 us | 144.87 us |  20.5078 |   6.8359 |        - |  336.15 KB |
| BindAndGenerateLargeIn | 20000    | 876.89 us | 294.07 us | 16.119 us | 872.65 us | 234.3750 | 234.3750 | 234.3750 | 1355.39 KB |

---

## SqlGenerationBenchmarks (P4 target)

| Method                        | Operations | Mean         | Error        | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|-------------------------------|----------- |-------------:|-------------:|-------------:|----------:|----------:|----------:|------------:|
| BindAndGenerateUpdateStatements | 10        |     26.37 us |     58.79 us |     3.222 us |    3.4180 |         - |         - |    56.56 KB |
| BindAndGenerateUpdateStatements | 1000      |  2,794.51 us |  2,549.13 us |   139.727 us |  421.8750 |  375.0000 |  203.1250 |  5598.87 KB |
| BindAndGenerateUpdateStatements | 10000     | 50,304.61 us | 74,424.62 us | 4,079.465 us | 4000.0000 | 2500.0000 | 1500.0000 | 56158.76 KB |

Headline: ~5.6 KB allocated per generated statement — intermediate-string emission (P4).
