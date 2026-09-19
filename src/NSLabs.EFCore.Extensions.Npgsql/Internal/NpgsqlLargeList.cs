using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

// PostgreSQL dialect for large `IN` lists (LARGE_LIST_SUPPORT_PLAN.md §4.3).
// Provider-local by design: the core assembly carries no provider dialect, so the
// typed-array builder lives here, in the Npgsql package, next to its generator.
// Shared neutral plumbing (null partition/count, byte[] detection) lives in the
// core LargeListHelper and is reused from here.
internal static class NpgsqlLargeList
{
    // Builds a typed non-nullable CLR array from provider-converted, non-null values
    // (int→int[], string→string[], …). Npgsql infers the PG array type from the CLR
    // array, so no NpgsqlDbType reference is needed. Element type follows the
    // first-non-null rule, falling back to the property CLR type (unwrapping
    // nullable/enum to the underlying type, matching post-ConvertToProvider values).
    public static Array BuildTypedArray(IProperty property, IReadOnlyList<object?> nonNulls)
    {
        var elementType = ResolveElementType(property, nonNulls);
        var array = Array.CreateInstance(elementType, nonNulls.Count);
        for (var i = 0; i < nonNulls.Count; i++)
        {
            array.SetValue(nonNulls[i], i);
        }

        return array;
    }

    private static Type ResolveElementType(IProperty property, IReadOnlyList<object?> nonNulls)
    {
        for (var i = 0; i < nonNulls.Count; i++)
        {
            if (nonNulls[i] is { } value)
            {
                var runtime = value.GetType();
                return runtime.IsEnum ? Enum.GetUnderlyingType(runtime) : runtime;
            }
        }

        var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        return clr.IsEnum ? Enum.GetUnderlyingType(clr) : clr;
    }
}
