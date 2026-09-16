using System.Reflection;
using BLite.Core.Storage;

namespace BLite.Tests;

public sealed class CollectionRoutingRaceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"blite_routing_{Guid.NewGuid():N}");

    [Fact]
    public void MissingCollectionSlot_MustNotFallBackToMainFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "test.db");
        using var storage = new StorageEngine(path, PageFileConfig.Server(path));
        var error = Assert.Throws<TargetInvocationException>(() => Route(storage, 0xc0000000));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public async Task RoutingDuringSlotRegistration_UsesTheCollectionFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "test.db");
        using var storage = new StorageEngine(path, PageFileConfig.Server(path));
        using var comparer = new PausedRegistrationComparer();
        typeof(StorageEngine).GetField("_collectionSlotToName", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(storage, new Dictionary<int, string>(comparer));
        var allocation = Task.Factory.StartNew(() => storage.AllocateCollectionPage("new_collection"),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<IPageStorage>? lookup = null;
        try
        {
            await comparer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            lookup = Task.Factory.StartNew(() => Route(storage, 0xc0000000),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // The old lookup finishes immediately with the main file; a synchronized
            // lookup waits for registration to finish. Release either case below.
            await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            comparer.Release.Set();
            await allocation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var routed = await lookup!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(Route(storage, 0), routed);
        Assert.Same(Route(storage, await allocation), routed);
    }

    private static IPageStorage Route(StorageEngine storage, uint pageId) =>
        (IPageStorage)typeof(StorageEngine).GetMethod("GetPageFile", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(storage, [pageId, 0U])!;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class PausedRegistrationComparer : IEqualityComparer<int>, IDisposable
    {
        private int _pause = 1;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public bool Equals(int x, int y) => x == y;
        public int GetHashCode(int value)
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            {
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Registration not released.");
            }
            return value.GetHashCode();
        }
        public void Dispose() => Release.Dispose();
    }
}
