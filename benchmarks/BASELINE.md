# Benchmark Baseline

Current results from [run #44988502240](https://github.com/niravinfo/NSLabs.EFCore.Extensions/actions/runs/44988502240).

## Environment

- BenchmarkDotNet v0.15.8
- Linux Ubuntu 26.04.1 LTS (Resolute Raccoon)
- Intel Xeon Platinum 8573C 3.10GHz, 1 CPU, 4 logical and 2 physical cores
- .NET SDK 11.0.100 / .NET 10.0.15, X64 RyuJIT x86-64-v4
- Job: ShortRun (IterationCount=3, LaunchCount=1, WarmupCount=3)

---

## PredicateTranslationBenchmarks

| Method                     | Operations | Mean         | Error        | StdDev     | Gen0     | Gen1     | Gen2    | Allocated   |
|--------------------------- |----------- |-------------:|-------------:|-----------:|---------:|---------:|--------:|------------:|
| BindComparisonPredicates   | 10         |     20.02 μs |     2.867 μs |   0.157 μs |   0.3662 |        - |       - |    30.22 KB |
| BindStartsWithPredicates   | 10         |     14.41 μs |     3.529 μs |   0.193 μs |   0.2441 |        - |       - |    22.41 KB |
| BindClosureCompileFallback | 10         |     16.11 μs |     4.204 μs |   0.230 μs |   0.2441 |        - |       - |    24.44 KB |
| BindComparisonPredicates   | 1000       |  2,045.09 μs |   352.532 μs |  19.323 μs |  31.2500 |        - |       - |  3008.38 KB |
| BindStartsWithPredicates   | 1000       |  1,474.85 μs |   355.041 μs |  19.461 μs |  23.4375 |   7.8125 |       - |  2234.86 KB |
| BindClosureCompileFallback | 1000       |  1,896.48 μs | 4,976.177 μs | 272.761 μs |  15.6250 |        - |       - |  2430.25 KB |
| BindComparisonPredicates   | 10000      | 31,244.10 μs | 5,968.189 μs | 327.137 μs | 250.0000 |        - |       - | 30178.24 KB |
| BindStartsWithPredicates   | 10000      | 28,268.64 μs | 6,591.356 μs | 361.294 μs | 312.5000 | 281.2500 | 93.7500 | 22443.82 KB |
| BindClosureCompileFallback | 10000      | 25,068.28 μs | 1,375.770 μs |  75.411 μs | 200.0000 |        - |       - | 24396.97 KB |

---

## SetExpressionBenchmarks

| Method                                | Operations | Mean         | Error         | StdDev       | Gen0     | Gen1     | Gen2     | Allocated   |
|-------------------------------------- |----------- |-------------:|--------------:|-------------:|---------:|---------:|---------:|------------:|
| BindComputedColumnArithmetic          | 10         |     18.58 μs |      5.260 μs |     0.288 μs |   0.2441 |        - |        - |    28.37 KB |
| BindComputedWithCapturedSubexpression | 10         |     25.63 μs |     68.737 μs |     3.768 μs |   0.2441 |        - |        - |    31.88 KB |
| BindComputedColumnArithmetic          | 1000       |  1,955.25 μs |  2,662.836 μs |   145.959 μs |  31.2500 |  15.6250 |        - |  2820.91 KB |
| BindComputedWithCapturedSubexpression | 1000       |  2,339.58 μs |  2,741.426 μs |   150.267 μs |  31.2500 |  15.6250 |        - |  3172.47 KB |
| BindComputedColumnArithmetic          | 10000      | 37,806.32 μs |  3,056.010 μs |   167.510 μs | 428.5714 | 357.1429 | 142.8571 |  29397.2 KB |
| BindComputedWithCapturedSubexpression | 10000      | 32,306.64 μs | 22,663.955 μs | 1,242.288 μs | 250.0000 |        - |        - | 31818.89 KB |

---

## CapturedValueBenchmarks

| Method                     | Operations | Mean         | Error         | StdDev     | Gen0     | Gen1     | Gen2     | Allocated   |
|--------------------------- |----------- |-------------:|--------------:|-----------:|---------:|---------:|---------:|------------:|
| BindCapturedConstant       | 10         |     13.50 μs |      7.014 μs |   0.384 μs |   0.2441 |        - |        - |    21.31 KB |
| BindCapturedArithmetic     | 10         |     26.86 μs |     65.511 μs |   3.591 μs |   0.2441 |        - |        - |    31.88 KB |
| BindCapturedStringConcat   | 10         |     13.42 μs |      3.272 μs |   0.179 μs |   0.2441 |        - |        - |    22.51 KB |
| BindInterpolatedString     | 10         |     14.06 μs |     13.834 μs |   0.758 μs |   0.2441 |        - |        - |    21.18 KB |
| BindCapturedMethodArgument | 10         |     28.15 μs |     71.838 μs |   3.938 μs |   0.2441 |        - |        - |    33.76 KB |
| BindCapturedDecimalFactor  | 10         |     22.46 μs |      5.051 μs |   0.277 μs |   0.3662 |        - |        - |    30.84 KB |
| BindCapturedConstant       | 1000       |  1,428.19 μs |    339.296 μs |  18.598 μs |  23.4375 |   7.8125 |        - |  2258.38 KB |
| BindCapturedArithmetic     | 1000       |  2,248.70 μs |    428.118 μs |  23.467 μs |  31.2500 |  15.6250 |        - |  3172.47 KB |
| BindCapturedStringConcat   | 1000       |  1,400.62 μs |    547.057 μs |  29.986 μs |  23.4375 |   7.8125 |        - |  2256.84 KB |
| BindInterpolatedString     | 1000       |  1,332.79 μs |    513.405 μs |  28.141 μs |  23.4375 |  15.6250 |        - |  2102.15 KB |
| BindCapturedMethodArgument | 1000       |  2,336.20 μs |    317.562 μs |  17.407 μs |  31.2500 |        - |        - |  3381.84 KB |
| BindCapturedDecimalFactor  | 1000       |  2,196.54 μs |    403.769 μs |  22.132 μs |  31.2500 |  15.6250 |        - |  3070.88 KB |
| BindCapturedConstant       | 10000      | 25,772.27 μs |  8,008.050 μs | 438.948 μs | 281.2500 | 218.7500 |  93.7500 | 21272.08 KB |
| BindCapturedArithmetic     | 10000      | 31,502.43 μs |  3,275.165 μs | 179.523 μs | 250.0000 |        - |        - | 31818.89 KB |
| BindCapturedStringConcat   | 10000      | 30,484.52 μs | 10,416.978 μs | 570.990 μs | 343.7500 | 312.5000 |  93.7500 | 22747.11 KB |
| BindInterpolatedString     | 10000      | 27,827.87 μs |  8,715.185 μs | 477.709 μs | 281.2500 | 250.0000 |  93.7500 | 21115.85 KB |
| BindCapturedMethodArgument | 10000      | 34,086.60 μs |  6,733.100 μs | 369.064 μs | 250.0000 |        - |        - | 33997.01 KB |
| BindCapturedDecimalFactor  | 10000      | 42,467.54 μs | 14,900.730 μs | 816.759 μs | 461.5385 | 384.6154 | 153.8462 | 32365.86 KB |

---

## SqliteExecuteBenchmarks

| Method                          | Operations | Mean        | Error      | StdDev    | Gen0    | Gen1    | Allocated  |
|-------------------------------- |----------- |------------:|-----------:|----------:|--------:|--------:|-----------:|
| ExecuteUpdateWhere              | 10         |    82.05 μs | 177.120 μs |  9.709 μs |  0.4883 |       - |    48.7 KB |
| ExecuteEntityRowPrimaryKeyMatch | 10         |    65.60 μs |   3.846 μs |  0.211 μs |  0.3662 |       - |   35.64 KB |
| ExecuteLargeInList              | 10         |    24.14 μs |  94.146 μs |  5.160 μs |  0.1221 |       - |   10.16 KB |
| ExecuteUpdateWhere              | 100        |   695.65 μs | 472.326 μs | 25.890 μs |  3.9063 |       - |  469.91 KB |
| ExecuteEntityRowPrimaryKeyMatch | 100        |   638.27 μs | 147.100 μs |  8.063 μs |  3.9063 |       - |  337.32 KB |
| ExecuteLargeInList              | 100        |    62.24 μs |  33.149 μs |  1.817 μs |       - |       - |   13.34 KB |
| ExecuteUpdateWhere              | 1000       | 6,977.87 μs | 989.630 μs | 54.245 μs | 31.2500 |       - |  4679.9 KB |
| ExecuteEntityRowPrimaryKeyMatch | 1000       | 6,307.14 μs | 450.307 μs | 24.683 μs | 39.0625 | 31.2500 | 3351.99 KB |
| ExecuteLargeInList              | 1000       |   460.36 μs |  48.285 μs |  2.647 μs |       - |       - |   66.52 KB |

---

## EntityRowMatchBenchmarks

| Method                         | Rows  | Mean          | Error          | StdDev      | Gen0      | Gen1      | Gen2      | Allocated   |
|------------------------------- |------ |--------------:|---------------:|------------:|----------:|----------:|----------:|------------:|
| BindAndGeneratePrimaryKeyMatch | 10    |      9.216 μs |      0.2458 μs |   0.0135 μs |    0.2441 |         - |         - |    21.09 KB |
| BindAndGenerateCustomMatch     | 10    |     12.560 μs |      0.8400 μs |   0.0460 μs |    0.2899 |    0.0153 |         - |    24.69 KB |
| BindAndGeneratePrimaryKeyMatch | 1000  |  1,264.586 μs |    210.2280 μs |  11.5233 μs |  216.7969 |  216.7969 |  216.7969 |  2014.63 KB |
| BindAndGenerateCustomMatch     | 1000  |  1,632.934 μs |    330.5858 μs |  18.1205 μs |  248.0469 |  248.0469 |  248.0469 |   2243.4 KB |
| BindAndGeneratePrimaryKeyMatch | 10000 | 18,458.393 μs |  5,179.7794 μs | 283.9212 μs | 1437.5000 | 1437.5000 | 1312.5000 | 20298.48 KB |
| BindAndGenerateCustomMatch     | 10000 | 21,290.966 μs | 10,304.5174 μs | 564.8254 μs | 1343.7500 | 1343.7500 | 1187.5000 | 22589.02 KB |

---

## LargeInListBenchmarks

| Method                 | ListSize | Mean        | Error        | StdDev     | Gen0     | Gen1     | Gen2     | Allocated  |
|----------------------- |--------- |------------:|-------------:|-----------:|---------:|---------:|---------:|-----------:|
| BindAndGenerateLargeIn | 500      |    26.22 μs |     2.574 μs |   0.141 μs |   0.2441 |        - |        - |   32.28 KB |
| BindAndGenerateLargeIn | 5000     |   288.67 μs |   280.642 μs |  15.383 μs |   3.9063 |        - |        - |  335.98 KB |
| BindAndGenerateLargeIn | 20000    | 1,703.02 μs | 5,937.396 μs | 325.449 μs | 234.3750 | 234.3750 | 234.3750 | 1355.16 KB |

---

## SqlGenerationBenchmarks

| Method                          | Operations | Mean         | Error        | StdDev       | Gen0      | Gen1      | Gen2      | Allocated   |
|-------------------------------- |----------- |-------------:|-------------:|-------------:|----------:|----------:|----------:|------------:|
| BindAndGenerateUpdateStatements | 10         |     37.46 μs |     14.92 μs |     0.818 μs |    0.4883 |         - |         - |     55.4 KB |
| BindAndGenerateUpdateStatements | 1000       |  5,211.41 μs | 30,425.66 μs | 1,667.733 μs |  187.5000 |  187.5000 |  187.5000 |  5397.14 KB |
| BindAndGenerateUpdateStatements | 10000      | 65,628.73 μs | 13,316.66 μs |   729.931 μs | 1666.6667 | 1666.6667 | 1333.3333 | 54127.35 KB |
