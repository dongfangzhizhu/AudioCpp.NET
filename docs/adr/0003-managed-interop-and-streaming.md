# ADR-0003：LibraryImport、SafeHandle 与 pull streaming

- 状态：接受
- 日期：2026-09-11

## 决策

.NET 10 使用源生成 `LibraryImport`、`NativeLibrary.Load` 和 `SafeHandle`。初版 PCM 从 native buffer 复制到 managed memory。流式阶段采用 push input + poll event，在 C# 中映射为 `IAsyncEnumerable<T>`，不使用 native thread 回调 managed delegate。

## 后果

首版存在一次输出复制，但生命周期清晰、适合异常和取消路径，也更容易支持 NativeAOT。流式 pull 模式便于背压和线程治理。
