# Benchmark Baseline

Current results from [run #45021556871](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/runs/45021556871).

## Environment

- BenchmarkDotNet v0.16.0-preview.2
- Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
- Intel Xeon Platinum 8573C 3.10GHz, 1 CPU, 4 logical and 2 physical cores
- .NET Runtime 11.0.0, X64 RyuJIT x86-64-v4
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)

---

## PredicateTranslationBenchmarks

| Method                     | Operations | Mean         | Error         | StdDev       | Gen0     | Gen1     | Gen2     | Allocated   |
|----------------------------|------------|--------------|---------------|--------------|----------|----------|----------|-------------|
|   BindComparisonPredicates |         10 |     19.67 μs |      6.800 μs |     0.373 μs |   0.3662 |   0.0000 |   0.0000 |    30.14 KB |
|   BindStartsWithPredicates |         10 |     14.17 μs |      0.677 μs |     0.037 μs |   0.2594 |   0.0000 |   0.0000 |    22.33 KB |
| BindClosureCompileFallback |         10 |     15.44 μs |      1.726 μs |     0.095 μs |   0.2747 |   0.0000 |   0.0000 |    24.36 KB |
|   BindComparisonPredicates |       1000 |  1,979.24 μs |    248.612 μs |    13.627 μs |  35.1563 |  23.4375 |   0.0000 |  3000.57 KB |
|   BindStartsWithPredicates |       1000 |  1,496.95 μs |    163.206 μs |     8.946 μs |  27.3438 |  19.5313 |   0.0000 |  2336.42 KB |
| BindClosureCompileFallback |       1000 |  1,575.28 μs |     61.001 μs |     3.344 μs |  29.2969 |  17.5781 |   0.0000 |  2531.82 KB |
|   BindComparisonPredicates |      10000 | 37,475.48 μs | 21,864.872 μs | 1,198.487 μs | 375.0000 | 312.5000 | 125.0000 | 30100.22 KB |
|   BindStartsWithPredicates |      10000 | 28,748.61 μs |  6,596.375 μs |   361.570 μs | 312.5000 | 281.2500 |  62.5000 | 22365.64 KB |
| BindClosureCompileFallback |      10000 | 28,256.75 μs |  1,709.940 μs |    93.728 μs | 343.7500 | 312.5000 | 125.0000 | 24318.96 KB |

---

## SetExpressionBenchmarks

| Method                                | Operations | Mean         | Error        | StdDev     | Gen0     | Gen1     | Gen2     | Allocated   |
|---------------------------------------|------------|--------------|--------------|------------|----------|----------|----------|-------------|
|          BindComputedColumnArithmetic |         10 |     17.82 μs |     3.228 μs |   0.177 μs |   0.3357 |   0.0000 |   0.0000 |    28.29 KB |
| BindComputedWithCapturedSubexpression |         10 |     22.12 μs |     1.190 μs |   0.065 μs |   0.3662 |   0.0000 |   0.0000 |     31.8 KB |
|          BindComputedColumnArithmetic |       1000 |  1,754.02 μs |    65.482 μs |   3.589 μs |  33.2031 |  17.5781 |   0.0000 |  2813.09 KB |
| BindComputedWithCapturedSubexpression |       1000 |  2,139.73 μs |   237.900 μs |  13.040 μs |  35.1563 |  19.5313 |   0.0000 |  3164.66 KB |
|          BindComputedColumnArithmetic |      10000 | 36,577.52 μs | 7,548.268 μs | 413.746 μs | 437.5000 | 375.0000 | 125.0000 | 28225.26 KB |
| BindComputedWithCapturedSubexpression |      10000 | 40,576.17 μs | 6,656.269 μs | 364.853 μs | 500.0000 | 437.5000 | 125.0000 | 31740.89 KB |

---

## CapturedValueBenchmarks

