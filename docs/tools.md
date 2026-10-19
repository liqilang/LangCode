# 工具参考

> 本文列出 LangCode 暴露给模型的全部工具、参数、执行路径与审批类别。工具定义的唯一来源是 [`ToolCatalog.cs`](../LangCode/Agent/ToolCatalog.cs)，新增工具只需要改那一处，schema、系统提示词与审批映射会同步生成。
>
> 维护提示：新增 / 修改工具时，请同步更新本文的「工具总表」与「审批与安全」两节。

## 1. 调用协议

模型用 `action` 代码块发出工具调用：

````text
```action
{"tool": "read_file", "path": "src/App.tsx", "start_line": 1, "end_line": 120}
```
````

规则：

- 一个代码块里只放一个动作；互不依赖的多个动作可以写在同一轮里一起发出。
- `tool` 是工具名，其余字段是参数。
- 字符串里的换行必须写成 `\n`；三反引号写成 `\u0060\u0060\u0060`。
- 不再需要工具时，直接用自然语言回答，并且不要再输出 `action` 块。
- 打开「原生 function calling」后，也可以由接口返回 `tool_calls`；后端会把它翻译成同样的动作块，执行路径完全一致。

动作解析与展示见 [`ToolProtocol.cs`](../LangCode/Agent/ToolProtocol.cs)。

## 2. 执行分派

| 分派路径        | 含义                                                      | 审批行为                                         |
| --------------- | --------------------------------------------------------- | ------------------------------------------------ |
| `Local`         | 本地只读 / 写准备（结果可能带 `Pending`）                 | 写准备走差异审批；只读默认自动批准               |
| `Orchestration` | 长任务编排（待办 / 提问 / 子任务 / 跳转 / 重命名 / 补丁） | 不碰磁盘的动作直接执行；补丁与重命名自行逐项审批 |
| `External`      | 有外部副作用（命令 / 测试 / 浏览器 / MCP / 调试器）       | 先问再做，危险命令直接拒绝                       |

## 3. 工具总表

### 3.1 代码理解（只读）

| 工具                   | 作用                                                       | 主要参数                         | 分派  | 审批类别 |
| ---------------------- | ---------------------------------------------------------- | -------------------------------- | ----- | -------- |
| `list_files`           | 列出工作区文件，支持 `*` / `?` 通配                        | `glob`                           | Local | 读取     |
| `read_file`            | 读取文件内容，可按起止行裁剪                               | `path`、`start_line`、`end_line` | Local | 读取     |
| `search_text`          | 按正则搜索文件内容，返回 `路径:行号: 内容`                 | `pattern`、`glob`                | Local | 读取     |
| `list_symbols`         | 列出文件里的类 / 方法 / 函数及行号（C# 走 Roslyn，含签名） | `path`                           | Local | 读取     |
| `find_references`      | 查找符号引用（Roslyn 标识符级，跳过字符串与注释）          | `symbol`、`glob`、`limit`        | Local | 读取     |
| `semantic_search`      | 语义检索：按功能 / 概念找相关代码片段                      | `query`、`limit`                 | Local | 读取     |
| `get_type_hierarchy`   | 查类型的基类 / 接口 / 已知派生类型                         | `type`                           | Local | 读取     |
| `find_implementations` | 查实现了某接口 / 继承了某基类的全部类型                    | `type`                           | Local | 读取     |

### 3.2 代码修改（写）

| 工具              | 作用                                               | 主要参数                                      | 分派          | 审批类别                         | 破坏性 |
| ----------------- | -------------------------------------------------- | --------------------------------------------- | ------------- | -------------------------------- | ------ |
| `replace_in_file` | 局部替换；`old_text` 必须在文件里唯一              | `path`、`old_text`、`new_text`、`replace_all` | Local         | 编辑                             | 是     |
| `write_file`      | 整文件写入（新建或整体重写）                       | `path`、`content`                             | Local         | 新增（覆盖已有文件时按编辑审批） | 是     |
| `edit_symbol`     | 按符号名定位代码块再改写，比字符串匹配更抗格式漂移 | `path`、`symbol`、`mode`、`new_text`          | Local         | 编辑                             | 是     |
| `apply_patch`     | 多文件补丁：一次改多处 / 跨文件重构                | `patches`                                     | Orchestration | 编辑（逐节审批）                 | 是     |
| `rename_symbol`   | 跨文件重命名（Roslyn 语义级）                      | `symbol`、`new_name`、`glob`                  | Orchestration | 编辑（逐文件审批）               | 是     |

