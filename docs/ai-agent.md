# AI 与自动化能力总览

> 面向使用者与二次开发者：说明 LangCode 的 AI 是怎么工作的、长任务怎么拆、子任务如何并行、哪些动作需要审批、上下文和费用如何控制。

## 1. 设计原则

LangCode 不是「把代码贴给 AI」的聊天框，而是一个**有工作区权限的 Agent**。它遵循四条原则：

1. **能真正干活**：直接读写工作区文件、跑命令、跑测试、操作浏览器、查看调试器。
2. **动你东西之前先问你**：写文件、执行命令、浏览器操作、MCP 调用默认都要确认；危险命令直接拒绝。
3. **长任务看得见**：用任务清单列出步骤，完成一项更新一项；可以随时打断、继续、恢复。
4. **改错了能回去**：每轮开始打检查点，支持「撤销上一步」「撤销本轮」「还原到某条消息之前」。

这些原则不是只写在提示词里，而是由代码强制执行：

- 工具定义集中在 [`ToolCatalog.cs`](../LangCode/Agent/ToolCatalog.cs)，每个工具都带「分派路径 / 是否破坏性 / 审批类别」。
- 计划模式会同时**过滤工具 schema**，并在运行时**拦截破坏性动作**（见 [`AgentRunner.Dispatch.cs`](../LangCode/Agent/AgentRunner.Dispatch.cs)）。
- 文件写入前会检查编辑器里是否有未保存改动，避免覆盖用户正在编辑的缓冲区。

---

## 2. Agent 循环：模型 → 动作 → 工具 → 回灌

核心循环在 [`AgentRunner.RunAsync()`](../LangCode/Agent/AgentRunner.cs)。一轮用户请求大致这样跑：

```text
构建系统提示词（工具清单 + 项目规则 + 工作区环境 + 用户自定义指令）
        │
        ▼
┌────────────────────────────────────────────────────────┐
│ 1. 按需压缩历史（接近上下文上限时生成摘要）            │
│ 2. 发起一次模型请求（流式正文 / 推理过程）             │
│ 3. 解析输出里的 action 代码块（或原生 tool_calls）     │
│ 4. 没有任何动作 → 本轮结束（或进入自动验证 / 自动修复）│
│ 5. 有动作 → 逐个执行工具，把结果回灌给模型 → 回到 2    │
└────────────────────────────────────────────────────────┘
```

### 2.1 系统提示词

[`AgentPrompts.BuildSystemPrompt()`](../LangCode/Agent/AgentPrompts.cs) 每轮任务只构建一次，内容包括：

- 当前是否处于规划模式、是否启用自动上下文、是否支持图片输入；
- 自动探测出的工作区环境（技术栈、包管理器、建议验证命令、本地开发地址）；
- **工具清单**（与 `ToolCatalog` 同源，避免「加了工具漏改提示词」）；
- 行为规则：先定位再精读、改动最小化、多个独立动作同一轮发出、不要凭猜测编造文件内容等；
- 用户的附加要求；
- 项目规则文件（优先级最高）。

### 2.2 一次模型输出可以包含多个动作

模型可以在同一轮里输出多个 `action` 代码块。互不依赖的动作会**一起发出**，减少「发一个等一个」的往返次数。

当同一轮里出现多个 `new_task` 时，顶层任务会把它们**并行执行**（见下一节）。

### 2.3 原生 function calling

设置里打开 `nativeToolCalling` 后，请求会附带 `tools` schema：

- OpenAI 兼容接口：`{"type":"function","function":{...}}`
- Anthropic 原生接口：`{"name":...,"input_schema":{...}}`，并在最后一个工具上打 `cache_control` 缓存断点

无论走哪种格式，后端都会把 `tool_calls` 翻译成同样的 `action` 块，因此主循环只有一套解析与执行逻辑。

### 2.4 防「只说不做」与坏 JSON

本地小模型常见两种问题，代码里有对应的补救：

- **只宣布意图、不调用工具**：检测到「我先看看 / 让我先读取」这类短句且本轮没有工具调用时，最多注入 2 次催跑提示。
- **动作块 JSON 解析失败**：通常是字符串里有未转义的换行或三反引号；检测到围栏但解析不出工具时，会自动提示模型重发合法 JSON。

### 2.5 步数、取消、继续

- `MaxAgentSteps = 0` 表示不限制（默认）；设为正数则达到上限后暂停。
- 暂停后输入框上方出现「继续任务」，点它带着现有历史继续。
- 用户按 Esc / 取消后出现「恢复任务」。
- 真正抛异常时出现「重试」。
- 费用达到预算时，下一步开始前直接停止并提示。