| Method                     | Operations | Mean         | Error         | StdDev       | Gen0     | Gen1     | Gen2     | Allocated   |
|----------------------------|------------|--------------|---------------|--------------|----------|----------|----------|-------------|
|       BindCapturedConstant |         10 |     11.90 μs |      1.467 μs |     0.080 μs |   0.2594 |   0.0000 |   0.0000 |    21.23 KB |
|     BindCapturedArithmetic |         10 |     21.90 μs |      0.822 μs |     0.045 μs |   0.3662 |   0.0000 |   0.0000 |     31.8 KB |
|   BindCapturedStringConcat |         10 |     12.65 μs |      2.567 μs |     0.141 μs |   0.2594 |   0.0000 |   0.0000 |    22.43 KB |
|     BindInterpolatedString |         10 |     12.60 μs |      0.720 μs |     0.039 μs |   0.2441 |   0.0000 |   0.0000 |     21.1 KB |
| BindCapturedMethodArgument |         10 |     22.50 μs |      0.426 μs |     0.023 μs |   0.3967 |   0.0000 |   0.0000 |    33.68 KB |
|  BindCapturedDecimalFactor |         10 |     20.55 μs |      0.964 μs |     0.053 μs |   0.3662 |   0.0000 |   0.0000 |    30.77 KB |
|       BindCapturedConstant |       1000 |  1,217.82 μs |     97.449 μs |     5.342 μs |  25.3906 |  15.6250 |   0.0000 |  2109.94 KB |
|     BindCapturedArithmetic |       1000 |  2,155.89 μs |    100.500 μs |     5.509 μs |  35.1563 |  19.5313 |   0.0000 |  3164.66 KB |
|   BindCapturedStringConcat |       1000 |  1,334.31 μs |     76.710 μs |     4.205 μs |  27.3438 |  15.6250 |   0.0000 |  2249.03 KB |
|     BindInterpolatedString |       1000 |  1,234.20 μs |    140.462 μs |     7.699 μs |  25.3906 |  17.5781 |   0.0000 |  2094.34 KB |
| BindCapturedMethodArgument |       1000 |  2,240.68 μs |    193.019 μs |    10.580 μs |  39.0625 |  27.3438 |   0.0000 |  3374.03 KB |
|  BindCapturedDecimalFactor |       1000 |  2,072.23 μs |    238.034 μs |    13.047 μs |  35.1563 |  19.5313 |   0.0000 |  3063.06 KB |
|       BindCapturedConstant |      10000 | 24,712.67 μs | 23,493.030 μs | 1,287.732 μs | 281.2500 | 250.0000 |  93.7500 | 21193.92 KB |
|     BindCapturedArithmetic |      10000 | 42,463.33 μs |  7,684.121 μs |   421.193 μs | 500.0000 | 437.5000 | 125.0000 | 31740.89 KB |
|   BindCapturedStringConcat |      10000 | 26,800.31 μs |  7,148.793 μs |   391.849 μs | 312.5000 | 281.2500 |  93.7500 | 22668.95 KB |
|     BindInterpolatedString |      10000 | 25,259.88 μs | 10,297.346 μs |   564.432 μs | 281.2500 | 250.0000 |  93.7500 | 21037.69 KB |
| BindCapturedMethodArgument |      10000 | 41,616.89 μs | 11,232.271 μs |   615.679 μs | 437.5000 | 375.0000 | 125.0000 |    33919 KB |
|  BindCapturedDecimalFactor |      10000 | 39,245.21 μs | 17,104.597 μs |   937.561 μs | 437.5000 | 375.0000 | 125.0000 | 32209.52 KB |

---

## SqliteExecuteBenchmarks

