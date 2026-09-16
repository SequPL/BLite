using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using BLite.Core.Storage;
using BLite.Core.Transactions;

namespace BLite.Tests;

public sealed class CheckpointRetirementRaceTests
{
    [Fact]
    public async Task CommitDuringCheckpointRemoval_RemainsVisibleAndInTheWal()
    {
        var pages = new MemoryPageStorage(PageFileConfig.Default.PageSize);
        pages.Open();
        using var storage = new StorageEngine(pages, new MemoryWriteAheadLog());
        var comparer = new RemovalInterleavingComparer();
        var field = typeof(StorageEngine).GetField("_walIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var original = (ConcurrentDictionary<uint, byte[]>)field.GetValue(storage)!;
        field.SetValue(storage, new ConcurrentDictionary<uint, byte[]>(original, comparer));

        var pageId = storage.AllocatePage();
        var previous = Enumerable.Repeat((byte)41, storage.PageSize).ToArray();
        var latest = Enumerable.Repeat((byte)73, storage.PageSize).ToArray();
        using (var seed = storage.BeginTransaction())
        {
            storage.WritePage(pageId, seed.TransactionId, previous);
            await seed.CommitAsync();
        }

        using var next = storage.BeginTransaction();
        storage.WritePage(pageId, next.TransactionId, latest);
        var committedDuringRemoval = false;
        comparer.BeforeRemoval = () =>
        {
            // The in-memory WAL completes this overload synchronously. Publish a real
            // commit after the checkpoint chose its victim but before bucket removal.
            var commit = storage.CommitTransactionAsync(next.TransactionId);
            Assert.True(commit.IsCompletedSuccessfully);
            committedDuringRemoval = true;
        };

        await storage.CheckpointAsync();
        Assert.True(committedDuringRemoval, "The test must exercise the removal interleaving.");
        var actual = new byte[storage.PageSize];
        storage.ReadPage(pageId, null, actual);
        Assert.Equal(latest, actual);
        Assert.True(storage.GetWalSize() > 0, "The newer page has not been checkpointed yet.");

        await storage.CheckpointAsync();
        pages.ReadPage(pageId, actual);
        Assert.Equal(latest, actual);
        Assert.Equal(0, storage.GetWalSize());
    }

    // No production timing hook: a dictionary comparer controls the interval before
    // ConcurrentDictionary takes the bucket lock. Both key-only and conditional
    // removal reach TryRemoveInternal; ordinary lookup/publication must not trigger it.
    private sealed class RemovalInterleavingComparer : IEqualityComparer<uint>
    {
        public Action? BeforeRemoval;
        public bool Equals(uint x, uint y) => x == y;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int GetHashCode(uint value)
        {
            if (BeforeRemoval != null && new StackTrace().GetFrames().Any(frame =>
                    frame.GetMethod()?.Name == "TryRemoveInternal"))
                Interlocked.Exchange(ref BeforeRemoval, null)?.Invoke();
            return value.GetHashCode();
        }
    }
}
