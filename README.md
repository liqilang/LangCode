# LangCode

<div align="center">

<img src="Images/robot.svg" width="104" alt="LangCode 机器人" />

<p><strong>Visual Studio 里的 AI 编程助手</strong></p>

<p>
  <img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="License" />
  <img src="https://img.shields.io/badge/Visual%20Studio-2022%20(17.0%2B)-5C2D91.svg" alt="Visual Studio" />
  <img src="https://img.shields.io/badge/.NET%20Framework-4.7.2-512BD4.svg" alt=".NET Framework" />
</p>

</div>

## 简介

LangCode 是一款集成在 Visual Studio 中的 AI 编程助手。它不仅能与你聊天，还能读懂你的项目、直接修改代码、运行命令和测试。每次修改文件前，都会清晰展示变更内容，需要你确认才会执行。

## 核心功能

| 功能类别 | 能力 |
|---------|------|
| **对话交互** | 中文/英文描述需求，自动携带当前文件、选中代码和编译诊断 |
| **代码理解** | 关键词搜索、语义检索、文档检索、符号查找、引用查找、类型层次 |
| **代码修改** | 局部替换、整文件写入、按符号改写、多文件补丁、跨文件重命名 |
| **执行验证** | 自动识别 .NET/Node.js/Python/Rust/Go/Java，运行命令和测试 |
| **浏览器自动化** | 导航、点击、填表、执行脚本、截图、读取 console 报错 |
| **调试器联动** | 断点、调用栈、局部变量、表达式求值；继续/单步/暂停/停止 |
| **MCP 扩展** | 连接 MCP 服务器、浏览市场条目、导入现有配置 |
| **长任务编排** | 任务清单、子任务、主动提问、计划模式，过程可见可打断 |
| **检查点** | 改动前自动留快照，可还原到某条消息之前 |
| **多模型支持** | 国内、国外、本地模型均可使用，可命名保存多套配置 |
| **编辑器内联** | 解释、重构、测试、文档、修复选择；Ctrl+K 直接修改 |

## 安装

### 环境要求

- Windows 10/11
- Visual Studio 2022（17.0 及以上）
- .NET Framework 4.7.2

### 安装步骤

1. 下载安装包 [LangCode.vsix](https://gitee.com/li_qi_lang/lang-code/raw/master/dist/LangCode.vsix)
2. 关闭 Visual Studio
3. 双击 `.vsix` 文件，按向导完成安装
4. 重新打开 Visual Studio，在 **扩展 → 管理扩展 → 已安装** 中确认 LangCode 已启用

## 快速开始

1. 打开工具窗口：**工具 → 打开 LangCode**（或代码窗口右键 → LangCode）
2. 在设置中选择「API 提供商」，填入 API Key
3. 选择一个模型（下拉选择或直接输入模型名）
4. 在输入框描述需求，回车开始

## 常用命令

| 命令 | 作用 |
|------|------|
| `/plan` | 先出方案，不改代码 |
| `/review` | 审查未提交的改动 |
| `/test` | 为最近改动补测试 |
| `/fix` | 定位并修复问题 |
| `/explain` | 解释代码 |
| `/clear` | 清空当前会话 |

## 支持的 AI 模型

| 类别 | 提供商 |
|------|--------|
| 国内 | DeepSeek、Kimi、通义千问、智谱 GLM、MiniMax、SiliconFlow |
| 国外 | OpenAI、Anthropic、Google Gemini、xAI、Mistral、Groq、OpenRouter |
| 本地 | Ollama、LM Studio、LiteLLM、任意 OpenAI 兼容网关 |

## 项目结构

```
LangCode/
├── Agent/           # AI 核心：工具定义、对话循环、提示词、长任务编排
├── Chat/            # 各种 AI 服务商接口适配
├── Domain/          # 数据模型：提供商、模型、设置、审批、任务清单
├── Mcp/             # MCP 工具支持
├── Persistence/     # 本地存储：设置、历史会话、用量与审计
├── Roslyn/          # C# 代码分析
├── Ui/              # 界面：工具窗口、抽屉、主题、图标
├── VsIntegration/   # 与 Visual Studio 对接
├── Vsx/             # 扩展入口与菜单
└── Workspace/       # 工作区能力：文件、命令、语义检索、浏览器
```

## 安全与隐私

- **写文件审批**：每次修改显示差异卡，确认后才落盘
- **命令审批**：执行终端命令前默认询问
- **计划模式**：只暴露只读工具，拦截破坏性动作
- **检查点**：改动前自动留快照，可还原
- **工作区边界**：所有文件操作限制在项目目录内

数据存储在本地：`%APPDATA%\LangCode\vsext\`

## 许可证

本项目基于 [Apache License 2.0](LICENSE) 开源。