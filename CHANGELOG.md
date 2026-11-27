# Changelog

本项目由 AI 生成。版本号按「能力里程碑」编排，不是语义化版本。

---

## v1.5 — 2026-11-27

### 修复：计划任务名解析错了（严重，闸1 实际从未生效）

`schtasks /query /fo CSV /v /nh` 输出的**第一列是 HostName，第二列才是 TaskName**。早期版本用正则取第一个带引号的字段，于是把**计算机名**当成了任务名。

真实事故日志：

```
[20:09:41] 导出失败：LAPTOP-TSBFIP3G
[20:09:41] 任务数：1
```

任务确实找到了（1 条），但名字是主机名，于是：

- `schtasks /query /xml ONE /tn "\<主机名>"` 失败 → 任务 XML 导不出来
- `schtasks /change /tn "\<主机名>" /disable` 失败 → **闸1（禁用/删除计划任务）等于空转**
- 在任务已被删干净的机器上永远不暴露，只有「真正存在 WPS 任务的新机器」才会踩到

修复：改用 RFC4180 规则切分 CSV（处理引号与双写转义），任务名取第 1 列，`Task To Run` 取第 8 列（回退到原正则）。

实测列序验证（本机真实输出）：

```
第0列(旧代码取的) : DESKTOP-ASACLVM          ← 主机名
第1列(新代码取的) : \Microsoft\Windows\.NET Framework\.NET Framework NGEN v4.0.30319
第8列(Task To Run): COM handler
```

### 修复：「备份」按钮只做了部分备份

按钮原先只导出计划任务 XML，其余内容（manifest / 哈希 / 注册表 / 服务 / 升级配置）要等点「关闭自启」才生成，日志还提示"完整备份会在点关闭自启时生成"——与按钮语义不符。

现在按钮直接走完整备份流程，产物与「关闭自启」时自动做的那份**完全一致**，并且不再杀进程、改 ACL 或动服务。

### 修复：仓库版 build.cmd 路径错位

源码树分层后（`src/` + `assets/` + `dist/`），原 `build.cmd` 仍按扁平目录找 `assets\WpsLaunchGuard.cs`，直接报 `missing`。已重写为按仓库布局编译：源在 `src\`，图标在 `..\assets\wps.ico`，产物输出到 `..\dist\`。

### 文档

- `docs/自动版使用说明.md`：修正「备份」按钮的行为描述，补上完整备份的 6 个文件清单

---

## v1.4 — 2026-11-27

### 新增：禁用 WPS 升级通道

- 手动版新增第 12 章「闸5 升级通道」，含原理、备份、实施、可选加固、验证四步、恢复、注意事项
- 手动版新增 3.9「备份六：升级服务器地址」，备份总表从 6 项扩到 7 项
- 自动版新增「禁用升级」「恢复升级」两个按钮，状态网格新增「升级通道」三态行
- 自动版新增命令行 `--upgrade-state` / `--upgrade-disable` / `--upgrade-restore` / `--upgrade-test`
- 实现方式：把 `UpdateSvr` / `UpdateSecSvr` / `NotifySvr` 三个注册表值指向 `127.0.0.1:9`（discard 端口，连接立即 RST，不产生外网流量，也不会因超时导致 WPS 启动卡顿）

### 修复：备份防污染（重要）

原先在「已禁用」状态下再点一次「禁用升级」，会把当前的黑洞值当作"原值"写进备份，覆盖掉真正的原始地址；之后再恢复只会把黑洞地址写回去，界面还显示成功。修复后有三道保护：

1. `Disable()` 幂等 —— 已处于禁用状态直接跳过，不写注册表也不动备份
2. `WriteBackup()` 不覆盖干净备份 —— 已有干净原值备份时另存为 `upgrade-before-<时间戳>.json`
3. 恢复端双重校验 —— 跳过 `originalValue` 含 `127.0.0.1` 的备份文件；写回前断言拒绝写入黑洞值

### 修复：污染判定误报

`WriteBackup` 原先扫描**整个文件文本**判断备份是否被污染，而备份里的 `newValue` 字段本身就写着 `127.0.0.1`，导致干净备份被误判成污染并被覆盖。改为只检查 `originalValue` 字段。

### 修复：其他一致性

- `--upgrade-test` 与 `Disable()` 现在都在**任何注册表写入之前**先落备份，写失败即中止
- selftest 里两个同名 `ksolaunch.exe` 分别标注 `(WPS Office)` 与 `(office6)`，不再无法区分
- 备份按钮也会在 `backup-<时间戳>\` 里单独生成 `upgrade-before.json`，与「禁用升级」的产物对称

---

## v1.3 — 2026-11-27

### 修复：任务栏图标失灵

拦截词 `from=task` 是 `from=taskbar` 的子串，导致点任务栏固定图标被当成更新任务吞掉。

修复方式：**参数判定改为 token 精确匹配** —— 按空白切分、去掉前导 `-` 或 `/`、逐 token 全等比对。这是同一类问题的第三次翻车（前两次是 `/prometheus`、`autostart_after_install`），改完之后从结构上杜绝。

### 新增：守卫壳运行日志

`%LOCALAPPDATA%\Kingsoft\wps-guard.log`，每次调用都留痕（`LAUNCH` / `BLOCKED` / `NO-TARGET` / `FAIL`），是排查"点不开"的第一入口。

### 修复：图标变成默认程序图标

守卫壳是重新编译的 exe，没有图标资源，而快捷方式的 `IconLocation` 指向 `ksolaunch.exe,0`。修复：从原版 exe 用 `PrivateExtractIcons` 提取 16/32/48/256 四尺寸、手工拼成多尺寸 ICO，编译时 `/win32icon:` 编进去。

---

## v1.2 — 2026-11-27

### 修复：`/prometheus` 被误拦

`/prometheus` 是 WPS 的**正常启动**标志（快捷方式与文件关联都在用），被列进拦截词后导致点图标毫无反应。

### 修复：`autostart_after_install` 漏拦

安装器装完的自我拉起是 `/prometheus /from=autostart_after_install`。因为 `/prometheus` 必须放行，这条被当成正常启动放过了。补上 `autostart` 拦截。

---

## v1.1 — 2026-11-27

### 修复：`icacls` 权限位用错

原先用简写 `W`（泛型写权限），实测会连带拒绝 `READ_CONTROL`，导致文件连读都读不了、WPS 起不来。

修复：改用精确权限位 `(X,WD,AD,WEA,WA,DE)`（文件）与 `(WD,AD)`（目录）。

---

## v1.0 — 2026-11-27

### 初始版本

- 手动版：逐条命令的操作手册，含四道闸、完整源码、备份清单、五步回滚
- 自动版：WinForms GUI，检测 / 备份 / 关闭自启 / 恢复原装
- 守卫壳：替换 `ksolaunch.exe`，拦截更新与自启调用，转发正常启动
- 命令行通用版：`WPS-Autostart-Portable.ps1`，自动探测安装布局与任务名
- 验收工具：`RebootProof.ps1`、`Verify-WpsAutostartLockdown.ps1`

### 根因结论

WPS 自启不在 `Run` 注册表，而是两条计划任务调用 `ksolaunch.exe /wpsupdate`，且 `wtoolex\wpsupdate.exe` 会用 `-createtask` / `-repairtask` 把删掉的任务重建回来。证据见 `%APPDATA%\kingsoft\office6\log\update\wpsupdate_<日期>.log`。
