using BLite.Core;
using BLite.Shared;

namespace BLite.Tests;

/// <summary>
/// Opening a database whose WAL still holds committed transactions replays that WAL inside
/// the <see cref="BLite.Core.Storage.StorageEngine"/> constructor. Hosts construct the
/// engine from dependency injection on UI threads whose <see cref="SynchronizationContext"/>
/// only pumps while the thread is idle. The recovery must therefore never resume on the
/// caller's context; a single continuation posted to the blocked thread freezes the host on
/// its startup screen forever.
/// </summary>
public class StartupRecoverySynchronizationContextTests : IDisposable
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"test_startup_recovery_{Guid.NewGuid()}.db");

    [Fact]
    public async Task Open_WithPendingWal_OnBlockingSynchronizationContext_DoesNotDeadlock()
    {
        await WriteCommittedTransactionAndCloseWithoutCheckpointAsync();
        var walPath = Path.ChangeExtension(_dbPath, ".wal");
        Assert.True(File.Exists(walPath) && new FileInfo(walPath).Length > 0, "The closed database must leave a pending WAL behind.");

        var opened = await OpenOnBlockingContextAsync();

        Assert.True(opened, $"Constructing the engine with a pending WAL did not complete within {OpenTimeout.TotalSeconds:0} s on a thread whose SynchronizationContext never pumps (deadlock).");
        Assert.Equal(0, new FileInfo(walPath).Length);
    }

    [Fact]
    public async Task Open_WithPendingWal_ReplaysCommittedRows()
    {
        await WriteCommittedTransactionAndCloseWithoutCheckpointAsync();

        using var reopened = new TestDbContext(_dbPath);
        var users = await reopened.Users.FindAllAsync().ToListAsync();

        Assert.Contains(users, user => user.Name == "Alice");
    }

    private async Task WriteCommittedTransactionAndCloseWithoutCheckpointAsync()
    {
        var db = new TestDbContext(_dbPath);
        using (var txn = db.BeginTransaction())
        {
            await db.Users.InsertAsync(new User { Name = "Alice", Age = 30 }, txn);
            await txn.CommitAsync();
        }

        // Dispose releases the file locks but does not checkpoint or truncate the WAL,
        // exactly like a process that was killed after committing.
        db.Dispose();
    }

    private async Task<bool> OpenOnBlockingContextAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new NeverPumpingSynchronizationContext());
                using var reopened = new TestDbContext(_dbPath);
                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "blite-startup-recovery-ui-like",
        };
        thread.Start();

        var finished = await Task.WhenAny(completion.Task, Task.Delay(OpenTimeout));
        if (!ReferenceEquals(finished, completion.Task))
            return false;

        return await completion.Task;
    }

    /// <summary>Queues posted work forever, like a UI dispatcher whose thread is blocked.</summary>
    private sealed class NeverPumpingSynchronizationContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
                _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Synchronous dispatch is not expected while the engine is being constructed.");

        public override SynchronizationContext CreateCopy() => this;
    }

    public void Dispose()
    {
        foreach (var path in new[] { _dbPath, Path.ChangeExtension(_dbPath, ".wal"), Path.ChangeExtension(_dbPath, ".wal-shm") })
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup */ }
        }

        GC.SuppressFinalize(this);
    }
}
