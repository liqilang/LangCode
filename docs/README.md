# LangCode 文档

这里放的是「功能怎么用、内部怎么跑」的说明。README 负责快速了解项目；本目录负责把 AI、子任务、长任务编排、工具与安全机制讲清楚。

## 文档索引

| 文档                               | 适合谁              | 内容                                                                                      |
| ---------------------------------- | ------------------- | ----------------------------------------------------------------------------------------- |
| [AI 与自动化能力总览](ai-agent.md) | 使用者 / 二次开发者 | Agent 循环、子任务、任务清单、主动提问、计划模式、自动验证/修复、上下文与成本、安全与回退 |
| [工具参考](tools.md)               | 使用者 / 二次开发者 | 全部 24 个工具的参数、审批类别、破坏性标记、调用协议与示例                                |
| [项目 README](../README.md)        | 第一次接触项目的人  | 安装、快速开始、界面截图、模型支持、项目结构                                              |

## 先认识几个词

| 名词                     | 含义                                                                           |
| ------------------------ | ------------------------------------------------------------------------------ |
| **Agent**                | 能「思考 → 调用工具 → 看结果 → 继续」的 AI 循环，而不是只回一段文字            |
| **动作块（action）**     | 模型输出的一段 JSON，告诉插件「要调用哪个工具、参数是什么」                    |
| **工具（tool）**         | Agent 可以执行的具体能力，例如读文件、搜索、改代码、跑命令、调用 MCP           |
| **子任务（subtask）**    | 父任务通过 `new_task` 拆出去、独立跑一轮的较小任务，完成后把文字结论带回父任务 |
| **任务清单（todo）**     | 长任务的进度列表，用 `update_todo_list` 维护，界面顶部常驻进度条               |
| **主动提问**             | 模型用 `ask_followup_question` 停下来问用户，拿到回答或「跳过」后继续          |
| **计划模式**             | 只读模式：只调研、出计划，不改文件、不跑命令                                   |
| **审批**                 | 写文件、跑命令、开浏览器、调 MCP 之前，由用户点「允许 / 拒绝」                 |
| **检查点（Checkpoint）** | 每轮对话开始前用影子 git 仓库打一个快照，之后可把文件还原到那一刻              |
| **MCP**                  | 一套「给 AI 用的工具协议」，接入后 Agent 可以调用外部服务器提供的能力          |

## 能力地图

```text
用户输入
   │
   ├─ @ 提及 / 图片 / 自动上下文 ──────────────┐
   │                                          │
   ▼                                          ▼
Agent 循环（AgentRunner）
   │  系统提示词 + 工具清单 + 历史 + 上下文
   │
   ├─ 长任务编排
   │    ├─ update_todo_list          任务清单 / 进度条
   │    ├─ ask_followup_question     主动提问 / 选项 / 跳过
   │    └─ new_task                  子任务（可并行，最多一层）
   │
   ├─ 代码理解
   │    ├─ list_files / read_file / search_text
   │    ├─ list_symbols / find_references
   │    └─ semantic_search / 类型层次 / 查找实现
   │
   ├─ 代码修改
   │    ├─ replace_in_file / write_file
   │    ├─ edit_symbol（按符号改写）
   │    ├─ apply_patch（多文件补丁）
   │    └─ rename_symbol（Roslyn 跨文件重命名）
   │
   ├─ 执行与验证
   │    ├─ run_command / run_tests
   │    ├─ 自动验证（改完自动跑验证命令）
   │    └─ 自动修复（读编译诊断，最多 N 轮）
   │
   └─ 外部能力
        ├─ open_url / browser_action
        ├─ mcp_call
        └─ debug_inspect / debug_control
```

## 相关源码入口

| 能力                                                  | 源码                                                                                            |
| ----------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| Agent 主循环                                          | [`AgentRunner.cs`](../LangCode/Agent/AgentRunner.cs)                                            |
| 子任务 / 任务清单 / 提问 / 补丁 / 重命名              | [`AgentRunner.Orchestration.cs`](../LangCode/Agent/AgentRunner.Orchestration.cs)                |
| 工具分派与本地工具执行                                | [`AgentRunner.Dispatch.cs`](../LangCode/Agent/AgentRunner.Dispatch.cs)                          |
| 有副作用的工具（命令 / 测试 / 浏览器 / MCP / 调试器） | [`AgentRunner.External.cs`](../LangCode/Agent/AgentRunner.External.cs)                          |
| 自动验证 / 自动修复                                   | [`AgentRunner.Automation.cs`](../LangCode/Agent/AgentRunner.Automation.cs)                      |
| 系统提示词                                            | [`AgentPrompts.cs`](../LangCode/Agent/AgentPrompts.cs)                                          |
| 工具单一来源定义                                      | [`ToolCatalog.cs`](../LangCode/Agent/ToolCatalog.cs)                                            |
| 任务清单 / 子任务 / 提问模型                          | [`PlanModels.cs`](../LangCode/Domain/Chat/PlanModels.cs)                                        |
| 审批类别                                              | [`ApprovalCatalog.cs`](../LangCode/Domain/Catalog/ApprovalCatalog.cs)                           |
| 检查点                                                | [`CheckpointStore.cs`](../LangCode/Workspace/CheckpointStore.cs)                                |
| 项目规则                                              | [`ProjectRules.cs`](../LangCode/Workspace/ProjectRules.cs)                                      |
| 工具链探测                                            | [`ToolchainDetector.cs`](../LangCode/Workspace/ToolchainDetector.cs)                            |
| 命令安全策略                                          | [`CommandPolicy.cs`](../LangCode/Workspace/CommandPolicy.cs)                                    |
| 子任务卡 / 任务卡 / 提问卡 UI                         | [`AgentToolWindowControl.Cards.cs`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Cards.cs) |
| 每轮调用与检查点 UI                                   | [`AgentToolWindowControl.Turns.cs`](../LangCode/Ui/ToolWindows/AgentToolWindowControl.Turns.cs) |
