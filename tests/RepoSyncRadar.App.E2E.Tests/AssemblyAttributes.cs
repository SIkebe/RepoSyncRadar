using Xunit;

// E2E tests launch the WPF app as a child process. Running two app instances
// concurrently risks WebView2 user-data folder lock contention and CDP port
// races. SeededAppHostFixture and AppHostFixture each spawn their own process,
// so we serialize the entire assembly. Within a collection xUnit already runs
// tests sequentially; this attribute extends that guarantee across collections.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
