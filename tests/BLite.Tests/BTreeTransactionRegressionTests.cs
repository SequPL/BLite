using System.Reflection;
using BLite.Core.Indexing;
using BLite.Core.Storage;
using BLite.Core.Transactions;

namespace BLite.Tests;

public sealed class BTreeTransactionRegressionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"blite_txn_{Guid.NewGuid():N}");
    private string DatabasePath => Path.Combine(_directory, "index.db");

    public BTreeTransactionRegressionTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootSplit_RollbackPreservesCommittedKeysAndRoot(bool separateIndexFile)
    {
        var config = Configuration(separateIndexFile);
        uint root;
        using (var storage = new StorageEngine(DatabasePath, config))
        {
            var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"));
            root = index.RootPageId;
            using (var seed = storage.BeginTransaction())
            {
                Insert(index, 0, 1, seed.TransactionId);
                await seed.CommitAsync();
            }
            using (var transaction = storage.BeginTransaction())
            {
                // Enough entries to split both leaves and an internal root.
                Insert(index, 1, 3000, transaction.TransactionId);
                await transaction.RollbackAsync();
            }

            // Assert before traversal: the original bug can otherwise loop on an empty page.
            Assert.Equal(root, index.RootPageId);
            AssertKeys(index, 0, 1);
            await storage.CheckpointAsync();
        }
        using var reopened = new StorageEngine(DatabasePath, config);
        AssertKeys(new BTreeIndex(reopened, IndexOptions.CreateBTree("key"), root), 0, 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RootCollapse_PreservesCommittedOrRolledBackTree(bool rollback, bool separateIndexFile)
    {
        var config = Configuration(separateIndexFile);
        uint root;
        using (var storage = new StorageEngine(DatabasePath, config))
        {
            var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"));
            using (var seed = storage.BeginTransaction())
            {
                Insert(index, 0, 400, seed.TransactionId);
                await seed.CommitAsync();
            }
            root = index.RootPageId;
            await storage.CheckpointAsync();
            using (var transaction = storage.BeginTransaction())
            {
                Delete(index, 1, 399, transaction.TransactionId);
                if (rollback) await transaction.RollbackAsync();
                else await transaction.CommitAsync();
            }
            Assert.Equal(root, index.RootPageId);
            AssertKeys(index, 0, rollback ? 400 : 1);
            await storage.CheckpointAsync();
        }
        using var reopened = new StorageEngine(DatabasePath, config);
        AssertKeys(new BTreeIndex(reopened, IndexOptions.CreateBTree("key"), root), 0, rollback ? 400 : 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedMergeSplitCheckpointAndReopen_PreserveAllLookups(bool separateIndexFile)
    {
        var config = Configuration(separateIndexFile);
        uint root = 0;
        for (var cycle = 0; cycle < 6; cycle++)
        {
            using var storage = new StorageEngine(DatabasePath, config);
            var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"), root);
            using (var insert = storage.BeginTransaction())
            {
                Insert(index, 0, 400, insert.TransactionId);
                await insert.CommitAsync();
            }
            await storage.CheckpointAsync();
            AssertKeys(index, 0, 400);
            using (var delete = storage.BeginTransaction())
            {
                Delete(index, 0, 400, delete.TransactionId);
                await delete.CommitAsync();
            }
            await storage.CheckpointAsync();
            AssertKeys(index, 0, 0);
            root = index.RootPageId;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedTransactions_ReleaseRegistryAndRunLifecycle(bool throughStorage)
    {
        using var storage = new StorageEngine(DatabasePath, PageFileConfig.Default);
        var index = new BTreeIndex(storage, IndexOptions.CreateBTree("key"));
        var commits = 0;
        var rollbacks = 0;
        for (var number = 0; number < 100; number++)
        {
            using var transaction = storage.BeginTransaction();
            transaction.OnCommit += () => commits++;
            transaction.OnRollback += () => rollbacks++;
            Insert(index, number, 1, transaction.TransactionId);
            if (number % 2 == 0)
            {
                if (throughStorage) await storage.CommitTransactionAsync(transaction);
                else await transaction.CommitAsync();
                Assert.Equal(TransactionState.Committed, transaction.State);
            }
            else
            {
                if (throughStorage) await storage.RollbackTransactionAsync(transaction);
                else await transaction.RollbackAsync();
                Assert.Equal(TransactionState.Aborted, transaction.State);
            }
        }
        Assert.Equal(50, commits);
        Assert.Equal(50, rollbacks);
        var registry = typeof(StorageEngine).GetField("_activeTransactions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(storage)!;
        Assert.Equal(0, (int)registry.GetType().GetProperty("Count")!.GetValue(registry)!);
    }

    private PageFileConfig Configuration(bool separateIndexFile) => PageFileConfig.Default with
    {
        IndexFilePath = separateIndexFile ? Path.Combine(_directory, "index.idx") : null
    };

    private static void Insert(BTreeIndex index, int start, int count, ulong transactionId)
    {
        foreach (var key in Enumerable.Range(start, count))
            index.Insert(IndexKey.Create(key), new DocumentLocation((uint)(key + 1), 0), transactionId);
    }

    private static void Delete(BTreeIndex index, int start, int count, ulong transactionId)
    {
        foreach (var key in Enumerable.Range(start, count))
            Assert.True(index.Delete(IndexKey.Create(key), new DocumentLocation((uint)(key + 1), 0), transactionId));
    }

    private static void AssertKeys(BTreeIndex index, int start, int count)
    {
        var expected = Enumerable.Range(start, count).ToArray();
        foreach (var key in expected)
        {
            Assert.True(index.TryFind(IndexKey.Create(key), out var location), $"Missing key {key}");
            Assert.Equal((uint)(key + 1), location.PageId);
        }
        Assert.Equal(expected, index.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Forward)
            .Take(count + 1).Select(entry => entry.Key.As<int>()));
        Assert.Equal(expected.Reverse(), index.Range(IndexKey.MinKey, IndexKey.MaxKey, IndexDirection.Backward)
            .Take(count + 1).Select(entry => entry.Key.As<int>()));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