---

## 3. 子任务（Sub-agent）

子任务是 LangCode 长任务编排的核心能力之一，入口是 `new_task` 工具。

### 3.1 它解决什么问题

父任务在推进时，可能遇到一块**相对独立、本身又需要多步完成**的工作，例如：

- 「先把登录页的表单校验实现出来，再回来接主流程」；
- 「单独调研这个第三方库的 API，给我结论」；
- 「把这一组测试补齐，不要动业务代码」。

这时父任务可以调用 `new_task`，把这块工作拆出去独立跑一轮；子任务完成后，把自然语言结论带回父任务，父任务继续往下做。

### 3.2 调用方式

```json
{
  "tool": "new_task",
  "task": "实现登录页的表单校验",
  "context": "相关文件 src/Login.tsx；校验规则见 src/validators.ts"
}
```

| 参数      | 必填 | 说明                                   |
| --------- | ---- | -------------------------------------- |
| `task`    | 是   | 子任务要做什么                         |
| `context` | 否   | 补充上下文（相关文件、约束、已知结论） |

### 3.3 生命周期

实现位于 [`RunSubTaskAsync()`](../LangCode/Agent/AgentRunner.Orchestration.cs)：

```text
父任务
  │  new_task(task, context)
  ▼
界面显示「子任务」卡（状态：进行中）
  │
  ▼
创建子 AgentRunner（SubTaskDepth + 1）
  │  历史只有一条 user 消息：子任务说明 + 补充上下文
  │  共用同一个后端、工作区、设置、会话 id
  ▼
子任务自己的「模型 → 工具 → 回灌」循环
  │  子任务内部产生的工具卡片（读文件 / 改文件 / 差异卡等）照常显示
  │  卡片在界面上缩进一格，表示属于这个子任务
  ▼
子任务给出「不带动作」的最终答复
  │  这段文本就是结论（会去掉 action 块）
  ▼
子任务卡更新为「已完成」，并附上结论
  │
  ▼
结论作为 new_task 的工具结果回灌给父任务
```

### 3.4 关键限制与行为

| 行为                        | 说明                                                                                     |
| --------------------------- | ---------------------------------------------------------------------------------------- |
| **最多一层**                | `SubTaskDepth >= 1` 时再调用 `new_task` 会被拒绝，避免「谁在等谁」不可读和空转           |
| **结论只取终局答复**        | 只有「不带动作」的那一轮文本会作为结论；中间带工具的轮次只用于界面展示                   |
| **没有结论也有兜底**        | 子任务结束但没给出文字时，返回「（子任务结束了，但没有给出文字结论）」                   |
| **不重复流式输出**          | 并行子任务会关闭流式转发，避免两个模型的分片混进同一条消息变成乱码                       |
| **审批仍然有效**            | 子任务里的写文件同样走审批；并行子任务同时请求审批时，界面用信号量串行化，一次只显示一张 |
| **不做自动验证 / 自动修复** | 自动验证与自动修复只在顶层任务执行，子任务内不会重复跑                                   |
| **继承会话归集**            | 子任务 Runner 继承 `ConversationId`，用量与审计仍归到同一会话                            |

### 3.5 并行子任务

如果父任务在**同一轮**里输出多个 `new_task`，顶层任务会：

1. 先全部派发（真正并行，不是伪并发）；
2. 再按模型给出的原顺序收集结果；
3. 单个子任务失败不影响其他子任务；
4. 界面提示「并行执行 N 个子任务…」。

适用场景：几块互不依赖的调研 / 实现工作，例如「同时查清 A、B、C 三个模块的现状」。

不适用场景：后一个子任务依赖前一个的结论；这种应该串行，或者让父任务先做前一步。

### 3.6 什么时候该用、什么时候不该用

**适合：**

- 工作相对独立，且本身需要多步（读多个文件、改多处、跑测试）；
- 并行调研几个互不依赖的问题；
- 希望把一大段探索过程隔离出去，只把结论带回主线。

**不适合：**

- 一句话就能完成的动作（直接调用对应工具更省）；
- 强依赖父任务当前上下文、拆出去反而要重复交代；
- 需要再往下拆多层（最多一层）。

### 3.7 源码位置

