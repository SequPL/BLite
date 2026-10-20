using BLite.Core.Storage;
using BLite.Core.Encryption;

namespace BLite.Tests;

public sealed class PageFileReadBoundsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"blite_bounds_{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(0x18000000u, false)] // Wild page ID corresponding to the Android crash family.
    [InlineData(uint.MaxValue, false)]
    [InlineData(0x18000000u, true)]
    [InlineData(uint.MaxValue, true)]
    public async Task WildPageIds_ThrowManagedErrors_ForEveryReadPath(uint pageId, bool encrypted)
    {
        var config = encrypted
            ? PageFileConfig.Default with { CryptoProvider = new AesGcmCryptoProvider(new CryptoOptions("bounds-test", iterations: 1)) }
            : PageFileConfig.Default;
        using var pages = new PageFile(_path, config);
        pages.Open();
        var buffer = new byte[pages.PageSize];
        Array.Fill(buffer, (byte)0xA5);

        var full = Assert.Throws<InvalidDataException>(() => pages.ReadPage(pageId, buffer));
        Assert.Contains($"pageId={pageId}", full.Message);
        Assert.Contains("mappedLength=", full.Message);
        Assert.Throws<InvalidDataException>(() => pages.ReadPageHeader(pageId, buffer.AsSpan(0, 64)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await pages.ReadPageAsync(pageId, buffer));
        Assert.All(buffer, value => Assert.Equal((byte)0xA5, value));

        // A rejected read must release its lock and leave the database usable.
        var valid = pages.AllocatePage();
        pages.WritePage(valid, buffer);
        var actual = new byte[pages.PageSize];
        pages.ReadPage(valid, actual);
        Assert.Equal(buffer, actual);
    }

    [Fact]
    public async Task MappingBoundary_IsRejected_ThenReadableAfterGrowthAndReopen()
    {
        var config = PageFileConfig.Default with { GrowthBlockSize = PageFileConfig.Default.PageSize * 2 };
        var expected = new byte[config.PageSize];
        Array.Fill(expected, (byte)0x42);
        uint boundary;
        using (var pages = new PageFile(_path, config))
        {
            pages.Open();
            boundary = checked((uint)(new FileInfo(_path).Length / config.PageSize));
            var buffer = new byte[config.PageSize];
            pages.ReadPage(boundary - 1, buffer);
            Assert.Throws<InvalidDataException>(() => pages.ReadPage(boundary, buffer));
            Assert.Throws<InvalidDataException>(() => pages.ReadPageHeader(boundary, buffer.AsSpan(0, 64)));
            await Assert.ThrowsAsync<InvalidDataException>(async () => await pages.ReadPageAsync(boundary, buffer));

            pages.WritePage(boundary, expected);
            await pages.ReadPageAsync(boundary, buffer);
            Assert.Equal(expected, buffer);
            var header = new byte[64];
            pages.ReadPageHeader(boundary, header);
            Assert.Equal(expected[..64], header);
            pages.Flush();
        }
        using var reopened = new PageFile(_path, config);
        reopened.Open();
        var persisted = new byte[config.PageSize];
        reopened.ReadPage(boundary, persisted);
        Assert.Equal(expected, persisted);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
