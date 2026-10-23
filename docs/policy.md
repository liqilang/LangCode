# 工作区权限策略（`.langcode/policy.json`）

LangCode 的审批从「类别开关」升级为三层策略：

1. **工作区硬拒绝**（本文件）：团队约束，提交到仓库，优先级最高；
2. **会话临时授权**：审批卡上的「本次会话内始终允许此工具」，只在内存里，新建 / 切换会话即清空；
3. **用户设置**：设置页的审批类别开关、`strict / balanced / trusted` 审批模式、命令解释器。

工作区文件不存在时使用默认策略（`balanced`，敏感文件需确认，拦截私网，允许 localhost）。

## 文件位置

```text
<工作区根>/.langcode/policy.json
```

多根工作区目前读取**主工作区根**的策略；子目录里的策略文件不会自动合并。

## 完整示例

```json
{
  "mode": "balanced",
  "allowedCommandPrefixes": ["dotnet", "npm", "git "],
  "deniedCommandPrefixes": ["npm publish", "git push --force"],
  "deniedPaths": ["secrets/*", "**/*.production.json", ".git/*"],
  "allowedTools": [],
  "deniedTools": ["mcp_call"],
  "blockSensitiveFiles": true,
  "allowSensitiveFiles": false,
  "blockPrivateNetwork": true,
  "allowLocalhost": true,
  "allowBrowserEvaluate": true,
  "shell": "cmd"
}
```

## 字段说明

| 字段                     | 类型                              | 默认       | 作用                                                                                   |
| ------------------------ | --------------------------------- | ---------- | -------------------------------------------------------------------------------------- |
| `mode`                   | `strict` / `balanced` / `trusted` | `balanced` | `strict` 全部确认；`balanced` 按类别开关；`trusted` 默认放行。硬拒绝与敏感文件不受影响 |
| `allowedCommandPrefixes` | string[]                          | `[]`       | 非空时，`run_command` / `run_tests` 必须以其中一个前缀开头                             |
| `deniedCommandPrefixes`  | string[]                          | `[]`       | 命中前缀直接拒绝（优先级高于允许前缀）                                                 |
| `deniedPaths`            | string[]                          | `[]`       | 禁止读写的路径；支持 `*` / `?` glob，也支持目录前缀                                    |
| `allowedTools`           | string[]                          | `[]`       | 非空时，破坏性 / 外部工具只能使用列表里的工具                                          |
| `deniedTools`            | string[]                          | `[]`       | 禁止使用的工具（优先级最高）                                                           |
| `blockSensitiveFiles`    | bool                              | `true`     | `.env` / 密钥 / 证书 / `appsettings.*.json` 等是否逐次确认                             |
| `allowSensitiveFiles`    | bool                              | `false`    | 团队明确接受风险时，放行敏感文件（不建议）                                             |
| `blockPrivateNetwork`    | bool                              | `true`     | 拦截私网 / 链路本地 / IPv6 ULA                                                         |
| `allowLocalhost`         | bool                              | `true`     | 是否允许 `localhost` / `127.0.0.1`（前端自测通常需要）                                 |
| `allowBrowserEvaluate`   | bool                              | `true`     | 是否允许 `browser_action` 的 `evaluate` / `eval`（任意 JS）                            |
| `shell`                  | `cmd` / `powershell` / `bash`     | 跟随设置   | `run_command` 使用的解释器                                                             |

> 云元数据地址（`169.254.169.254`、`metadata.google.internal`）**永远拦截**，`blockPrivateNetwork=false` 也不能放行。

## 敏感文件

默认识别：

- `.env`、`.env.*`；
- `id_rsa` / `id_dsa` / `id_ecdsa` / `id_ed25519` / `*.ppk`；
- `*.pem` / `*.key` / `*.pfx` / `*.p12` / `*.keystore` / `*.jks` / `*.kdbx`；
- `.npmrc` / `.pypirc` / `.netrc` / `.git-credentials` / `.git/config`；
- `appsettings.<Environment>.json`（基础 `appsettings.json` 不强制拦截）。

行为：

- `search_text` 与语义索引**跳过**敏感文件，避免模型在未确认前看到内容；
- `read_file` / `read_multiple_files` / 写入 / 移动 / 删除：逐次审批，且不接受「本次会话始终允许」；
- `open_url` 打开工作区敏感文件：直接拒绝，需要 `allowSensitiveFiles=true`。

## SSRF 守卫

`NetworkGuard` 应用于 `open_url` 与 `browser_action(navigate)`：

- 非 http/https 的绝对 URL 交给调用方处理；
- `localhost` / `127.0.0.1`：`allowLocalhost` 控制；
- `10/8`、`172.16/12`、`192.168/16`、`100.64/10`、`198.18/15`、IPv6 ULA / 链路本地：`blockPrivateNetwork` 控制；
- `169.254/16`、`metadata.google.internal`：永久拦截。

## 会话级授权

审批卡上的「本次会话内始终允许此工具」：

- 普通工具：按工具名授权（例如整个 `replace_in_file`）；
- `run_command`：只授权**这一条完整命令**，不会扩大到其它命令；
- 敏感文件、`deniedPaths`、`deniedTools`、网络守卫仍然优先；
- 新建会话、切换历史会话、重启 VS 后自动失效。

## 排查

- 策略加载失败时，`PermissionPolicy.Describe()` 会把错误写进系统提示的环境摘要；模型会因此避免反复尝试被拒绝的操作。
- 策略文件的布尔字段采用「安全默认值」：漏写 `blockSensitiveFiles` 仍然是 `true`，必须显式写 `false` 才关闭。
- 修改策略文件后，下一轮任务自动生效（无需重载 VS）。