- 子任务执行与结论回传：[`AgentRunner.Orchestration.cs`](../LangCode/Agent/AgentRunner.Orchestration.cs)
- 并行派发与流式抑制：[`AgentRunner.cs`](../LangCode/Agent/AgentRunner.cs)
- 子任务卡与缩进：[`AgentToolWindowControl.Cards.cs`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Cards.cs)
- 子任务数据模型：[`PlanModels.cs`](../LangCode/Domain/Chat/PlanModels.cs)

---

## 4. 任务清单（Todo）

长任务的「看得见的进度」由 `update_todo_list` 承担。

### 4.1 清单格式

模型每次提交一整份清单，每行一项：

```text
[-] 正在做的一项
[ ] 还没开始的一项
[x] 已经做完的一项
```

| 标记  | 状态   | 界面符号 |
| ----- | ------ | -------- |
| `[ ]` | 待办   | `○`      |
| `[-]` | 进行中 | `◐`      |
| `[x]` | 已完成 | `✓`      |

解析器还兼容 `✓`、`→`、`◐` 等写法，但推荐使用上面的标准形式。

### 4.2 界面行为

- 每次调用都是**整体替换**，界面永远只显示「当前这一份清单」。
- 对话里出现一张「任务进度」卡。
- 窗口顶部同时出现常驻进度条，显示「已完成 / 总数」和百分比；顶部点开可看明细。
- 载入历史会话时，会从消息里恢复最后一份清单与进度条。
- 刷新任务清单会重绘同一张卡，不会每更新一次就多一张。

### 4.3 使用建议

系统提示词要求模型：

- 任务预计超过 3 步时，**先**列计划；
- 只在「进入下一阶段 / 计划有变 / 全部做完」时整体更新一次；
- 不要每完成一项就重写一遍清单（每次调用都是一整轮往返，费时费钱）。

### 4.4 源码位置

- 工具处理：[`UpdateTodoList()`](../LangCode/Agent/AgentRunner.Orchestration.cs)
- 解析、统计、渲染：[`TodoListParser`](../LangCode/Domain/Chat/PlanModels.cs)
- 界面进度条与卡片：[`AgentToolWindowControl.Cards.cs`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Cards.cs)

---

## 5. 主动提问（Ask Follow-up）

当需求不明确、或者存在多个合理方案时，模型可以调用 `ask_followup_question` 停下来问用户。

### 5.1 调用方式

```json
{
  "tool": "ask_followup_question",
  "question": "登录校验失败时，希望提示在表单上方还是字段下方？",
  "options": "表单上方|字段下方|两者都提示"
}
```

| 参数       | 必填 | 说明           |
| ---------- | ---- | -------------- | ------------------------------------------------- |
| `question` | 是   | 要问用户的问题 |
| `options`  | 否   | 备选答案，用 ` | ` / 换行 / 分号分隔；也可以不提供，让用户自由输入 |

### 5.2 交互

- 界面出现「等待你的回答」提问卡。
- 点备选答案即回答；也可以在输入框里打字回车。
- 不想回答时点「跳过回答」，模型会按最合理的默认方案继续，并说明假设。
- 提问是阻塞的：拿到回答或跳过信号前，Agent 不会继续。
- 并行子任务可能同时提问，界面会串行化，一次只让一个问题处于「等待回答」状态。
- 取消任务时，未回答的问题按「跳过 / 已取消」收尾，避免 Agent 永久等待。

### 5.3 源码位置

- 工具处理：[`AskFollowupAsync()`](../LangCode/Agent/AgentRunner.Orchestration.cs)
- 提问卡与回答：[`AskUserAsync()` / `AnswerQuestion()`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Cards.cs)
- 选项解析：[`TodoListParser.SplitOptions()`](../LangCode/Domain/Chat/PlanModels.cs)

---

## 6. 计划模式（只读）

输入框下方有「计划 / 执行」两个互斥芯片，对应设置里的 `PlanMode`。

### 6.1 打开后会怎样

- 系统提示词明确告知模型：当前只能调研，不能改文件、不能跑命令、不能调用 MCP / 打开网页。
- **只向模型暴露非破坏性工具**：schema 和提示词里的工具清单都变短，既省 token，也减少误操作。
- 即使模型硬发破坏性动作，运行时也会拦截并返回原因，让模型改用只读工具。
- 模型被要求用 `update_todo_list` 给出分步计划，并说明关键假设、风险与取舍。

### 6.2 正确的使用方式

1. 点「计划」，描述目标；
2. 看模型调研后的计划与任务清单；
3. 确认后点「执行」，再让它按计划动手。

