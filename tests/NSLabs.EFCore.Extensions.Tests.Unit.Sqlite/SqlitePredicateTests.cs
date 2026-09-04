using Microsoft.EntityFrameworkCore;

namespace NSLabs.EFCore.Extensions.Tests.Unit.Sqlite;

public class SqlitePredicateTests
{
    [Fact]
    public void Contains_renders_like_with_escape()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("foo")).Set(x => x.Key3, 1)));
        Assert.Contains("\"Key1\" LIKE @p", sql);
        Assert.Contains("ESCAPE '\\'", sql);
        Assert.Contains("%foo%", p.First(v => v.Value?.ToString()?.Contains("foo") == true).Value?.ToString() ?? "");
    }

    [Fact]
    public void Contains_with_special_chars_escapes_for_sqlite()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a%b_c")).Set(x => x.Key3, 1)));
        Assert.Contains("ESCAPE '\\'", sql);
        var val = p.First(v => v.Value?.ToString()?.Contains("a") == true).Value?.ToString() ?? "";
        Assert.Contains("\\%", val);
        Assert.Contains("\\_", val);
        Assert.DoesNotContain("[%]", val);
    }

    [Fact]
    public void StartsWith_renders_like_prefix()
    {
        var (sql, p) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.StartsWith("bar")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        Assert.Contains("ESCAPE '\\'", sql);
        Assert.Contains("bar%", p.First(v => v.Value?.ToString()?.StartsWith("bar") == true).Value?.ToString() ?? "");
    }

    [Fact]
    public void EndsWith_renders_like_suffix()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.EndsWith("baz")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        Assert.Contains("ESCAPE", sql);
    }

    [Fact]
    public void In_renders_in_clause_with_quotes()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("\"Id\" IN (", sql);
    }

    [Fact]
    public void IsNullOrEmpty_renders_check()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => string.IsNullOrEmpty(x.Key1)).Set(x => x.Key3, 1)));
        Assert.Contains("IS NULL", sql);
    }

    [Fact]
    public void Like_renders_like_without_extra_escape_for_user_pattern()
    {
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => Microsoft.EntityFrameworkCore.EF.Functions.Like(x.Key1, "%test%")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        Assert.Contains("ESCAPE '\\'", sql);
    }

    [Fact]
    public void Empty_in_renders_false()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = SqliteHarness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("1=0", sql);
    }
}
