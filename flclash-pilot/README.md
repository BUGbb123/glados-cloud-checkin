# FlClash Pilot

为 FlClash 提供节点择优辅助：Windows 本机工具和安卓配置脚本。根据连通性、响应延迟、波动与失败情况选择候选，不能保证每个应用的下载带宽始终最大。

本版为 **v1.1**，针对 **FlClash v0.9.1** 设计，并非 FlClash 官方软件。仓库包含程序、源码、测试和使用说明，不包含订阅、节点密码、控制口令或个人配置。

## 下载与使用

- **Windows**：在 [`dist/FlClashPilot.exe`](dist/FlClashPilot.exe) 页面点击下载原始文件，或在仓库选择 **Code → Download ZIP** 后解压。双击打开；需要 Windows 自带的 .NET Framework 4.x，不需要管理员权限。
- **安卓**：下载 [`Android/自动选线.js`](Android/自动选线.js)，在 FlClash 中导入为配置脚本，并选择新增的自动组。它不是 APK，不能作为订阅 YAML 导入。
- 完整操作、暂停与恢复方法见[使用说明](使用说明.md)。已有覆写脚本需自行合并，先保留原配置备份。

Windows 工具需要 FlClash 正在本机运行，并由本人启用仅回环地址可访问的控制器。控制口令只在工具窗口输入，关闭后不保存。首次连接后先使用“只检测一次”，点击“开始自动选线”才会接管选定策略组。

## 策略与边界

- Windows 默认每轮结束后等待 60 秒，每轮最多 12 个目标、并发 3 个。最近五次样本用于评分；一般要求候选连续两轮领先且改善达到阈值，切换后冷却三分钟。
- 发现手动改选或可识别的配置变化时暂停。只选择原策略组已有的直接代理节点；不会自动改成直连，不修改路由规则或 DNS。
- 安卓脚本增加 mihomo `url-test` 组，默认周期 300 秒、容差 80 ms。手机策略由内核执行，与 Windows 的额外确认和冷却逻辑不同。
- 小型连通性检查会产生少量代理流量。切换主要影响新连接，既有下载、视频和通话可能继续使用原线路。
- Windows 工具需要保持运行；安卓脚本依赖手机上的 FlClash 运行。本项目不是云端代理控制服务，也不能在设备关机后继续替该设备选线。

## 验证状态

本地逻辑和模拟接口测试可通过 `build.ps1 -Test` 复现。测试全部使用虚拟配置、模拟口令和本机临时接口，不连接真实 FlClash 或读取真实订阅。

Windows v1.1 的真实自动托管、恢复以及安卓手机导入仍待实机验证；没有通过真实下载测试证明提速。详见[验证说明](验证说明.md)。

## 从源码构建

在 Windows PowerShell 中进入仓库目录执行：

```powershell
.\build.ps1
```

输出为 `dist/FlClashPilot.exe`，SHA-256 摘要写入 `SHA256SUMS.txt`。使用系统自带 .NET Framework C# 编译器，不下载依赖。

运行测试另需本地 Node.js：

```powershell
.\build.ps1 -Test
```

`build/` 中的临时测试程序和日志不提交到 Git。请勿把真实订阅、控制口令或含节点信息的诊断记录加入仓库或 Issue。

## 参考

- [FlClash](https://github.com/chen08209/FlClash)
- [mihomo 控制 API](https://wiki.metacubex.one/api/)
- [mihomo 自动选择组](https://wiki.metacubex.one/config/proxy-groups/url-test/)