### 3.3 长任务编排

| 工具                    | 作用                                 | 主要参数              | 分派          | 审批                 |
| ----------------------- | ------------------------------------ | --------------------- | ------------- | -------------------- |
| `update_todo_list`      | 维护任务清单                         | `todos`               | Orchestration | 无需审批             |
| `ask_followup_question` | 向用户提问并等待回答                 | `question`、`options` | Orchestration | 无需审批             |
| `new_task`              | 把一块独立的活拆成子任务             | `task`、`context`     | Orchestration | 无需审批             |
| `goto_definition`       | 在编辑器里打开文件并请求「转到定义」 | `path`、`line`        | Orchestration | 无需审批（只读跳转） |

### 3.4 执行与验证

| 工具          | 作用                           | 主要参数                       | 分派     | 审批类别 | 破坏性 |
| ------------- | ------------------------------ | ------------------------------ | -------- | -------- | ------ |
| `run_command` | 在工作区里执行 shell 命令      | `command`、`working_directory` | External | 命令     | 是     |
| `run_tests`   | 运行工作区测试，自动识别技术栈 | `filter`（仅 `dotnet test`）   | External | 命令     | 是     |

### 3.5 外部能力

| 工具             | 作用                                                                 | 主要参数                                       | 分派     | 审批类别 | 破坏性 |
| ---------------- | -------------------------------------------------------------------- | ---------------------------------------------- | -------- | -------- | ------ |
| `open_url`       | 用系统浏览器打开网页，可选抓取正文                                   | `url`、`fetch`                                 | External | 浏览器   | 是     |
| `browser_action` | DevTools 自动化：导航 / 读正文 / 点击 / 填表 / 脚本 / 截图 / console | `action`、`url`、`selector`、`value`、`script` | External | 浏览器   | 是     |
| `mcp_call`       | 调用已启用 MCP 服务器上的工具                                        | `server`、`tool`、`arguments`                  | External | MCP      | 是     |
| `debug_inspect`  | 只读查看调试器                                                       | `action`、`expression`、`limit`                | External | 读取     | 否     |
| `debug_control`  | 控制调试器（断点 / 继续 / 单步 / 暂停 / 停止）                       | `action`、`path`、`line`、`condition`          | External | 命令     | 是     |

---

## 4. 重点工具说明

### 4.1 `read_file`

```json
{ "tool": "read_file", "path": "src/App.tsx", "start_line": 1, "end_line": 120 }
```

- 路径必须相对工作区根目录，使用 `/` 分隔。
- 长文件建议带 `start_line` / `end_line`，避免一次读太多。
- 读文件成功后，界面只显示一条「读文件」卡片，点击可在 Visual Studio 里打开对应位置。

### 4.2 `replace_in_file`

```json
{
  "tool": "replace_in_file",
  "path": "src/App.tsx",
  "old_text": "旧片段（带足够上下文以保持唯一）",
  "new_text": "新片段"
}
```

- `old_text` 必须在文件里**唯一**；出现多次时会返回「原文出现了 N 次」，应补上下文重发。
- 确实要替换全部匹配时才加 `"replace_all":"true"`。
- 落盘前会先出差异卡，用户允许后才生效。

### 4.3 `write_file`

```json
{ "tool": "write_file", "path": "src/New.tsx", "content": "...整段内容..." }
```

- `content` 是 JSON 字符串：换行写成 `\n`，三反引号写成 `\u0060\u0060\u0060`。
- 新建文件按「新增文件」审批；若目标已存在，则按「编辑已有文件」审批。
- 只有新建文件或整体重写才用它；局部改动优先 `replace_in_file`。

### 4.4 `edit_symbol`

```json
{
  "tool": "edit_symbol",
  "path": "src/App.tsx",
  "symbol": "CalculateTotal",
  "mode": "replace_body",
  "new_text": "...新的方法体..."
}
```

`mode` 支持：

| 模式            | 含义                     |
| --------------- | ------------------------ |
| `replace`       | 替换整个代码块（含签名） |
| `replace_body`  | 只替换块内内容           |
| `insert_before` | 插到符号之前             |
| `insert_after`  | 插到符号之后             |
| `delete`        | 删除符号                 |