> 计划模式不是「安全沙箱」的替代品。执行阶段仍然有审批、命令安全策略、检查点等保护。

### 6.3 源码位置

- 模式开关：[`SwitchMode()`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Popups.cs)
- 只读工具过滤：[`ToolCatalog.Offered()`](../LangCode/Agent/ToolCatalog.cs)
- 运行时拦截：[`ExecuteActionAsync()`](../LangCode/Agent/AgentRunner.Dispatch.cs)
- 提示词约束：[`AgentPrompts.cs`](../LangCode/Agent/AgentPrompts.cs)

---

## 7. 自动化闭环：改完还能跑通

### 7.1 自动探测工作区

[`ToolchainDetector`](../LangCode/Workspace/ToolchainDetector.cs) 会从根目录特征文件推断技术栈、包管理器、验证命令和本地开发地址：

| 技术栈        | 识别依据                                                | 建议验证命令                                                               |
| ------------- | ------------------------------------------------------- | -------------------------------------------------------------------------- |
| .NET          | `*.sln` / `*.csproj` 等                                 | `dotnet build`                                                             |
| Node.js       | `package.json`                                          | `npm test` > `npm run build` > `lint` > `typecheck`，或 `npx tsc --noEmit` |
| Go            | `go.mod`                                                | `go build ./...`                                                           |
| Rust          | `Cargo.toml`                                            | `cargo check`                                                              |
| Java (Maven)  | `pom.xml`                                               | `mvn -q -DskipTests compile`                                               |
| Java (Gradle) | `build.gradle*`                                         | `gradlew build -x test` / `gradle build -x test`                           |
| Python        | `pyproject.toml` / `requirements.txt` / `pytest.ini` 等 | `python -m pytest -q` 或 `python -m compileall -q .`                       |

同时会尝试识别本地开发地址：

- ASP.NET：从 `launchSettings.json` 的 `applicationUrl` 读取；
- Vite：`http://localhost:5173`；
- Next.js / React：`http://localhost:3000`；
- Angular：`http://localhost:4200`；
- Django：`http://localhost:8000`；
- 也会从 `dev` / `start` 脚本里的 `--port` / `PORT=` 提取端口。

探测结果会写进系统提示词的「工作区环境」，也会作为 `browser_action` 的默认地址。

### 7.2 自动验证

条件：

- 本轮真的落盘过文件（`_editApplied`）；
- 开启了 `AutoVerify`；
- 本轮还没验证过；
- 有验证命令（用户填的优先，否则用自动探测）。

行为：

- 只跑一次，避免改一处验一次；
- 结果回灌给模型；
- 通过则让模型收尾，失败则要求定位根因并最小改动。

### 7.3 自动修复

条件：

- 开启了 `AutoFixProblems`；
- 本轮改过文件；
- 自动修复轮次未超过 `AutoFixMaxRounds`（默认 2）；
- 能从 Visual Studio「错误列表」读到编译错误（受 `AutoReadProblems` 控制）。

行为：

- 把错误列表回灌给模型，要求只改相关文件；
- 达到轮次上限就停止，避免反复修不好把任务拖长；
- 子任务内不执行自动修复。

### 7.4 长任务完成通知

任务结束且满足以下任一条件时，会发桌面通知（可在设置里关闭）：

- 本轮改过文件；
- 撞到步数上限、可继续；
- 出错、可重试；
- 产生了多张卡片（说明任务较长）。

### 7.5 源码位置

- 自动验证 / 自动修复：[`AgentRunner.Automation.cs`](../LangCode/Agent/AgentRunner.Automation.cs)
- 工具链探测：[`ToolchainDetector.cs`](../LangCode/Workspace/ToolchainDetector.cs)
- 每轮收尾与通知：[`AgentToolWindowControl.Turns.cs`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Turns.cs)

---

## 8. 上下文与成本控制

长任务最容易烧 token。LangCode 在多个层面做了控制。

### 8.1 先定位再精读

- `semantic_search`：**混合检索**——正文、符号名、路径三路分别用 BM25 打分后加权，中文按二元组切分；按功能 / 概念找片段时比纯关键词更稳；
- `search_text`：正则搜索；
- `list_symbols` / `find_references` / 类型层次 / 查找实现：先缩小范围；
- `read_file` 支持 `start_line` / `end_line`，只读需要的片段。

### 8.2 工具结果预算

