using System.Reflection;
using BLite.Core.Indexing;
using BLite.Core.Storage;
using BLite.Core.Transactions;

namespace BLite.Tests;

public sealed class IndexPageRetirementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"blite_retirement_{Guid.NewGuid():N}");

    public IndexPageRetirementTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergedPages_AreReusableOnlyAfterCommitAndCheckpoint(bool separateIndexFile)
    {
        var config = PageFileConfig.Default with
        {
            IndexFilePath = separateIndexFile ? Path.Combine(_directory, "data.idx") : null
        };
        using var storage = new StorageEngine(Path.Combine(_directory, "data.db"), config);
        var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"));
        using (var seed = storage.BeginTransaction())
        {
            for (var key = 0; key < 400; key++)
                index.Insert(IndexKey.Create(key), new DocumentLocation((uint)(key + 1), 0), seed.TransactionId);
            await seed.CommitAsync();
        }
        await storage.CheckpointAsync();
        var treePages = index.CollectAllPages().ToHashSet();
        using (var deletion = storage.BeginTransaction())
        {
            for (var key = 1; key < 400; key++)
                Assert.True(index.Delete(IndexKey.Create(key), new DocumentLocation((uint)(key + 1), 0), deletion.TransactionId));

            // A pending deletion must not put committed pages on the free list.
            Assert.DoesNotContain(storage.AllocateIndexPage(), treePages);
            await deletion.CommitAsync();
        }

        // Older WAL images must be drained before a retired node is reused.
        Assert.DoesNotContain(storage.AllocateIndexPage(), treePages);
        await storage.CheckpointAsync();
        var reusedPage = storage.AllocateIndexPage();
        Assert.Contains(reusedPage, treePages);
        Assert.NotEqual(index.RootPageId, reusedPage);
        Assert.True(index.TryFind(IndexKey.Create(0), out _));
    }

    [Fact]
    public async Task CancelledCommit_KeepsTransactionRegisteredUntilRollback()
    {
        using var storage = new StorageEngine(Path.Combine(_directory, "data.db"), PageFileConfig.Default);
        using var transaction = storage.BeginTransaction();
        var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"));
        for (var key = 0; key < 400; key++)
            index.Insert(IndexKey.Create(key), new DocumentLocation((uint)(key + 1), 0), transaction.TransactionId);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.CommitTransactionAsync(transaction, cancellation.Token));
        Assert.Equal(TransactionState.Active, transaction.State);
        var registry = typeof(StorageEngine).GetField("_activeTransactions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(storage)!;
        Assert.Equal(1, (int)registry.GetType().GetProperty("Count")!.GetValue(registry)!);
        await storage.RollbackTransactionAsync(transaction);
        Assert.Equal(TransactionState.Aborted, transaction.State);
        Assert.Equal(0, (int)registry.GetType().GetProperty("Count")!.GetValue(registry)!);
        Assert.Empty(index.Range(IndexKey.MinKey, IndexKey.MaxKey));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