适合「重写一个已存在的方法 / 类型」：不需要原样抄 `old_text`，不受缩进和换行差异影响。

### 4.5 `apply_patch`

```json
{
  "tool": "apply_patch",
  "patches": "*** Update File: src/App.tsx\n<<<<<<< SEARCH\n旧代码\n=======\n新代码\n>>>>>>> REPLACE\n*** Add File: src/New.tsx\n<<<<<<< SEARCH\n=======\n新文件内容\n>>>>>>> REPLACE"
}
```

补丁正文按节书写：

1. `*** Update File: 相对路径`（新建文件用 `*** Add File: 相对路径`）；
2. 旧代码段以「七个小于号 + 空格 + SEARCH」开头；
3. 分隔行「七个等号」；
4. 新代码；
5. 本节以「七个大于号 + 空格 + REPLACE」收尾。

注意：

- 旧代码段必须与文件内容完全一致，且在该文件里唯一；
- 多节首尾相接即可；
- 界面会为每一节分别出差异卡，因此小改动仍优先 `replace_in_file`。

### 4.6 `rename_symbol`

```json
{
  "tool": "rename_symbol",
  "symbol": "OldName",
  "new_name": "NewName",
  "glob": "*.cs"
}
```

- Roslyn 语义级：只改真正绑定到该符号的标识符，自动跳过字符串、注释与同名局部变量；
- 逐文件列出差异卡，用户确认后落盘；
- 重命名已有类型 / 方法 / 字段时优先用它，不要手写多处 `replace_in_file`；
- 非 C# 文件或 Roslyn 不可用时，会退回标识符级匹配。

### 4.7 `run_command`

```json
{
  "tool": "run_command",
  "command": "npm test",
  "working_directory": "前端子目录(可省略)"
}
```

- `working_directory` 是相对工作区的子目录；
- 确认卡上会显示实际执行的命令；
- 输出流式回显，结果附带退出码；
- 危险命令（格式化磁盘、删系统目录、改引导等）会被 [`CommandPolicy`](../LangCode/Workspace/CommandPolicy.cs) 直接拒绝。

### 4.8 `run_tests`

```json
{ "tool": "run_tests" }
```

或：

```json
{ "tool": "run_tests", "filter": "FullyQualifiedName~Login" }
```

自动识别测试命令：

| 技术栈  | 命令                                                  |
| ------- | ----------------------------------------------------- |
| .NET    | `dotnet test`                                         |
| Node.js | `npm test`（使用探测到的包管理器：npm / pnpm / yarn） |
| Python  | `pytest`                                              |
| Rust    | `cargo test`                                          |
| Go      | `go test ./...`                                       |
| Java    | `mvn test`                                            |

`dotnet test` 会附带 TRX 记录器，返回结构化的通过 / 失败统计与失败用例堆栈。`filter` 只对 `dotnet test` 生效。

### 4.9 `browser_action`

```json
{
  "tool": "browser_action",
  "action": "navigate",
  "url": "http://localhost:3000"
}
```

`action` 支持：

| 动作         | 作用                                 |
| ------------ | ------------------------------------ |
| `navigate`   | 打开地址                             |
| `read`       | 读取页面正文                         |
| `html`       | 读取页面 HTML                        |
| `click`      | 点击元素（配合 `selector`）          |
| `fill`       | 填写表单（配合 `selector`、`value`） |
| `evaluate`   | 执行脚本（配合 `script`）            |
| `screenshot` | 截图存档                             |
| `console`    | 读取浏览器 console 报错              |
| `wait`       | 等待（配合选择器或时间）             |

省略 `url` 时，会自动使用工作区探测到的本地开发地址。浏览器自动化受 `BrowserAutomation` 开关控制。

### 4.10 `mcp_call`

```json
{
  "tool": "mcp_call",
  "server": "filesystem",
  "tool": "read_file",
  "arguments": "{\"path\":\"/tmp/a.txt\"}"
}
```

- 省略 `tool` 字段：列出该服务器的全部工具；
- 省略 `server`：使用第一个已启用的服务器；
- `arguments` 是 JSON 字符串，需要正确转义；
- 默认调用前要确认（可在设置里关闭）。

### 4.11 `debug_inspect` / `debug_control`

只读查看：

```json
{ "tool": "debug_inspect", "action": "get_stack" }
```

```json
{ "tool": "debug_inspect", "action": "evaluate", "expression": "user.Name" }
```