| Method                          | Operations | Mean        | Error      | StdDev    | Gen0    | Gen1    | Allocated  |
|---------------------------------|------------|-------------|------------|-----------|---------|---------|------------|
|              ExecuteUpdateWhere |         10 |    67.99 μs |   3.189 μs |  0.175 μs |  0.4883 |  0.0000 |   48.77 KB |
| ExecuteEntityRowPrimaryKeyMatch |         10 |    64.32 μs |   1.257 μs |  0.069 μs |  0.3662 |  0.0000 |   35.72 KB |
|              ExecuteLargeInList |         10 |    18.94 μs |   0.242 μs |  0.013 μs |  0.0916 |  0.0000 |    9.88 KB |
|              ExecuteUpdateWhere |        100 |   643.68 μs |  19.317 μs |  1.059 μs |  4.8828 |  0.9766 |   473.5 KB |
| ExecuteEntityRowPrimaryKeyMatch |        100 |   632.56 μs |  43.109 μs |  2.363 μs |  3.9063 |  0.9766 |  340.92 KB |
|              ExecuteLargeInList |        100 |    60.39 μs |   3.829 μs |  0.210 μs |  0.1221 |  0.0000 |   13.07 KB |
|              ExecuteUpdateWhere |       1000 | 6,555.64 μs | 540.448 μs | 29.624 μs | 54.6875 | 39.0625 | 4718.65 KB |
| ExecuteEntityRowPrimaryKeyMatch |       1000 | 6,203.28 μs | 510.447 μs | 27.979 μs | 39.0625 | 31.2500 | 3390.74 KB |
|              ExecuteLargeInList |       1000 |   454.50 μs |  59.006 μs |  3.234 μs |  0.4883 |  0.0000 |   66.13 KB |

---

## EntityRowMatchBenchmarks

| Method                         | Rows  | Mean          | Error         | StdDev      | Gen0      | Gen1      | Gen2      | Allocated   |
|--------------------------------|-------|---------------|---------------|-------------|-----------|-----------|-----------|-------------|
| BindAndGeneratePrimaryKeyMatch |    10 |      9.341 μs |     0.8726 μs |   0.0478 μs |    0.2441 |    0.0000 |    0.0000 |    21.01 KB |
|     BindAndGenerateCustomMatch |    10 |     12.642 μs |     0.4870 μs |   0.0267 μs |    0.2899 |    0.0153 |    0.0000 |    24.69 KB |
| BindAndGeneratePrimaryKeyMatch |  1000 |  1,424.520 μs |    46.9340 μs |   2.5726 μs |  216.7969 |  216.7969 |  216.7969 |  2006.74 KB |
|     BindAndGenerateCustomMatch |  1000 |  1,856.930 μs |   159.8324 μs |   8.7610 μs |  248.0469 |  248.0469 |  248.0469 |  2243.32 KB |
| BindAndGeneratePrimaryKeyMatch | 10000 | 17,887.001 μs | 9,734.5488 μs | 533.5835 μs | 1437.5000 | 1437.5000 | 1312.5000 | 20219.87 KB |
|     BindAndGenerateCustomMatch | 10000 | 22,346.491 μs | 8,807.6011 μs | 482.7743 μs | 1468.7500 | 1468.7500 | 1312.5000 | 22588.93 KB |

---

## LargeInListBenchmarks

| Method                 | ListSize | Mean        | Error      | StdDev   | Gen0     | Gen1     | Gen2     | Allocated  |
|------------------------|----------|-------------|------------|----------|----------|----------|----------|------------|
| BindAndGenerateLargeIn |      500 |    28.85 μs |   3.045 μs | 0.167 μs |   0.3662 |   0.0305 |   0.0000 |   32.28 KB |
| BindAndGenerateLargeIn |     5000 |   235.38 μs |   4.489 μs | 0.246 μs |   3.9063 |   1.4648 |   0.0000 |  336.09 KB |
| BindAndGenerateLargeIn |    20000 | 1,577.21 μs | 101.972 μs | 5.589 μs | 234.3750 | 234.3750 | 234.3750 | 1355.09 KB |

---

## SqlGenerationBenchmarks

| Method                          | Operations | Mean         | Error        | StdDev     | Gen0      | Gen1      | Gen2      | Allocated   |
|---------------------------------|------------|--------------|--------------|------------|-----------|-----------|-----------|-------------|
| BindAndGenerateUpdateStatements |         10 |     34.73 μs |     0.377 μs |   0.021 μs |    0.6104 |    0.0000 |    0.0000 |    54.54 KB |
| BindAndGenerateUpdateStatements |       1000 |  4,365.23 μs |   892.937 μs |  48.945 μs |  210.9375 |  210.9375 |  210.9375 |  5389.29 KB |
| BindAndGenerateUpdateStatements |      10000 | 66,855.57 μs | 7,748.978 μs | 424.748 μs | 1625.0000 | 1625.0000 | 1125.0000 | 55142.21 KB |

---
