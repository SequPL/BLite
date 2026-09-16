using BLite.Shared;

namespace BLite.Tests
{
    /// <summary>
    /// Regression coverage for concurrent writes to different collections of the same single-file
    /// database. Each collection locks its own writes, but page placement depends on file-wide
    /// state — the shared FreeSpaceIndex and the page allocator — and FindPageWithSpace and
    /// InsertIntoPage are two separate calls. Without a file-wide write lock two collections are
    /// handed the same page: the loser either fails the space check
    /// ("Not enough space: need N, have M | PageId=...") or, when the check passes, writes over
    /// slots another collection's primary index still points at.
    /// </summary>
    public class CrossCollectionWriteRaceTests : IDisposable
    {
        private const int Rounds = 512;

        private readonly string _path;

        public CrossCollectionWriteRaceTests()
        {
            _path = Path.Combine(Path.GetTempPath(), $"blite_xcoll_{Guid.NewGuid()}.db");
        }

        public void Dispose()
        {
            foreach (var file in new[] { _path, Path.ChangeExtension(_path, ".wal") })
            {
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [Fact]
        public async Task Concurrent_Inserts_Into_Different_Collections_LoseNothing()
        {
            var strings = new Dictionary<string, string>();
            var integers = new Dictionary<int, string>();
            var users = new Dictionary<BLite.Bson.ObjectId, (string Name, int Age)>();
            using (var db = new TestDbContext(_path))
            {
                // A round is a start/finish boundary for three concurrent writes. Each
                // collection must make progress; a fast producer cannot flood the
                // shared gate while another is waiting for its thread-pool continuation.
                // Checkpoints race with each batch instead of depending on a timer
                // or machine throughput to cross the automatic checkpoint threshold.
                for (var i = 1; i <= Rounds; i++)
                {
                    var sequence = i;
                    var roundStarted = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await Task.WhenAll(
                            Task.Run(async () =>
                            {
                                var entity = new StringEntity
                                {
                                    Id = Guid.NewGuid().ToString(),
                                    Value = new string('s', 200 + sequence % 900),
                                };
                                await db.StringEntities.InsertAsync(entity);
                                strings.Add(entity.Id, entity.Value);
                            }),
                            Task.Run(async () =>
                            {
                                var entity = new IntEntity
                                {
                                    Id = sequence,
                                    Name = new string('i', 200 + sequence % 900),
                                };
                                await db.IntEntities.InsertAsync(entity);
                                integers.Add(entity.Id, entity.Name);
                            }),
                            Task.Run(async () =>
                            {
                                var entity = new User
                                {
                                    Name = new string('u', 200 + sequence % 900),
                                    Age = sequence,
                                };
                                await db.Users.InsertAsync(entity);
                                users.Add(entity.Id, (entity.Name, entity.Age));
                            }),
                            Task.Run(() => db.Storage.CheckpointAsync()));
                    }
                    catch (Exception error)
                    {
                        throw new InvalidOperationException(
                            $"Concurrent write round {sequence}/{Rounds} failed after {roundStarted.ElapsedMilliseconds} ms; "
                            + $"thread pool threads={ThreadPool.ThreadCount}, pending={ThreadPool.PendingWorkItemCount}.", error);
                    }
                }
                await Verify(db);
            }

            using var reopened = new TestDbContext(_path);
            await Verify(reopened);

            async Task Verify(TestDbContext db)
            {
                var seenStrings = new HashSet<string>();
                await foreach (var entity in db.StringEntities.FindAllAsync())
                {
                    Assert.True(seenStrings.Add(entity.Id), "Duplicate string ID");
                    Assert.Equal(strings[entity.Id], entity.Value);
                }
                Assert.Equal(Rounds, seenStrings.Count);

                var seenIntegers = new HashSet<int>();
                await foreach (var entity in db.IntEntities.FindAllAsync())
                {
                    Assert.True(seenIntegers.Add(entity.Id), "Duplicate integer ID");
                    Assert.Equal(integers[entity.Id], entity.Name);
                }
                Assert.Equal(Rounds, seenIntegers.Count);

                var seenUsers = new HashSet<BLite.Bson.ObjectId>();
                await foreach (var entity in db.Users.FindAllAsync())
                {
                    Assert.True(seenUsers.Add(entity.Id), "Duplicate user ID");
                    Assert.Equal(users[entity.Id], (entity.Name, entity.Age));
                }
                Assert.Equal(Rounds, seenUsers.Count);
            }
        }
    }
}
