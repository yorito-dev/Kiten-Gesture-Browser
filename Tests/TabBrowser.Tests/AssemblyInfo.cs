using Xunit;

// WPFとネイティブWindowの共有資源を使うため、試験同士の干渉を避ける。
// 各試験のSTAは維持し、全件を順番に実行する。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
