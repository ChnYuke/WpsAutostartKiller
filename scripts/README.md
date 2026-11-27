# 03 · 参考脚本（命令行版）

这些是主方案验证过程中实际使用并跑通的原始脚本，供审阅、二次开发、或批量部署使用。

它们和 `01-手动配置版`、`02-自动版` 做的是同一件事，只是形式不同：纯命令行、无界面、可无人值守。

---

## 文件说明

| 脚本 | 作用 | 什么时候用 |
|---|---|---|
| `WPS-Autostart-Portable.ps1` | 通用版：自动探测安装路径与任务名，支持封锁 / 恢复 / 演练 | 批量部署、或想在别的电脑上用一条命令搞定 |
| `RebootProof.ps1` | 重启后判定"本次开机以来 WPS 到底有没有启动过" | 做完处置后验收 |
| `Verify-WpsAutostartLockdown.ps1` | 全量自启面扫描 + 双向行为测试 | 体检、排查"怎么又有 WPS 了" |
| `Revert-WpsAutostartLockdown.ps1` | 针对**本机原始部署**（非通用版）的还原脚本 | 只在用本机那套 `Apply-*.ps1` 部署过的情况下用 |

> 注意：`WPS-Autostart-Portable.ps1` 是自包含的（守卫壳源码、图标提取、备份还原全在文件里），**只拷这一个文件就能用**。另外三个是辅助工具。

---

## 快速用法

```powershell
# 1. 只检测，不改任何东西
.\WPS-Autostart-Portable.ps1

# 2. 演练：打印将要做什么
.\WPS-Autostart-Portable.ps1 -Apply -DryRun

# 3. 真正执行
.\WPS-Autostart-Portable.ps1 -Apply

# 4. 恢复原装
.\WPS-Autostart-Portable.ps1 -Restore

# 5. 重启后验收
.\RebootProof.ps1

# 6. 体检
.\Verify-WpsAutostartLockdown.ps1
```

执行策略受限时：

```powershell
powershell -ExecutionPolicy Bypass -File .\WPS-Autostart-Portable.ps1 -Apply
```

---

## `RebootProof.ps1` 的两个参数

| 参数 | 含义 |
|---|---|
| 无参数 | 以**开机时间**为判定锚点 |
| `-Since "2026-11-27 18:06:00"` | 以指定时刻为锚点。适合"我中途自己点过 WPS，想跳过那段"的情况 |
| `-Save` | 额外存一份快照备查（可选，不影响判定） |

---

## 判读速查

| 守卫壳日志内容 | 含义 |
|---|---|
| `BLOCKED args=[/wpsupdate -from=task -source=task]` | 计划任务来敲过门，被拦下 —— 说明拦截在工作 |
| `BLOCKED args=[/prometheus /from=autostart_after_install]` | 安装器想自我拉起，被拦下 |
| `LAUNCH args=[/prometheus /fromksolaunch /from=startmenu]` | 有人点了开始菜单/桌面图标，正常转发 |
| `LAUNCH args=[/prometheus /fromksolaunch /from=taskbar]` | 有人点了任务栏固定图标，正常转发 |
| `NO-TARGET` | 找不到 WPS 主程序，重新执行一次封锁 |
| `FAIL ... err=` | 转发抛异常，把整行留作排查依据 |

日志位置：`%LOCALAPPDATA%\Kingsoft\wps-guard.log`

---

## 批量部署思路

```powershell
$machines = @('PC-01','PC-02')
foreach ($m in $machines) {
    Copy-Item .\WPS-Autostart-Portable.ps1 "\\$m\C$\Temp\" -Force
    Invoke-Command -ComputerName $m -ScriptBlock {
        powershell -ExecutionPolicy Bypass -File C:\Temp\WPS-Autostart-Portable.ps1 -Apply
    }
}
```

前提：目标机已开启 PowerShell 远程、你有管理员凭据、WPS 装在能被当前账户改权限的位置（用户级安装最省事）。