`debug_inspect` 的 `action`：

| 动作               | 作用               |
| ------------------ | ------------------ |
| `list_breakpoints` | 列出断点           |
| `get_stack`        | 查看调用栈         |
| `get_locals`       | 查看局部变量与参数 |
| `evaluate`         | 求值表达式         |
| `status`           | 查看调试状态       |

控制：

```json
{
  "tool": "debug_control",
  "action": "set_breakpoint",
  "path": "src/App.cs",
  "line": 42,
  "condition": "i > 3"
}
```

`debug_control` 的 `action`：

`set_breakpoint` / `remove_breakpoint` / `clear_breakpoints` / `continue` / `step_over` / `step_into` / `step_out` / `pause` / `stop`。

这些工具只在 Visual Studio 处于调试状态、且有活动栈帧时有效。

---

## 5. 长任务编排示例

### 5.1 列计划

````text
```action
{"tool":"update_todo_list","todos":"[-] 调研登录流程\n[ ] 实现表单校验\n[ ] 补测试并运行"}
```
````

### 5.2 拆子任务

````text
```action
{"tool":"new_task","task":"调研登录流程并给出校验规则清单","context":"相关文件：src/Login.tsx、src/auth/*.ts"}
```
````

同一轮里可以拆多个：

````text
```action
{"tool":"new_task","task":"调研登录流程","context":"src/Login.tsx"}
```

```action
{"tool":"new_task","task":"调研现有校验工具","context":"src/validators.ts"}
```
````

### 5.3 向用户确认

````text
```action
{"tool":"ask_followup_question","question":"校验失败提示放在哪？","options":"表单上方|字段下方|两者都提示"}
```
````

---

## 6. 审批与安全

| 工具                                                                          | 默认是否需要用户确认 |
| ----------------------------------------------------------------------------- | -------------------- |
| 只读工具（读 / 搜 / 符号 / 语义 / 类型 / 调试查看）                           | 否                   |
| `write_file` / `replace_in_file` / `edit_symbol`                              | 是                   |
| `apply_patch` / `rename_symbol`                                               | 是（逐节 / 逐文件）  |
| `run_command` / `run_tests` / `debug_control`                                 | 是                   |
| `open_url` / `browser_action`                                                 | 是                   |
| `mcp_call`                                                                    | 是                   |
| `update_todo_list` / `ask_followup_question` / `new_task` / `goto_definition` | 否                   |

「默认是否需要确认」可以在设置里按类别放开；危险命令不受自动批准影响，仍会被拒绝。

---

## 7. 工具与源码对照

| 工具组                            | 源码                                                                                                                                         |
| --------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| 定义 / schema / 提示词 / 审批类别 | [`ToolCatalog.cs`](../LangCode/Agent/ToolCatalog.cs)                                                                                         |
| 动作解析与格式化                  | [`ToolProtocol.cs`](../LangCode/Agent/ToolProtocol.cs)                                                                                       |
| 本地只读与写准备                  | [`AgentRunner.Dispatch.cs`](../LangCode/Agent/AgentRunner.Dispatch.cs)                                                                       |
| 编排类工具                        | [`AgentRunner.Orchestration.cs`](../LangCode/Agent/AgentRunner.Orchestration.cs)                                                             |
| 外部副作用工具                    | [`AgentRunner.External.cs`](../LangCode/Agent/AgentRunner.External.cs)                                                                       |
| 工具链探测与测试命令              | [`ToolchainDetector.cs`](../LangCode/Workspace/ToolchainDetector.cs)、[`AgentRunner.External.cs`](../LangCode/Agent/AgentRunner.External.cs) |
| 命令安全策略                      | [`CommandPolicy.cs`](../LangCode/Workspace/CommandPolicy.cs)                                                                                 |
| 浏览器自动化                      | [`BrowserAutomation.cs`](../LangCode/Workspace/BrowserAutomation.cs)                                                                         |
| 工作区文件操作                    | [`FileSystemWorkspace.cs`](../LangCode/Workspace/FileSystemWorkspace.cs)                                                                     |
| Roslyn 分析                       | [`RoslynCodeService.cs`](../LangCode/Roslyn/RoslynCodeService.cs)                                                                            |
| MCP                               | [`McpManager.cs`](../LangCode/Mcp/McpManager.cs)、[`McpClient.cs`](../LangCode/Mcp/McpClient.cs)                                             |