- 单条工具结果回灌前会截断到上限（默认 12000 字符，中段省略）；
- 命令 / 验证 / 诊断输出另有更小的上限；
- 只读工具重复调用且结果完全相同时，回灌一句「内容未变化，已省略」；
- 文件内容按「最后写入时间 + 长度」缓存：同一文件在未改动前重复读取 / 搜索不再重复读盘；
- 读取结果按「文件版本 + 行区间」再缓存一层：反复读同一段不重新切行 / 拼行号；
- 同一轮里多个只读工具（读文件 / 搜索 / 符号 / 混合检索）会**并行执行**（最多 4 个），写操作与需要审批的动作仍然串行；
- 回灌历史前会折叠动作块里的超长参数（例如 `write_file` 的整段 content）；
- 界面渲染仍使用原始文本，不受折叠影响。

### 8.3 可选向量重排

默认关闭，设置页「能力 → 向量检索增强」可选：

| 提供商              | 说明                                                                                          |
| ------------------- | --------------------------------------------------------------------------------------------- |
| `Ollama`            | 默认端点 `http://localhost:11434`，使用 `/api/embed`（老版本自动退回 `/api/embeddings`）      |
| `OpenAI Compatible` | OpenAI / LM Studio / vLLM / TEI 等 `/embeddings` 兼容端点                                     |
| `Local ONNX`        | 反射加载本机 `Microsoft.ML.OnnxRuntime`，需配置 BERT 风格模型与 `vocab.txt`；不可用时自动回退 |

流程：BM25 先召回候选（约 `limit × 4`）→ 查询与候选做 embedding → 余弦相似度与 BM25 归一化分按权重融合 → 取前 N。首次启用时还会在后台分批为最多 600 个片段建立向量，后续查询可做**向量召回**：BM25 没命中但语义相近的片段也能进入结果。任何一步失败都只影响本次重排，结果仍返回 BM25 命中，并在结果头部标注回退原因；连续失败会冷却 2 分钟。

### 8.4 总结式上下文压缩

- 接近上下文窗口阈值（默认 60%，可设置）时触发；
- 较早的中间段交给模型压成结构化摘要，而不是直接丢弃；
- 采用「只追加、不改写」策略：最早一批轮次和已生成的摘要块永不改写，保证前后请求的前缀逐字节一致，**提示缓存才能命中**；
- 也可以手动点「压缩上下文」。

### 8.5 提示缓存

设置里的 `PromptCaching` 会为稳定前缀标记缓存：

- Anthropic：`cache_control: ephemeral`；
- OpenAI 兼容：隐式缓存；
- 用量统计会记录缓存命中的输入 token，并按折扣价估算费用。

### 8.6 用量、预算与审计

- `usage.jsonl` 按会话记录每次模型调用的输入 / 输出 / 缓存 token 与费用；
- 设置费用预算后，每一步开始前检查，达到预算硬停；
- 开启审计日志后，每次工具调用（名字、参数摘要、结果摘要）追加到 `audit.log`，单条字段截断，文件超过 2MB 轮转。

### 8.7 源码位置

- 主循环里的压缩 / 去重 / 并行只读 / 折叠：[`AgentRunner.cs`](../LangCode/Agent/AgentRunner.cs)
- 混合检索 / 持久化 / 增量更新：[`SemanticIndex.cs`](../LangCode/Workspace/SemanticIndex.cs)
- 向量提供商（Ollama / OpenAI 兼容 / 本地 ONNX）：[`Embeddings.cs`](../LangCode/Workspace/Embeddings.cs)
- 仓库地图：[`RepoMap.cs`](../LangCode/Workspace/RepoMap.cs)
- 文件内容 / 行区间缓存、大文件大纲：[`FileSystemWorkspace.cs`](../LangCode/Workspace/FileSystemWorkspace.cs)
- 权限策略 / 会话授权 / 工作区策略：[`PermissionPolicy.cs`](../LangCode/Agent/PermissionPolicy.cs)、[`SessionGrantStore.cs`](../LangCode/Agent/SessionGrantStore.cs)、[`WorkspacePolicy.cs`](../LangCode/Domain/Settings/WorkspacePolicy.cs)
- 敏感文件与网络守卫：[`SecurityGuards.cs`](../LangCode/Workspace/SecurityGuards.cs)
- 工具结果裁剪：[`ToolOutputTrimmer`](../LangCode/Domain/Chat/ToolOutputTrimmer.cs)
- 用量与预算：[`UsageStore.cs`](../LangCode/Persistence/UsageStore.cs)
- 审计日志：[`AuditLog.cs`](../LangCode/Persistence/AuditLog.cs)

---

## 9. 安全、审批与可回退

