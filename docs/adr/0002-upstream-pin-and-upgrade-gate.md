# ADR-0002：精确固定上游并对升级执行差异门禁

- 状态：接受
- 日期：2026-09-11

## 决策

所有正式构建只使用 `eng/upstream.lock.json` 中的 40 位 commit SHA。更新脚本不得直接发布；候选 commit 必须先通过差异分类、shim 适配、原生/托管测试和人工审批。

当前固定提交：`22cad6af5043a2d7c7f1d9a0e6c9d7996a80dfc8`（2026-10-16 自 `f825d1d1b92a` 升级，跨越 v0.8.2 后期 → v0.9.2 共 150 个提交；shim ABI 保持 1.4，结构化结果 schema 2 → 3，shim 0.5.0 → 0.6.0）。

## 网络受限环境的升级通道

`github.com:443` 被本机网络策略拦截时 `git fetch` 必然失败（表现为 `fetch-pack: invalid index-pack output`）。此时用
`eng/release/import-upstream-history.py`：它经 GitHub Git Data API（`api.github.com` 可达）从目标 commit 递归收集本地缺失的提交，
按内容寻址逐条重建 blob/tree/commit（每个 SHA 与上游逐一校验，含合并提交的双亲），最后把 `refs/remotes/origin/main` 指到目标 commit。
之后照常跑 `eng/Compare-Upstream.ps1` 生成差异报告、适配 shim、测试，再用 `eng/Update-UpstreamLock.ps1` 更新锁文件。

## 后果

不会自动获得上游最新能力，但能提供可重现的二进制和明确兼容性。项目通过 `stable` 与 preview 流程平衡稳定和跟进速度。
