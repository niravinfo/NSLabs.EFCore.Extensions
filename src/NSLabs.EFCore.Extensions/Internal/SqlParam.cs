namespace NSLabs.EFCore.Extensions.Internal;

// EF Core pattern: RelationalParameter is struct-like for hot param lists — reduces GC per chunk (2000 params => 2000 objects -> inline structs)
internal readonly record struct SqlParam(string Name, object? Value);
