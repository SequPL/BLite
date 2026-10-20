using BLite.Core;
using BLite.Core.Storage;
using BLite.Core.Encryption;

namespace BLite.Tests;

public sealed class PageFileFreeListRecoveryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"blite_free_recovery_{Guid.NewGuid():N}.db");

    [Fact]
    public void CyclicFreeList_DoesNotAllocateTheSamePageTwiceBeforeCallerWrites()
    {
        using var file = new PageFile(_path, PageFileConfig.Default);
        file.Open();
        var first = file.AllocatePage();
        var second = file.AllocatePage();
        file.FreePage(first);
        file.FreePage(second);
        var buffer = new byte[file.PageSize];
        file.ReadPage(first, buffer);
        var header = PageHeader.ReadFrom(buffer);
        header.NextPageId = second;
        header.WriteTo(buffer);
        file.WritePage(first, buffer);

        var allocated = new[] { file.AllocatePage(), file.AllocatePage(), file.AllocatePage() };
        Assert.Equal(3, allocated.Distinct().Count());
        Assert.Equal(second, allocated[0]);
        Assert.Equal(first, allocated[1]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void InvalidFreeList_AllocatesWithoutOverwritingLivePage_AfterReopen(bool wildHead, bool encrypted)
    {
        PageFileConfig Config() => encrypted
            ? PageFileConfig.Default with { CryptoProvider = new AesGcmCryptoProvider(new CryptoOptions("recovery-test", iterations: 1)) }
            : PageFileConfig.Default;
        uint liveId;
        var expected = new byte[PageFileConfig.Default.PageSize];
        using (var file = new PageFile(_path, Config()))
        {
            file.Open();
            liveId = file.AllocatePage();
            new SlottedPageHeader { PageId = liveId, PageType = PageType.Overflow,
                FreeSpaceStart = 24, FreeSpaceEnd = (ushort)expected.Length }.WriteTo(expected);
            expected[100] = 42;
            file.WritePage(liveId, expected);
            var headerBytes = new byte[file.PageSize];
            file.ReadPage(0, headerBytes);
            var header = PageHeader.ReadFrom(headerBytes);
            header.NextPageId = wildHead ? 0x18000000u : liveId;
            header.WriteTo(headerBytes);
            file.WritePage(0, headerBytes);
            file.Flush();
        }
        using (var file = new PageFile(_path, Config()))
        {
            file.Open();
            var allocated = file.AllocatePage();
            Assert.NotEqual(liveId, allocated);
            file.WritePage(allocated, new byte[file.PageSize]);
            var actual = new byte[file.PageSize];
            file.ReadPage(liveId, actual);
            Assert.Equal(expected, actual);
            file.ReadPage(0, actual);
            Assert.Equal(0u, PageHeader.ReadFrom(actual).NextPageId);
            file.Flush();
        }
        using var reopened = new PageFile(_path, Config());
        reopened.Open();
        var persisted = new byte[reopened.PageSize];
        reopened.ReadPage(liveId, persisted);
        Assert.Equal(expected, persisted);
        Assert.NotEqual(liveId, reopened.AllocatePage());
    }

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
}
