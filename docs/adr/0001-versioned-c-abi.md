# ADR-0001：使用版本化 C ABI shim

- 状态：接受
- 日期：2026-09-11

## 背景

`audio.cpp` 没有稳定 C API；核心接口包含 C++ 类、STL、异常和 RTTI，且更新频繁。

## 决策

在固定上游提交之上构建 `extern "C"` 共享库。ABI 只包含基础整数、UTF-8 指针、buffer+长度和不透明句柄。异常、对象生命周期和内存释放全部终止于 shim。ABI 使用 major/minor 和 capability 协商。

## 备选方案

- 直接绑定 C++ symbols：编译器/STL ABI 不稳定，拒绝。
- C++/CLI：仅 Windows/MSVC，不满足跨平台，拒绝。
- SWIG：仍需定义稳定边界，生成 API 难以长期控制，拒绝。
- 进程外 HTTP：隔离性强但增加部署和传输成本；保留为特殊部署方案，不作为此 SDK 核心。

## 后果

增加一层 C++ 适配维护成本，但把上游变化集中在 native shim，稳定托管 API，并可跨 Windows/Linux/macOS。
