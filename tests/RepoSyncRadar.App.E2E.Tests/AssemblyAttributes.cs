using Xunit;

// E2E tests share one WPF app child process. Keep assembly-wide parallelization
// disabled so any future E2E collection cannot race that process for WebView2
// state or CDP ports.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
