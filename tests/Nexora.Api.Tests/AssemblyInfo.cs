// All test classes share one in-memory database (it is configured through process-wide environment
// variables), so tests that wipe data, such as "remove all workstations", must not run alongside others.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
