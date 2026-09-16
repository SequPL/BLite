# Checkpoint retirement and collection routing races

## Retire only the checkpointed page version

Checkpoint retirement previously read the current WAL-index entry, compared its
buffer reference to the flushed snapshot, then removed the entry by page ID.
A commit between comparison and removal could publish a newer buffer which the
checkpoint then removed. Reads fell back to the older disk page. If the index
became empty, the WAL containing the newer commit could also be truncated.

Retirement now uses `ConcurrentDictionary`'s atomic key/value removal through
`ICollection<KeyValuePair<uint, byte[]>>.Remove`. Array equality preserves the
buffer identity check while comparison and removal share the dictionary lock.
The newer committed page remains visible and journaled until a later checkpoint.

`CheckpointRetirementRaceTests` interleaves a real commit with dictionary removal
using a test-only key comparer. It fails on the old code by reading the previous
page bytes and passes after the fix. It also verifies WAL retention and the next
checkpoint's page contents. There is no timing hook in production code.

## Publish and read collection slots consistently

Server layout stores data in separate files and encodes the collection slot in
page IDs. Registration modifies both slot/name mappings under a lock, but the
reverse lookup previously read an ordinary `Dictionary` without that lock.
It could observe a missing reverse mapping during publication or dictionary
growth. The missing-slot fallback incorrectly treated the entire encoded page
ID as a physical main-file page number, potentially reading outside its mapping.

Reverse lookups now acquire the registration lock. Unknown collection slots
throw explicitly instead of falling back to the main file.
`CollectionRoutingRaceTests` pauses registration between the two map writes,
checks that lookup resolves to the collection file, and checks unknown-slot
rejection without invoking an unsafe read on the old implementation.

## Repeatable consistency workloads

The eight-worker read/write test now requires every reader's first read before
writers can finish. Cancellation can no longer precede an unscheduled reader.

The cross-collection test uses 512 rounds of three concurrent inserts and a
concurrent checkpoint. It validates all IDs, complete values and duplicates for
all three entity types before and after reopen. This replaces a five-second
unbounded producer race whose transaction count depended on host throughput.
The engine's lock timeouts and test parallelism remain unchanged.
