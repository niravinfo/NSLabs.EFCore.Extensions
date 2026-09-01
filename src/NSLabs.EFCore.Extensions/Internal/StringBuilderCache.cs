using System.Text;

namespace NSLabs.EFCore.Extensions.Internal;

// EF Core pattern: Microsoft.EntityFrameworkCore.Internal.StringBuilderCache / IndentedStringBuilder pooling
// ThreadStatic cache avoids LOH/StringBuilder alloc per BuildChunk
// Follows EF Core's StringBuilderPool + BCL StringBuilderCache (max 360 capacity gate).
internal static class StringBuilderCache
{
    private const int MaxCachedCapacity = 1024;

    [ThreadStatic]
    private static StringBuilder? t_cached;

    public static StringBuilder Acquire(int capacity = 16)
    {
        var sb = t_cached;
        if (sb is not null && sb.Capacity >= capacity)
        {
            t_cached = null;
            sb.Clear();
            return sb;
        }

        return new StringBuilder(capacity);
    }

    public static string GetStringAndRelease(StringBuilder sb)
    {
        var result = sb.ToString();
        Release(sb);
        return result;
    }

    public static void Release(StringBuilder sb)
    {
        if (sb.Capacity <= MaxCachedCapacity && t_cached is null)
        {
            sb.Clear();
            t_cached = sb;
        }
    }
}
