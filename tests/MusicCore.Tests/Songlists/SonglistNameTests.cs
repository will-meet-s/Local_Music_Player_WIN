using MusicCore.Songlists;
using Xunit;

namespace MusicCore.Tests.Songlists;

public class SonglistNameTests
{
    private static readonly (Guid Id, string Name)[] NoExisting = Array.Empty<(Guid, string)>();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　　")] // 全角空格
    [InlineData("\t\n")]
    [InlineData(null)]
    public void Validate_EmptyOrWhitespaceOnly_ReturnsNameEmpty(string? input)
    {
        var result = SonglistName.Validate(input, NoExisting, self: null);

        Assert.False(result.IsValid);
        Assert.Equal(SonglistErrorCode.NameEmpty, result.Error);
    }

    [Fact]
    public void Validate_TrimsLeadingAndTrailingWhitespaceButKeepsMiddle()
    {
        var result = SonglistName.Validate(" 上 班 ", NoExisting, self: null);

        Assert.True(result.IsValid);
        Assert.Equal("上 班", result.TrimmedName);
    }

    [Fact]
    public void Validate_DuplicateCaseInsensitive_ReturnsNameDuplicate()
    {
        var existing = new[] { (Guid.NewGuid(), "Work") };

        var result = SonglistName.Validate("work", existing, self: null);

        Assert.False(result.IsValid);
        Assert.Equal(SonglistErrorCode.NameDuplicate, result.Error);
    }

    [Fact]
    public void Validate_DuplicateAgainstTrimmedExistingName_ReturnsNameDuplicate()
    {
        var existing = new[] { (Guid.NewGuid(), "通勤") };

        var result = SonglistName.Validate(" 通勤　", existing, self: null);

        Assert.False(result.IsValid);
        Assert.Equal(SonglistErrorCode.NameDuplicate, result.Error);
    }

    [Fact]
    public void Validate_RenameSkipsComparingAgainstSelf()
    {
        var id = Guid.NewGuid();
        var existing = new[] { (id, "Work") };

        var result = SonglistName.Validate("work", existing, self: id);

        Assert.True(result.IsValid);
        Assert.Equal("work", result.TrimmedName);
    }

    [Theory]
    [InlineData(100, false)] // 100 个「长」字
    [InlineData(101, true)]  // 101 个「长」字
    public void Validate_LengthBoundaryByCharacterCount(int count, bool expectTooLong)
    {
        var name = new string('长', count);

        var result = SonglistName.Validate(name, NoExisting, self: null);

        Assert.Equal(!expectTooLong, result.IsValid);
        if (expectTooLong) Assert.Equal(SonglistErrorCode.NameTooLong, result.Error);
    }

    [Fact]
    public void Validate_HundredEmoji_IsValid()
    {
        var name = string.Concat(Enumerable.Repeat("🎵", 100));

        var result = SonglistName.Validate(name, NoExisting, self: null);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_NinetyNineCharsPlusSurrogatePairCharacter_IsValid()
    {
        // 99 个「长」字 + 1 个代理对汉字（𠮷，U+20BB7），按 LengthInTextElements 应该算 100
        var name = new string('长', 99) + "\U00020BB7";

        var result = SonglistName.Validate(name, NoExisting, self: null);

        Assert.True(result.IsValid);
    }
}
