# ADR-0002：精确固定上游并对升级执行差异门禁

- 状态：接受
- 日期：2026-09-11

## 决策

所有正式构建只使用 `eng/upstream.lock.json` 中的 40 位 commit SHA。更新脚本不得直接发布；候选 commit 必须先通过差异分类、shim 适配、原生/托管测试和人工审批。

当前固定提交：`4be37c28f4f51c2b191c8587e38debd41c7dd626`（2026-11-13 自 `be3d36ed7` 升级；把上游 `33ca47fc` 的 93 个提交合并进 fork，并把 2 个本地下载修复移植到重构后的 `http_util.cpp`；shim ABI 保持 1.4，shim 版本保持 0.6.0，模型家族 134 → 143、可下载包 421 → 446、任务令牌 17 → 18）。

**`repository` 已改为 fork**（`https://github.com/dongfangzhizhu/audio.cpp`）。该 pin 是 fork 的合并提交，上游仓库并不存在这个 SHA，而 CI（`native-abi.yml`）用 `repository + commit` 精确 fetch，所以锁文件必须指向能取到该提交的仓库。fork 长期保留两个下载侧定制：`HF_ENDPOINT` 镜像变量回退、Windows 系统 CA store 加载；后续升级通道因此变为「先把上游 main 合并进 fork → 解决冲突并移植定制 → 提交并推送 fork → 再更新锁文件」，不能直接用上游 tip 作为 pin。

上一固定提交：`be3d36ed7315260e5a9752f7e8e3e0de9af18567`（2026-11-02 自 `22cad6af5043` 升级，跨越 v0.9.2 后期 → v0.9.3+60 共 220 个提交；shim ABI 保持 1.4，shim 版本保持 0.6.0，模型家族 123 → 134、loader 125 → 137、可下载包 384 → 421）。

再上一固定提交：`22cad6af5043a2d7c7f1d9a0e6c9d7996a80dfc8`（2026-10-16 自 `f825d1d1b92a` 升级，跨越 v0.8.2 后期 → v0.9.2 共 150 个提交；shim ABI 保持 1.4，结构化结果 schema 2 → 3，shim 0.5.0 → 0.6.0）。

## 网络受限环境的升级通道

`github.com:443` 被本机网络策略拦截时 `git fetch` 必然失败（表现为 `fetch-pack: invalid index-pack output`）。此时用
`eng/release/import-upstream-history.py`：它经 GitHub Git Data API（`api.github.com` 可达）从目标 commit 递归收集本地缺失的提交，
按内容寻址逐条重建 blob/tree/commit（每个 SHA 与上游逐一校验，含合并提交的双亲），最后把 `refs/remotes/origin/main` 指到目标 commit。
之后照常跑 `eng/Compare-Upstream.ps1` 生成差异报告、适配 shim、测试，再用 `eng/Update-UpstreamLock.ps1` 更新锁文件。

## 后果

不会自动获得上游最新能力，但能提供可重现的二进制和明确兼容性。项目通过 `stable` 与 preview 流程平衡稳定和跟进速度。
