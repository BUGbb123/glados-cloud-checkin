# GLaDOS Cloud Check-in

运行在 Cloudflare Workers 上的 GLaDOS 免费签到程序，包含签到记录核验、北京时间日期处理及重复提交保护。

**当前状态：记录解析、21 项本地测试和单次云端记录核验已通过；正式定时自动触发仍未完成验证，不能宣称无人值守签到已经完全验收。**

## 功能

- 默认每天北京时间 09:17、17:17 检查，时间可自行调整。
- 先读取当天签到记录，已有记录则跳过。
- 兼容官网返回的数字毫秒时间戳和数字字符串奖励。
- 提交后重新读取记录，仅在存在当天记录时报告成功。
- 超时或结果不明时不盲目重发；KV 不可用时不提交。
- 固定访问 GLaDOS 官方域名，不跟随重定向，不输出登录凭证。
- 仅支持免费签到，不兑换、押注、购买、续费或修改账户设置。
- 普通 HTTP 请求返回 404，不提供公开签到或凭证输入接口。

## 部署

1. 在自己的 Cloudflare 账号创建一个 Worker，并部署 `worker.mjs`。
2. 创建 Workers KV 命名空间，以 `CHECKIN_STATE` 绑定到 Worker。
3. 在 Worker 正式环境添加加密 **Secret**：`GLADOS_COOKIE`。由账号本人直接在平台填写登录 Cookie，不要放进代码或仓库。
4. 配置 Cron：`17 1,9 * * *`。Cloudflare 使用 UTC，对应北京时间每天 09:17、17:17。新增调度可能需要最多 15 分钟同步，勿把过近的测试时刻作为可靠依据。
5. 保存并启用日志，核对正式运行结果和官网当天记录。

也可以使用官方 Wrangler 部署。先替换 `wrangler.jsonc` 中的 KV 命名空间占位符，再由本人登录 Cloudflare、设置 Secret、部署。不要把 Cookie 写入配置文件。

## 本地验证

需要 Node.js 22 或更高版本，无需安装第三方运行依赖。

```sh
npm test
```

测试全部使用虚拟数据，不读取浏览器、真实 Cookie 或真实账号。覆盖实际字段类型、重复提交保护、超时、跨日、认证拒绝和存储故障。

## 状态

| 状态 | 含义 |
| --- | --- |
| `VERIFIED` | 提交后从历史记录核实当天已签到 |
| `ALREADY_DONE` | 当天已完成，未重复提交 |
| `LOGIN_NOT_CONFIGURED` | 缺少登录 Secret |
| `LOGIN_REQUIRED` / `LOGIN_OR_SITE_REJECTION` | 登录失效或站点拒绝 |
| `DEVICE_MISMATCH` | 站点拒绝跨设备提交，停止执行 |
| `SECURITY_CHECK` | 验证或限流，停止执行 |
| `REDIRECT_STOPPED` | 不跟随跳转，不跨域发送凭证 |
| `HISTORY_FORMAT_CHANGED` | 记录格式超出已支持范围，停止提交 |
| `UNVERIFIED` / `UNVERIFIED_NO_REPEAT` | 无法确认结果，当天不再自动重发 |
| `STATE_*` | KV 存储或绑定异常，停止提交 |

## 安全与限制

- Cookie 具有账号访问能力，不是只允许签到的独立授权。只保存在自己账号的加密 Secret 中；不要提交到 Git、Issue、截图或聊天。
- 网站可能要求设备匹配、重新登录或验证码。程序会停止，不伪造设备或绕过检查。
- 凭证可能过期，需由本人更新；不能承诺永久免维护或绝不漏签。
- KV 是最终一致存储，不能提供所有重复事件下严格的“恰好一次”保证。
- 当前仅记录运行日志，未配置手机或邮件故障推送；没有通知不代表签到成功。
- 真实的新一天领取仍应由正式运行的 `VERIFIED` 与官网记录核实，已有签到日期的检查不能替代该验证。
- 停止时移除 Cron 计划；撤销云端访问时删除 Secret，并在官网撤销对应会话。

此仓库模板不包含个人 Cloudflare 命名空间 ID、账号信息、部署日志、截图或真实凭证。

参考：[Cloudflare Cron Triggers](https://developers.cloudflare.com/workers/configuration/cron-triggers/)、[Cloudflare Secrets](https://developers.cloudflare.com/workers/configuration/secrets/)。