### 9.1 按类别自动批准

[`ApprovalCatalog`](../LangCode/Domain/Catalog/ApprovalCatalog.cs) 把操作分为七类：

| 类别           | 默认     | 覆盖的操作                                           |
| -------------- | -------- | ---------------------------------------------------- |
| 读取文件       | 自动批准 | 读文件 / 列目录 / 搜索 / 混合检索 / 符号查询         |
| 编辑已有文件   | 需要确认 | 局部替换 / 结构化编辑 / 覆盖已有文件 / 补丁 / 重命名 |
| 新增文件       | 需要确认 | 新建文件                                             |
| 执行终端命令   | 需要确认 | `run_command` / `run_tests` / 调试器控制             |
| 浏览器操作     | 需要确认 | `open_url` / `browser_action`                        |
| 浏览器脚本执行 | 需要确认 | `browser_action` 的 `evaluate` / `eval`（任意 JS）   |
| MCP 工具       | 需要确认 | `mcp_call`                                           |

输入框的 **Auto** 芯片可以直接切换这些类别；设置页也有同样的开关。

### 9.2 工作区策略与会话授权

完整 schema 见 [`policy.md`](policy.md)。三层优先级：

1. **工作区硬拒绝**：`.langcode/policy.json` 的 `deniedTools` / `deniedPaths` / `deniedCommandPrefixes`、网络守卫、`allowBrowserEvaluate=false`；
2. **会话临时授权**：审批卡上的「本次会话内始终允许此工具」；命令只放行那一条完整命令，新建 / 切换会话即清空；
3. **审批模式**：`strict` 全部确认、`balanced` 按类别开关、`trusted` 默认放行；敏感文件与硬拒绝不受模式影响。

敏感文件（`.env`、`*.pem`、`*.key`、`id_rsa`、`appsettings.*.json` 等）不会进入 `search_text` / 语义索引；读取与写入逐次确认，且**不接受**「本次会话始终允许」。

### 9.3 写文件的审批流程

1. 工具先「准备」改动（算出 diff），此时磁盘还没变；
2. 若该类别未自动批准，界面出现差异卡与统一审批区；
3. 用户点「允许」才真正落盘；点「拒绝」则文件不变，模型收到拒绝原因；
4. 自动批准时也会补一张「已应用」的编辑卡，保证对话里能看到「AI 动过哪个文件」。

### 9.4 未保存文件保护

如果目标文件在 Visual Studio 编辑器里还有未保存改动，直接写盘会被拒绝，并提示先按 `Ctrl+S`。这样不会覆盖用户正在编辑的缓冲区。

### 9.5 命令安全策略

[`CommandPolicy`](../LangCode/Workspace/CommandPolicy.cs) 会检查：

