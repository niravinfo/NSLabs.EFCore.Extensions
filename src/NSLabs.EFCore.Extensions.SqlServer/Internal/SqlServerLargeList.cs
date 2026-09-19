using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace NSLabs.EFCore.Extensions.Internal;

// SQL Server dialect for large `IN` lists (LARGE_LIST_SUPPORT_PLAN.md §4.2).
// Provider-local by design: the core assembly carries no provider dialect, so the
// OPENJSON WITH-type map lives here, in the SqlServer package, next to its
// generator. Shared neutral plumbing (null partition, decision rule, JSON payload)
// lives in the core LargeListHelper and is reused from here.
internal static class SqlServerLargeList
{
    // OPENJSON WITH ([Value] <type> '$') clause type (§4.2), derived from EF metadata
    // with fallback by CLR type of the provider-converted values.
    // Strings are ALWAYS nvarchar(max): a WITH (nvarchar(n)) would truncate over-long
    // inputs and false-match rows the exact value could never equal.
    public static string OpenJsonWithType(IProperty property, IReadOnlyList<object?> nonNulls)
    {
        var clr = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (clr.IsEnum)
        {
            clr = Enum.GetUnderlyingType(clr);
        }

        if (clr == typeof(decimal))
        {
            var precision = property.GetPrecision();
            var scale = property.GetScale();
            return precision.HasValue && scale.HasValue
                ? $"decimal({precision.Value},{scale.Value})"
                : "decimal(38,18)";
        }

        if (clr == typeof(int))
        {
            return "int";
        }

        if (clr == typeof(long))
        {
            return "bigint";
        }

        if (clr == typeof(short))
        {
            return "smallint";
        }

        if (clr == typeof(byte))
        {
            return "tinyint";
        }

        if (clr == typeof(bool))
        {
            return "bit";
        }

        if (clr == typeof(Guid))
        {
            return "uniqueidentifier";
        }

        if (clr == typeof(DateTime))
        {
            return "datetime2";
        }

        if (clr == typeof(DateTimeOffset))
        {
            return "datetimeoffset";
        }

        if (clr == typeof(DateOnly))
        {
            return "date";
        }

        if (clr == typeof(TimeOnly) || clr == typeof(TimeSpan))
        {
            return "time";
        }

        if (clr == typeof(char))
        {
            return "nchar(1)";
        }

        if (clr == typeof(double))
        {
            return "float";
        }

        if (clr == typeof(float))
        {
            return "real";
        }

        if (clr == typeof(string))
        {
            return "nvarchar(max)";
        }

        return property.GetColumnType()
            ?? throw new NotSupportedException(
                $"Large IN lists over '{property.Name}' ({clr.Name}) are not supported on SQL Server.");
    }
}
