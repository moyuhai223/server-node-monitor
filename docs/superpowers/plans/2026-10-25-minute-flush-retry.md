# P1-1 MinuteFlush 修复实施记录

用户已确认设计；仅处理 P1-1，不改主题或其他审查项，不提交、推送或发布。

1. 保存源文件、已有主题修改及四角色快照；原始 SHA256 为 ef8930594d6eef5cc1cf3f07247b46244f0c0aefcfc1c2c55ba3a05460cdcb41。
2. 用同一 SQLite 故障注入测试先跑 BASELINE：新增 6 项均失败（保存/提交失败、两种取消、并发 rollover、重叠 flush）。
3. 写锁覆盖收集、事务和确认；提交前保留 PendingDays/PendingClosed，提交后按快照条目移除；失败及取消恢复 metric rows 和 Dirty，取消继续抛出。
4. 跑 MODIFIED 同输入、全量 .NET 及既有 Node 回归；用独立副本执行回滚并重跑相同测试。
5. 扩展既有 MODIFIED_FILE.zip、DIFF_FILE.patch、VERIFICATION.txt、ROLLBACK.sh，保留历史记录，验证 patch 可重建候选包、回滚哈希一致、原有主题哈希不变后交付。

writing-plans 技能在本机所列技能路径不存在，采用上述项目内可执行计划；不自动创建 Git 提交。
