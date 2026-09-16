using System.Reflection;
using BLite.Core.Storage;

namespace BLite.Tests;

public sealed class PageFileCheckpointTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"blite_checkpoint_{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowFlush_DoesNotBlockPageOperations_AndFlushesToDisk(bool asynchronous)
    {
        using var pages = new PageFile(_path, PageFileConfig.Default);
        pages.Open();
        var pageId = pages.AllocatePage();
        var original = new byte[pages.PageSize];
        Array.Fill(original, (byte)42);
        pages.WritePage(pageId, original);

        // Replace only the file I/O endpoint, keeping the real memory map and locks.
        // The checkpoint remains stalled until the foreground operations finish.
        var field = typeof(PageFile).GetField("_fileStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var stream = (FileStream)field.GetValue(pages)!;
        using var slowStream = new DelayedFlushStream(stream);
        field.SetValue(pages, slowStream);
        var flush = Task.Run(async () =>
        {
            if (asynchronous) await pages.FlushAsync();
            else pages.Flush();
        });
        try
        {
            await slowStream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var allocated = pages.AllocatePage();
            pages.WritePage(allocated, original);
            var read = new byte[pages.PageSize];
            await pages.ReadPageAsync(allocated, read);
            Assert.Equal(original, read);
            pages.FreePage(allocated);
            Assert.Equal(allocated, pages.AllocatePage());
        }
        finally
        {
            slowStream.Release.Set();
            try { await flush.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { field.SetValue(pages, stream); }
        }

        Assert.True(slowStream.FlushedToDisk, "A checkpoint must reach stable storage before the WAL is truncated.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flush_PersistsPagesAcrossGrowthAndReopen(bool asynchronous)
    {
        var config = PageFileConfig.Default with { GrowthBlockSize = PageFileConfig.Default.PageSize * 2 };
        var expected = new byte[config.PageSize];
        Array.Fill(expected, (byte)73);
        uint lastPage;
        using (var pages = new PageFile(_path, config))
        {
            pages.Open();
            lastPage = pages.AllocatePage();
            pages.WritePage(lastPage, expected);
            for (var i = 0; i < 8; i++)
            {
                lastPage = pages.AllocatePage();
                pages.WritePage(lastPage, expected);
            }
            if (asynchronous) await pages.FlushAsync();
            else pages.Flush();
            // Read through positioned file I/O before Dispose can mask a missing flush.
            // PageFile uses FileShare.None, so use its existing handle for positioned I/O.
            var field = typeof(PageFile).GetField("_fileStream", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var stream = (FileStream)field.GetValue(pages)!;
            var actual = new byte[config.PageSize];
            Assert.Equal(actual.Length, RandomAccess.Read(stream.SafeFileHandle, actual, (long)lastPage * config.PageSize));
            Assert.Equal(expected, actual);
        }
        using var reopened = new PageFile(_path, config);
        reopened.Open();
        var restored = new byte[config.PageSize];
        reopened.ReadPage(lastPage, restored);
        Assert.Equal(expected, restored);
    }

    public void Dispose() => File.Delete(_path);

    private sealed class DelayedFlushStream(FileStream original) : FileStream(
        original.SafeFileHandle, FileAccess.ReadWrite, bufferSize: 1, isAsync: true)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool FlushedToDisk { get; private set; }

        // The original stream owns this handle and its thread-pool binding.
        protected override void Dispose(bool disposing)
        {
            if (disposing) Release.Dispose();
        }

        public override void Flush(bool flushToDisk)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Test did not release the flush.");
            base.Flush(flushToDisk);
            FlushedToDisk |= flushToDisk;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Run(() => Release.Wait(cancellationToken), cancellationToken);
            await base.FlushAsync(cancellationToken);
        }
    }
}
