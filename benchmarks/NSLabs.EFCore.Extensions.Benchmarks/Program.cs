using BenchmarkDotNet.Running;

namespace NSLabs.EFCore.Extensions.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
