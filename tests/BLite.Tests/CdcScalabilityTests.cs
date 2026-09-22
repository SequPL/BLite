using System.Threading.Channels;
using BLite.Shared;

namespace BLite.Tests;

public class CdcScalabilityTests : IDisposable
{
    private readonly TestDbContext _db;
    private readonly string _dbPath;

    public CdcScalabilityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cdc_scaling_{Guid.NewGuid()}.db");
        _db = new TestDbContext(_dbPath);
    }

    [Fact]
    public async Task Test_Cdc_1000_Subscribers_Receive_Events()
    {
        const int SubscriberCount = 1000;
        var eventCounts = new int[SubscriberCount];
        var subscriptions = new List<IDisposable>();

        // 1. Create 1000 subscribers
        for (int i = 0; i < SubscriberCount; i++)
        {
            int index = i;
            var sub = _db.People.Watch().Subscribe(_ => 
            {
                Interlocked.Increment(ref eventCounts[index]);
            });
            subscriptions.Add(sub);
        }

        // 2. Perform some writes
        await _db.People.InsertAsync(new Person { Id = 1, Name = "John", Age = 30 });
        await _db.People.InsertAsync(new Person { Id = 2, Name = "Jane", Age = 25 });
        await _db.SaveChangesAsync();

        // 3. Wait for events to propagate
        await Task.Delay(1000);

        // 4. Verify all subscribers received both events
        for (int i = 0; i < SubscriberCount; i++)
        {
            Assert.Equal(2, eventCounts[i]);
        }

        foreach (var sub in subscriptions) sub.Dispose();
    }

    [Fact]
    public async Task Test_Cdc_Slow_Subscriber_Does_Not_Block_Others()
    {
        var timeout = TimeSpan.FromSeconds(10);
        var fastEvents = Channel.CreateUnbounded<int>();
        var slowEvents = Channel.CreateUnbounded<int>();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var slowSub = _db.People.Watch().Subscribe(change =>
        {
            if (change.DocumentId == 1)
            {
                slowStarted.TrySetResult();
                // Deliberately block OnNext until the fast subscriber receives both events.
                // An async callback would return early and would not test subscriber isolation.
#pragma warning disable xUnit1031
                releaseSlow.Task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            }
            slowEvents.Writer.TryWrite(change.DocumentId);
        });

        using var fastSub = _db.People.Watch().Subscribe(change =>
            fastEvents.Writer.TryWrite(change.DocumentId));

        try
        {
            await _db.People.InsertAsync(new Person { Id = 1, Name = "John", Age = 30 });
            await _db.SaveChangesAsync();

            await slowStarted.Task.WaitAsync(timeout);
            Assert.Equal(1, await fastEvents.Reader.ReadAsync().AsTask().WaitAsync(timeout));

            // Keep the slow callback blocked throughout the second write and delivery.
            await _db.People.InsertAsync(new Person { Id = 2, Name = "Jane", Age = 25 });
            await _db.SaveChangesAsync();

            Assert.Equal(2, await fastEvents.Reader.ReadAsync().AsTask().WaitAsync(timeout));
            Assert.False(slowEvents.Reader.TryRead(out _));
        }
        finally
        {
            // Also release the callback when an assertion or timeout fails.
            releaseSlow.TrySetResult();
        }

        Assert.Equal(1, await slowEvents.Reader.ReadAsync().AsTask().WaitAsync(timeout));
        Assert.Equal(2, await slowEvents.Reader.ReadAsync().AsTask().WaitAsync(timeout));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        var wal = Path.ChangeExtension(_dbPath, ".wal");
        if (File.Exists(wal)) File.Delete(wal);
    }
}
