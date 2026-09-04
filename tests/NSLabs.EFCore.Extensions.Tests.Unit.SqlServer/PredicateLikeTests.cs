using Microsoft.EntityFrameworkCore;
using NSLabs.EFCore.Extensions.Internal;

namespace NSLabs.EFCore.Extensions.Tests.Unit.SqlServer;

public class PredicateLikeTests
{
    [Fact]
    public void Contains_renders_like()
    {
        var (sql, p) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("foo")).Set(x => x.Key3, 1)));
        Assert.Contains("[Key1] LIKE @p", sql);
        Assert.Contains("%foo%", Harness.Params(p).Values.Select(v => v?.ToString()).FirstOrDefault(v => v != null && v.Contains("foo")) ?? "");
    }

    [Fact]
    public void StartsWith_renders_like_prefix()
    {
        var (sql, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.StartsWith("bar")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        Assert.Contains("bar%", pars.First(v => v.Value?.ToString()?.EndsWith("%") == true).Value?.ToString() ?? "");
    }

    [Fact]
    public void EndsWith_renders_like_suffix()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.EndsWith("baz")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
    }

    [Fact]
    public void In_collection_renders_in_clause()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("IN (", sql);
        Assert.Contains("[Id] IN", sql);
    }

    [Fact]
    public void Not_in_renders_not_in()
    {
        var ids = new[] { 1, 2, 3 };
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => !ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("NOT", sql);
        Assert.Contains("IN (", sql);
    }

    [Fact]
    public void IsNullOrEmpty_renders_is_null_or_empty()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => string.IsNullOrEmpty(x.Key1)).Set(x => x.Key3, 1)));
        Assert.Contains("IS NULL", sql);
        Assert.Contains("= @p", sql);
    }

    [Fact]
    public void Like_renders_like()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => EF.Functions.Like(x.Key1, "%test%")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
    }

    [Fact]
    public void Not_contains_renders_not_like()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => !x.Key1.Contains("neg")).Set(x => x.Key3, 1)));
        Assert.Contains("NOT", sql);
        Assert.Contains("LIKE", sql);
    }

    [Fact]
    public void Contains_with_special_chars_escapes_like()
    {
        var (sql, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a%b[c]")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        var val = Harness.Params(pars).Values.First(v => v?.ToString()?.Contains("a") == true)?.ToString() ?? "";
        Assert.Contains("[%]", val);
        Assert.Contains("[[]", val);
    }

    [Fact]
    public void Contains_with_underscore_escapes_like()
    {
        var (sql, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a_b")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        var val = Harness.Params(pars).Values.First(v => v?.ToString()?.Contains("a") == true)?.ToString() ?? "";
        Assert.Contains("[_]", val);
    }

    [Fact]
    public void Contains_with_all_like_special_chars_escapes()
    {
        var (sql, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a%b_c[d]e")).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE @p", sql);
        var val = Harness.Params(pars).Values.First(v => v?.ToString()?.Contains("a") == true)?.ToString() ?? "";
        Assert.Contains("[%]", val);
        Assert.Contains("[_]", val);
        Assert.Contains("[[]", val);
        Assert.StartsWith("%", val);
        Assert.EndsWith("%", val);
    }

    [Fact]
    public void Contains_with_no_special_chars_is_not_escaped()
    {
        var (_, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("abc")).Set(x => x.Key3, 1)));
        var val = Harness.Params(pars).Values.First(v => v?.ToString() == "%abc%")?.ToString();
        Assert.Equal("%abc%", val);
    }

    [Fact]
    public void Empty_in_renders_false()
    {
        var ids = Array.Empty<int>();
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("1=0", sql);
    }

    [Fact]
    public void List_contains_renders_in()
    {
        var ids = new List<int> { 5, 6 };
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("IN (", sql);
    }

    [Fact]
    public void IsNullOrWhiteSpace_renders_trim_check()
    {
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => string.IsNullOrWhiteSpace(x.Key1)).Set(x => x.Key3, 1)));
        Assert.Contains("IS NULL", sql);
        Assert.Contains("TRIM", sql);
    }

    [Fact]
    public void Equals_renders_equal()
    {
        var (sql, pars) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Equals("foo")).Set(x => x.Key3, 1)));
        Assert.Contains("[Key1] = @p", sql);
        Assert.Equal("foo", Harness.Params(pars).Values.First(v => v?.ToString() == "foo"));
    }

    [Fact]
    public void Combined_contains_and_in()
    {
        var ids = new[] { 1, 2 };
        var (sql, _) = Harness.GenerateSingle(b => b.Update<Item>(op => op.Where(x => x.Key1.Contains("a") && ids.Contains(x.Id)).Set(x => x.Key3, 1)));
        Assert.Contains("LIKE", sql);
        Assert.Contains("IN (", sql);
        Assert.Contains("AND", sql);
    }
}
