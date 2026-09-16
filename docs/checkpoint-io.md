# Checkpoint I/O and page locks

`PageFile.WritePage` previously created, flushed and disposed a writable mapping
for every page while holding the exclusive page lock. Disposing a writable
`MemoryMappedViewAccessor` also flushes it, so this performed two synchronous
view flushes per page. A background checkpoint could keep foreground allocation
waiting beyond the default 500 ms lock timeout.

Page writes now copy into a persistent writable mapping under the existing
exclusive page lock. Reads retain the shared lock and cannot observe torn page
copies. No lock timeout or test parallelism setting was increased.

Both flush entry points now capture an independent mapped view under the page
lock, release that lock, flush the view, then call `FileStream.Flush(true)`.
The separate I/O gate prevents backup, truncation and disposal from closing the
stream while a flush is running. File growth can proceed while the captured
view remains alive. Resizing retires the old writable accessor without its
implicit flush; the shared dirty pages remain in the OS cache and the new
mapping covers them. Backup, truncation and disposal explicitly flush mappings.
Disposal drains in-flight I/O instead of closing resources after a lock timeout.

`FlushAsync` formerly called `FileStream.FlushAsync`, which does not request a
flush to stable storage. Checkpoint completion must include both mapped-view
flush and disk flush before `StorageEngine` truncates the WAL. It now schedules
that synchronous OS work away from the caller after acquiring the async gate.

The storage interface requires explicit `Flush` for durability; `WritePage`
updates the shared mapping. Transaction commits retain their existing WAL
durability behavior. The on-disk format is unchanged.

`PageFileCheckpointTests` uses a real memory map and a controlled file stream to
hold checkpoint I/O while allocating, writing, reading and recycling pages.
Before the fix the synchronous case reproduces the exact `AllocatePage`
timeout, and the asynchronous case detects the missing disk flush. Additional
cases check positioned file reads before disposal and reopen after growth.
The existing torn-read, encryption, backup, truncate, transaction and HNSW tests
remain enabled.

Reference: [.NET 10 MemoryMappedViewAccessor implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.MemoryMappedFiles/src/System/IO/MemoryMappedFiles/MemoryMappedViewAccessor.cs).
