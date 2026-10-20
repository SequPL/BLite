# Transactional B+Tree root and page lifetime repair

This fork starts from upstream 5.1.1, commit
`019ab02476534fe8c230e1268e5b269d66d076ff`. The original MIT license and copyright
notices are retained. Core's informational version identifies the fork patch.

## Reproduced defects

- Splitting a root changed its in-memory address before commit. Readers could
  follow an uncommitted page, and rollback left the index pointing at an aborted
  root. Collapsing a root had the same transaction boundary problem.
- Merged index nodes were freed before commit. Immediate reuse could destroy a
  committed node still needed by rollback. Reuse before checkpoint also allowed
  an older WAL page image to overwrite a new allocation.
- Caller-owned transactions stayed in the engine registry after completion.
  Storage overloads taking a transaction bypassed its state and event lifecycle.
  An unsuccessful commit also removed an active transaction prematurely.

## Changes

The root page address stays fixed. A split copies the left child to a new page
and rewrites the original root in the same WAL transaction. A collapse copies
the remaining child into that root. Copied page identifiers and leaf backlinks
are repaired together. A split/collapse therefore no longer fires the
`onRootChanged` callback: collection metadata already has the correct address.

Index nodes retired by a merge become eligible for reuse only after commit;
newly allocated index nodes become eligible after rollback. The checkpoint
reclaims eligible pages only after the journal drains and no transaction has
pending page writes. The free list is never updated during an uncommitted merge.

Transaction completion releases the engine registry entry. Storage overloads
delegate to that lifecycle. A cancelled or rejected commit keeps the active
transaction registered so its caller can roll it back or retry.

This repair does not add general concurrent-writer isolation or cross-process
snapshot isolation. Applications must continue to observe the engine's access
constraints. Page retirement eligibility is held in memory until checkpoint;
an abrupt exit before reclamation can leak space, but cannot authorize early
reuse. Persisting a crash-recoverable retirement queue is outside this patch.

## Regression coverage

### Overflow page lifetime and legacy free lists

Overflow deletion previously called `FreePage` immediately, before transaction
commit. A rollback left live document pages on the free list. Checkpoint could
then restore their slotted headers over free-list nodes. Reading such a node as
`PageHeader` interpreted `FreeSpaceStart = 24` as `NextPageId = 0x18000000`
(402653184), reproducing the Android out-of-bounds read exactly.

Overflow pages now use the same deferred retirement queue as index pages:
rollback preserves them, and commit makes them reusable only after checkpoint.
Allocation validates the free-list head's bounds, page identity, type and next
pointer. An invalid list is abandoned and persisted as empty; allocation then
appends pages. This preserves page contents, at the cost of potentially leaked
free space. Claimed pages are marked empty so cycles cannot allocate a page
twice while its replacement is still only in the WAL. This does not reconstruct
document bytes already overwritten by older versions.

`DocumentOverflowTests` covers rollback before/after checkpoint and repeated
large-document updates followed by further inserts. `PageFileFreeListRecoveryTests`
covers reopening files with a wild head or a live overflow page incorrectly
listed as free, with both plain and encrypted storage.

`BTreeTransactionRegressionTests` covers root split rollback through two tree
levels, merge commit/rollback, repeated merge/split/checkpoint/reopen, exact
lookups, forward/reverse scans, transaction state, callbacks, and registry
cleanup. Both single-file and separate-index-file layouts are exercised.
`IndexPageRetirementTests` verifies that retired nodes cannot be reused before
commit/checkpoint, are reusable afterwards, and cancelled commits remain active.
Existing root callback assertions now follow the stable-root contract.

Run the standalone suite with:

```sh
dotnet test tests/BLite.Tests/BLite.Tests.csproj
```

`Directory.Build.props` forms a standalone build boundary when this repository
is nested as a submodule; a parent application's global usings and central
package configuration must not affect upstream projects.
