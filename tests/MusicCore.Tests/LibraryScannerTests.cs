using MusicCore.Library;
using Xunit;

namespace MusicCore.Tests;

public class LibraryScannerTests : IDisposable
{
    private readonly string _root;

    public LibraryScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MusicScannerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* 清理失败不影响测试结论 */ }
    }

    private void MakeFile(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }

    [Fact]
    public void FiltersBySupportedExtension()
    {
        MakeFile("a.mp3");
        MakeFile("b.flac");
        MakeFile("c.txt");
        MakeFile("d.jpg");
        MakeFile("e.lrc");

        var names = LibraryScanner.Scan(_root).Select(Path.GetFileName).ToHashSet();

        Assert.Equal(new HashSet<string?> { "a.mp3", "b.flac" }, names);
    }

    [Fact]
    public void ExtensionMatchIsCaseInsensitive()
    {
        MakeFile("A.MP3");
        MakeFile("B.M4a");

        var names = LibraryScanner.Scan(_root).Select(Path.GetFileName).ToHashSet();

        Assert.Equal(new HashSet<string?> { "A.MP3", "B.M4a" }, names);
    }

    [Fact]
    public void RecursesIntoSubdirectories()
    {
        MakeFile("top.mp3");
        MakeFile(Path.Combine("album", "one.mp3"));
        MakeFile(Path.Combine("album", "disc2", "two.mp3"));

        var names = LibraryScanner.Scan(_root).Select(Path.GetFileName).ToHashSet();

        Assert.Equal(new HashSet<string?> { "top.mp3", "one.mp3", "two.mp3" }, names);
    }

    [Fact]
    public void SkipsHiddenDirectories()
    {
        MakeFile("visible.mp3");
        MakeFile(Path.Combine("hidden", "inside.mp3"));

        var hidden = new DirectoryInfo(Path.Combine(_root, "hidden"));
        hidden.Attributes |= FileAttributes.Hidden;

        var names = LibraryScanner.Scan(_root).Select(Path.GetFileName).ToList();

        Assert.Equal(new[] { "visible.mp3" }, names);
    }

    [Fact]
    public void EmptyDirectoryReturnsEmpty()
    {
        Assert.Empty(LibraryScanner.Scan(_root));
    }

    [Fact]
    public void MissingDirectoryReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(LibraryScanner.Scan(Path.Combine(_root, "does-not-exist")));
    }

    [Fact]
    public void NaturalSortOrder()
    {
        var paths = new[]
        {
            @"C:\m\track10.mp3",
            @"C:\m\track2.mp3",
            @"C:\m\track1.mp3"
        };

        var sorted = LibraryScanner.Sort(paths).Select(Path.GetFileName);

        Assert.Equal(new[] { "track1.mp3", "track2.mp3", "track10.mp3" }, sorted);
    }
}

public class NaturalStringComparerTests
{
    [Theory]
    [InlineData("a1", "a2", -1)]
    [InlineData("a2", "a10", -1)]
    [InlineData("a10", "a2", 1)]
    [InlineData("a01", "a1", 0)]      // 前导零不影响数值大小
    [InlineData("abc", "abc", 0)]
    [InlineData("a", "ab", -1)]
    public void ComparesNaturally(string x, string y, int expectedSign)
    {
        var result = NaturalStringComparer.Instance.Compare(x, y);
        Assert.Equal(expectedSign, Math.Sign(result));
    }

    [Fact]
    public void HandlesVeryLongNumbersWithoutOverflow()
    {
        // 直接 int.Parse 会溢出，所以实现是按位比较的
        var a = "track99999999999999999999.mp3";
        var b = "track99999999999999999998.mp3";

        Assert.True(NaturalStringComparer.Instance.Compare(a, b) > 0);
    }

    [Fact]
    public void NullsSortFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }
}
