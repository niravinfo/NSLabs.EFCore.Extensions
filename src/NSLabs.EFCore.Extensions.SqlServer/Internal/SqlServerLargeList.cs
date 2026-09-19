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
    // OPENJSON WITH ([Value] <type> '$') clause type (§4.2), or null for the untyped
    // fallback (`SELECT [v].[value] FROM OPENJSON(@p)`, EF's own shape when it cannot
    // type the payload). The type MUST describe the converted (JSON) domain, never the
    // CLR domain: a value-converted column (bool → "Y"/"N") carries strings in JSON,
    // so a CLR-derived `bit` would throw where the untyped shape succeeds. Returns null
    // whenever the type is not exactly known — untyped is always result-identical,
    // only less seekable. Never throws for an exotic type in v1.
    public static string? OpenJsonWithType(IProperty property, IReadOnlyList<object?> nonNulls)
    {
        var converter = property.GetValueConverter() ?? property.GetRelationalTypeMapping().Converter;
        if (converter is not null)
        {
            // Converted domain: the store column type is the only exact description.
            return NormalizeStoreType(property.GetColumnType());
        }

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
            // ALWAYS max: a WITH (nvarchar(n)) would truncate over-long inputs and
            // false-match rows the exact value could never equal.
            return "nvarchar(max)";
        }

        // Unmapped CLR without a converter (uint, sbyte, …): EF still knows the store
        // type — reuse it through the same normalization as the converter path.
        return NormalizeStoreType(property.GetColumnType());
    }

    // Maps a store column type to a WITH-clause type. String-ish types normalize to
    // max of the same unicode-ness (never a capped length — see above); anything
    // outside the known set yields null (untyped fallback) instead of guessing.
    private static string? NormalizeStoreType(string? storeType)
    {
        if (string.IsNullOrWhiteSpace(storeType))
        {
            return null;
        }

        var t = storeType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(');
        var baseName = paren < 0 ? t : t[..paren];
        var args = paren < 0 ? "" : t[paren..];

        switch (baseName)
        {
            case "int":
            case "bigint":
            case "smallint":
            case "tinyint":
            case "bit":
            case "uniqueidentifier":
            case "date":
            case "time":
            case "datetimeoffset":
            case "float":
            case "real":
                return baseName;
            case "datetime2":
                return "datetime2";
            case "decimal":
            case "numeric":
                // Keep exact precision/scale when present; bare decimal defaults to
                // decimal(18,0) in SQL Server, which would silently corrupt fractions.
                return args.Length > 2 ? $"decimal{args}" : null;
            case "nvarchar":
            case "nchar":
                return "nvarchar(max)";
            case "varchar":
            case "char":
                return "varchar(max)";
            default:
                return null;
        }
    }
}