- 用户自定义黑名单（命中即拒绝，优先级最高）；
- 用户信任前缀（命中则跳过内置危险检查，黑名单仍生效）；
- 高风险命令名（`format`、`diskpart`、`shutdown`、`bcdedit` 等）；
- 危险参数组合（`rm -rf /`、`del /f /s /q c:\`、`reg delete hklm`、`dd` 写块设备、fork 炸弹等）；
- PowerShell 混淆执行（`powershell -EncodedCommand` / `-enc`）与 `curl | sh` 这类远程脚本管道。

命令解释器可在设置页或工作区策略里选择 `cmd` / `PowerShell` / `bash`。危险命令即使开了自动批准也不会执行。

### 9.6 SSRF 与私网守卫

[`NetworkGuard`](../LangCode/Workspace/SecurityGuards.cs) 会拦截 `open_url` / `browser_action(navigate)` 的目标：

- 永远拦截云元数据 / 链路本地地址（如 `169.254.169.254`）；
- 私网段（`10/8`、`172.16/12`、`192.168/16`、IPv6 ULA）默认拦截，可用策略 `blockPrivateNetwork=false` 放行；
- `localhost` 默认放行（前端自测需要），可用 `allowLocalhost=false` 关闭；
- 相对路径打开工作区文件时，敏感文件仍需策略放行。

### 9.7 检查点与撤销

**检查点：**

- 每轮对话开始、Agent 动手之前，用影子 git 仓库打一个快照；
- 影子仓库在 `%APPDATA%\LangCode\vsext\checkpoints\<工作区哈希>`；
- 排除 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`dist`、`build` 等目录，并读取工作区 `.gitignore`；
- 每条用户消息上带「还原到此处」，把被跟踪文件还原到那条消息发出之前（未跟踪的新建文件保留原样）。

**撤销：**

- 「撤销上一步」：按相反顺序回滚最近一次落盘的改动；
- 「撤销本轮」：回滚本轮所有落盘改动，新建文件删除；
- 这两者走内存中的改动记录，不依赖 git；检查点则用于跨轮次 / 重开 VS 后的还原。

### 9.8 工作区边界

文件类工具都限制在工作区根目录内，路径统一使用相对路径与 `/` 分隔；写入工作区之外的路径会被拒绝。

---

## 10. 扩展与输入方式

### 10.1 MCP

- 在「设置 → MCP」里添加服务器（命令、参数、环境变量、启用状态）；
- 连接成功后，模型通过 `mcp_call` 调用服务器上的工具；
- 支持 MCP 市场条目；
- 支持从常见 MCP JSON 配置导入，并按服务器名合并（同名覆盖），见 [`McpConfig.cs`](../LangCode/Mcp/McpConfig.cs)；
- 默认不自动连接 MCP：`npx ...` 可能触发网络下载 / 启动第三方进程，需要用户显式开启；
- 默认工具调用需要确认（可关闭）。

### 10.2 多模态图片

- 支持粘贴剪贴板图片、拖入图片文件；
- 以 `image_url` data URL 发给视觉模型；
- 默认每条消息最多 4 张、单张 5MB；剪贴板大图会逐步缩小后再编码；
- 关闭 `Multimodal` 后不再接受图片附件。

### 10.3 `@` 上下文与自动上下文

`@` 弹层提供：

- 文件 / 文件夹浏览与搜索；
- 问题（Visual Studio 错误列表）；
- 终端（剪贴板里的终端输出）；
- 网页正文（剪贴板里的 URL，抓取正文）；
- Git Commits / Git 变更；
- 当前文件、当前选中代码、调试状态。

用户主动发送消息时，若开启 `AutoContext`，还会自动附带：

- 当前打开的文件与光标位置；
- 编辑器里选中的代码；
- 编译诊断（若开启 `AutoReadProblems`）。

### 10.4 斜杠命令

输入 `/` 唤起命令列表，命令定义见 [`SlashCommands.cs`](../LangCode/Domain/Catalog/SlashCommands.cs)：

| 命令       | 作用                                 |
| ---------- | ------------------------------------ |
| `/plan`    | 只调研并给出分步计划，不改文件       |
| `/review`  | 审查未提交的改动，按严重程度列出问题 |
| `/commit`  | 按 Conventional Commits 生成提交信息 |
| `/test`    | 为最近改动的代码补单元测试           |
| `/fix`     | 先说清根因，再给最小改动             |
| `/explain` | 解释一段代码的作用与陷阱             |
| `/clear`   | 清空当前会话（历史仍保留）           |

### 10.5 编辑器内联编辑

- 选中代码，右键 → **LangCode → 内联编辑 (Ctrl+K)**；
- 在光标处直接改，改完就地替换；
- 提示词要求只输出替换后的代码，尽量保持原风格与最小改动，见 [`InlinePrompts.cs`](../LangCode/Chat/InlinePrompts.cs)。

### 10.6 会话、历史与「本次变更」

- **历史抽屉**：按今天 / 昨天 / 更早分组；搜索框按「标题 + 摘要 + 模型」过滤；支持多选删除、清空全部。
- **载入历史**：恢复消息、任务清单、检查点 id、本次变更摘要；若上一轮是暂停 / 中断 / 出错，请求条会恢复「继续 / 恢复 / 重试」。
- **就地编辑重发**：用户消息可以在气泡里直接改，保存后会丢弃它之后的对话并用新内容重跑。
- **分叉**：从某条消息分出一个新会话，原会话完整保留，方便对比不同走向。
- **本次变更**：本轮真正落盘的文件按「新增 / 修改 / 删除」累计增删行数，随时可展开查看；点文件名可开 Visual Studio 差异对比。

### 10.7 排队、取消与恢复

- Agent 正在生成时，发送按钮仍然是「发送」：新消息会进入队列，当前这一轮结束后自动接着跑。
- 输入框上方的通栏「取消」或 `Esc` 会停止当前生成；未决的审批按「拒绝」收尾，未回答的提问按「跳过」收尾。
- 停止后可以「恢复任务」；达到步数上限可以「继续任务」；真正出错才显示「重试」。
- 关闭 VS 或意外中断后，会话里的暂停 / 中断标记会落盘，重新打开仍可继续。

### 10.8 项目规则与自定义指令

开启「项目规则文件」后，会按固定优先级读取并注入系统提示词：

- 根目录：`AGENTS.md`、`LANGCODE.md`、`CLAUDE.md`、`.langcoderules`、`.cursorrules`、`.windsurfrules`、`.clinerules`、`.github/copilot-instructions.md`、`CONVENTIONS.md`；
- 规则目录：`.langcode/rules/*.md`、`.cursor/rules/*.mdc|*.md`、`.clinerules/*.md|*.txt`、`.github/instructions/*.md`。

限制：

- 单个规则文件最多注入 8000 字符（超出截断并标注）；
- 全部规则合计最多 20000 字符；
- 项目规则在系统提示词里位于「通用规则」之后，优先级更高。

除此之外，「自定义指令」会作为用户的附加要求追加到系统提示词。规则加载逻辑见 [`ProjectRules.cs`](../LangCode/Workspace/ProjectRules.cs)。

---

## 11. 配置速查

| 设置项                     | 作用                                  | 默认     |
| -------------------------- | ------------------------------------- | -------- |
| `PlanMode`                 | 计划模式（只读）                      | 关       |
| `AutoContext`              | 自动附带当前文件 / 选中代码 / 诊断    | 开       |
| `AutoReadProblems`         | 读取「错误列表」诊断                  | 开       |
| `AutoVerify`               | 改完文件后自动跑验证命令              | 开       |
| `VerifyCommand`            | 指定验证命令；留空则自动探测          | 空       |
| `AutoFixProblems`          | 自动回灌编译错误修复                  | 开       |
| `AutoFixMaxRounds`         | 自动修复轮次上限                      | 2        |
| `Checkpoints`              | 每轮打检查点                          | 开       |
| `NativeToolCalling`        | 使用原生 function calling             | 关       |
| `PromptCaching`            | 提示缓存                              | 开       |
| `ContextWindow`            | 上下文窗口                            | 131072   |
| `MaxAgentSteps`            | 单轮最大模型→工具往返次数；0 = 不限制 | 0        |
| `AutoCompactThreshold`     | 自动压缩阈值                          | 60%      |
| `SemanticIndex`            | 本地混合索引                          | 开       |
| `RepoMap`                  | 系统提示注入仓库地图                  | 开       |
| `ApprovalMode`             | strict / balanced / trusted           | balanced |
| `CommandShell`             | cmd / powershell / bash               | cmd      |
| `AutoApproveBrowserScript` | browser_action evaluate 自动批准      | 关       |
| `EmbeddingProvider`        | 向量重排提供商（Off / Ollama / ...）  | Off      |
| `EmbeddingWeightPercent`   | 向量重排权重（0-100）                 | 35       |
| `StructuredEditing`        | 结构化编辑                            | 开       |
| `BrowserAutomation`        | 浏览器自动化                          | 开       |
| `WebSearchProvider`        | 联网搜索提供商                        | Bing     |
| `WebSearchMaxResults`      | 联网搜索默认条数                      | 5        |
| `DocSearchResults`         | 文档检索默认片段数                    | 8        |
| `Multimodal`               | 图片输入                              | 开       |
| `BudgetUsd`                | 费用预算；0 = 不限制                  | 0        |
| `AuditLogEnabled`          | 工具调用审计日志                      | 关       |
| `CommandDenyList`          | 用户命令黑名单                        | 空       |
| `CommandAllowPrefixes`     | 用户命令信任前缀                      | 空       |

完整字段与默认值见 [`AppSettings.cs`](../LangCode/Domain/Settings/AppSettings.cs)。

---

## 12. 已知边界

- 子任务最多一层，不能无限嵌套。
- 同一轮里的只读工具会并行（最多 4 个）；写操作、命令与需要审批的动作仍串行。
- 计划模式、审批、命令策略是三层不同的保护，不能互相替代。
- 检查点依赖本机可调用的 `git`；不可用时检查点功能优雅失败，其他能力不受影响。
- 混合检索是本地索引（BM25 + 符号 / 路径字段 + 中文二元组 + 可选向量重排），关闭后 `semantic_search` 等工具不可用；C# 的类型层次 / 查找实现会优先走 Roslyn。
- 向量重排默认关闭；Ollama / OpenAI 兼容需要端点可访问，本地 ONNX 需要本机已有 OnnxRuntime 与配套模型 / 词表，失败时自动退回 BM25。
- `run_tests` 的 `filter` 目前只对 `dotnet test` 生效。
