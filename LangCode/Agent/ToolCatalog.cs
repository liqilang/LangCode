using LangCode.Domain.Catalog;
using System.Collections.Generic;
using System.Text;

namespace LangCode.Agent
{
    /// <summary>
    /// 工具的执行分派路径：决定 <see cref="AgentRunner"/> 用哪条通道执行一个动作
    /// </summary>
    public enum ToolDispatch
    {
        /// <summary>本地只读 / 写准备（结果带 <c>Pending</c> 时由通用 diff 审批处理）</summary>
        Local,

        /// <summary>长任务编排（待办 / 提问 / 子任务 / 跳转 / 重命名 / 多文件补丁）：自行处理审批</summary>
        Orchestration,

        /// <summary>有外部副作用（命令 / 测试 / 浏览器 / MCP / 调试器）：先问再做</summary>
        External,
    }

    /// <summary>
    /// 一个工具的全部元数据（单一来源）：<c>schema</c>、系统提示词、分派路径、审批类别、
    /// 是否破坏性。新增工具只需在这里补一条 —— 原生 schema 与系统提示词会同步生成，
    /// 避免「加了一个工具、漏改另一处」的经典漏改。
    /// </summary>
    public sealed class ToolDescriptor
    {
        public ToolDescriptor(
            string name,
            string description,
            string propertiesJson,
            string requiredJson,
            ToolDispatch dispatch,
            bool destructive,
            string approvalCategory,
            int promptIndex,
            string prompt)
        {
            Name = name;
            Description = description;
            Properties = propertiesJson;
            Required = requiredJson;
            Dispatch = dispatch;
            Destructive = destructive;
            ApprovalCategory = approvalCategory ?? string.Empty;
            PromptIndex = promptIndex;
            Prompt = prompt;
        }

        /// <summary>工具名（与模型约定的调用名一致）</summary>
        public string Name { get; }

        /// <summary>给模型看的一句话说明（进原生 schema 的 description）</summary>
        public string Description { get; }

        /// <summary>JSON Schema 的 properties 段</summary>
        public string Properties { get; }

        /// <summary>JSON Schema 的 required 段（数组字符串，如 <c>["path"]</c>）</summary>
        public string Required { get; }

        /// <summary>执行分派路径</summary>
        public ToolDispatch Dispatch { get; }

        /// <summary>会改动磁盘或有外部副作用（规划模式下被拦回）</summary>
        public bool Destructive { get; }

        /// <summary>归属的自动批准类别（<see cref="ApprovalCatalog"/> 常量；空 = 无需批准）</summary>
        public string ApprovalCategory { get; }

        /// <summary>在系统提示词里的编号（1 起；决定提示词里的展示顺序）</summary>
        public int PromptIndex { get; }

        /// <summary>系统提示词里的工具说明（多行；不含前导编号，编号由调用方生成）</summary>
        public string Prompt { get; }
    }

    /// <summary>
    /// 工具目录：把「本插件支持的全部工具」收敛到一份定义，同时渲染成
    /// <list type="bullet">
    /// <item>OpenAI 兼容：<c>{"type":"function","function":{...}}</c>（见 <see cref="BuildToolsJson"/>）</item>
    /// <item>Anthropic 原生：<c>{"name":...,"input_schema":{...}}</c>（见 <see cref="BuildAnthropicToolsJson"/>）</item>
    /// <item>系统提示词里的编号清单（见 <see cref="PromptOrdered"/>，由 <see cref="AgentPrompts"/> 使用）</item>
    /// </list>
    /// 三者共用同一份定义，避免「加了一个工具、只更新了一种格式」这类漏改。
    /// </summary>
    public static class ToolCatalog
    {
        /// <summary>
        /// 全部工具定义（schema 顺序）
        /// </summary>
        private static readonly ToolDescriptor[] Definitions =
        [
            new ToolDescriptor("list_files", "列出工作区文件（支持 * 与 ? 通配，路径相对工作区根）", "{\"glob\":{\"type\":\"string\",\"description\":\"通配模式，如 src/**/*.tsx\"}}", "[]", ToolDispatch.Local, false, ApprovalCatalog.Read, 1,
                "列目录 / 通配查找：{\"tool\":\"list_files\",\"glob\":\"src/**/*.tsx\"}"),

            new ToolDescriptor("read_file", "读取文件内容，可按起止行裁剪", "{\"path\":{\"type\":\"string\"},\"start_line\":{\"type\":\"integer\"},\"end_line\":{\"type\":\"integer\"}}", "[\"path\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 2,
                "读文件：{\"tool\":\"read_file\",\"path\":\"src/App.tsx\",\"start_line\":1,\"end_line\":120}"),

            new ToolDescriptor("search_text", "按正则搜索文件内容，返回 路径:行号: 内容", "{\"pattern\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"}}", "[\"pattern\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 3,
                "正则搜索：{\"tool\":\"search_text\",\"pattern\":\"useTheme\",\"glob\":\"*.tsx\"}"),

            new ToolDescriptor("write_file", "整文件写入（新建或整体重写）", "{\"path\":{\"type\":\"string\"},\"content\":{\"type\":\"string\"}}", "[\"path\",\"content\"]", ToolDispatch.Local, true, ApprovalCatalog.Create, 4,
                "整文件写入：{\"tool\":\"write_file\",\"path\":\"src/New.tsx\",\"content\":\"...\"}\n   · content 是 JSON 字符串：换行要转义为 \\n，三反引号要写成 \\u0060（否则会提前结束动作块）"),

            new ToolDescriptor("replace_in_file", "局部替换：old_text 必须在文件里唯一", "{\"path\":{\"type\":\"string\"},\"old_text\":{\"type\":\"string\"},\"new_text\":{\"type\":\"string\"},\"replace_all\":{\"type\":\"string\",\"description\":\"填 true 时替换全部匹配\"}}", "[\"path\",\"old_text\",\"new_text\"]", ToolDispatch.Local, true, ApprovalCatalog.Edit, 5,
                "局部替换：{\"tool\":\"replace_in_file\",\"path\":\"src/App.tsx\",\"old_text\":\"旧片段\",\"new_text\":\"新片段\"}\n   · old_text 必须在文件里**唯一**（带上足够上下文）；确实要全部替换时才加 \"replace_all\":\"true\""),

            new ToolDescriptor("apply_patch", "多文件补丁：一次改多处 / 跨文件重构", "{\"patches\":{\"type\":\"string\",\"description\":\"每节以 *** Update File: 路径 开头（新建用 *** Add File:），旧代码段以七个小于号加 SEARCH 起，七个等号分隔，七个大于号加 REPLACE 收尾\"}}", "[\"patches\"]", ToolDispatch.Orchestration, true, ApprovalCatalog.Edit, 12,
                "多文件补丁（一次改多处 / 跨文件重构）：{\"tool\":\"apply_patch\",\"patches\":\"...补丁正文...\"}\n   补丁正文按节书写：\n     ① 文件头：*** Update File: 相对路径（新建文件写 *** Add File: 相对路径）\n     ② 旧代码段：以「七个小于号 + 空格 + SEARCH」开头，到分隔行为止\n     ③ 分隔行：「七个等号」；之后是新代码；这一节以「七个大于号 + 空格 + REPLACE」收尾\n   · 旧代码段必须与文件内容完全一致、且在该文件里唯一；多节首尾相接即可\n   · 界面会为每一节分别出差异卡等你确认，所以小改动仍优先用 replace_in_file"),

            new ToolDescriptor("run_command", "在工作区里执行 shell 命令", "{\"command\":{\"type\":\"string\"},\"working_directory\":{\"type\":\"string\",\"description\":\"相对工作区的子目录，可省略\"}}", "[\"command\"]", ToolDispatch.External, true, ApprovalCatalog.Command, 6,
                "执行命令：{\"tool\":\"run_command\",\"command\":\"npm test\",\"working_directory\":\"前端子目录(可省略)\"}"),

            new ToolDescriptor("open_url", "用系统浏览器打开网页，可选抓取正文", "{\"url\":{\"type\":\"string\"},\"fetch\":{\"type\":\"string\",\"description\":\"填 true 时抓取正文给模型\"}}", "[\"url\"]", ToolDispatch.External, true, ApprovalCatalog.Browser, 7,
                "打开网页：{\"tool\":\"open_url\",\"url\":\"https://example.com/docs\",\"fetch\":\"true\"}"),

            new ToolDescriptor("mcp_call", "调用已启用 MCP 服务器上的工具", "{\"server\":{\"type\":\"string\"},\"tool\":{\"type\":\"string\"},\"arguments\":{\"type\":\"string\",\"description\":\"JSON 字符串\"}}", "[]", ToolDispatch.External, true, ApprovalCatalog.Mcp, 8,
                "调用 MCP 工具：{\"tool\":\"mcp_call\",\"server\":\"filesystem\",\"tool\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"/tmp/a.txt\\\"}\"}\n   · 省略 tool 字段则列出该服务器的全部工具；省略 server 则用第一个已启用的服务器"),

            new ToolDescriptor("update_todo_list", "维护任务清单（长任务用）", "{\"todos\":{\"type\":\"string\",\"description\":\"每行一项，以 [ ] / [-] / [x] 开头\"}}", "[\"todos\"]", ToolDispatch.Orchestration, false, ApprovalCatalog.Unknown, 9,
                "维护任务清单（长任务用）：{\"tool\":\"update_todo_list\",\"todos\":\"[-] 正在做的一项\\n[ ] 还没开始的一项\\n[x] 已经做完的一项\"}\n   · 用换行分隔；每项以 [ ] / [-] / [x] 开头（待办 / 进行中 / 已完成）"),

            new ToolDescriptor("ask_followup_question", "向用户提问并等待回答", "{\"question\":{\"type\":\"string\"},\"options\":{\"type\":\"string\",\"description\":\"备选答案，用 | 分隔，可省略\"}}", "[\"question\"]", ToolDispatch.Orchestration, false, ApprovalCatalog.Unknown, 10,
                "向用户提问：{\"tool\":\"ask_followup_question\",\"question\":\"要选哪个方案？\",\"options\":\"方案A|方案B|方案C\"}\n   · options 用 | 分隔备选答案（可省略，用户也可以自由输入）；收到回答前会一直等待"),

            new ToolDescriptor("new_task", "把一个相对独立的活拆成子任务独立完成", "{\"task\":{\"type\":\"string\"},\"context\":{\"type\":\"string\"}}", "[\"task\"]", ToolDispatch.Orchestration, false, ApprovalCatalog.Unknown, 11,
                "拆分子任务：{\"tool\":\"new_task\",\"task\":\"实现登录页的表单校验\",\"context\":\"相关文件 src/Login.tsx\"}\n   · 子任务会带着你给的 task/context 独立跑一轮，做完把结论带回给你\n   · task/context 是 JSON 字符串：换行要转义为 \\n，三反引号要写成 \\u0060；不要把 Markdown 代码块原样贴进去"),

            new ToolDescriptor("list_symbols", "列出文件里的类 / 方法 / 函数及行号（C# 走 Roslyn，含签名）", "{\"path\":{\"type\":\"string\"}}", "[\"path\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 13,
                "列出文件符号：{\"tool\":\"list_symbols\",\"path\":\"src/App.tsx\"}"),

            new ToolDescriptor("find_references", "查找符号引用（Roslyn 标识符级，跳过字符串与注释；返回 路径:行号: 内容）", "{\"symbol\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"symbol\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 14,
                "查找符号引用：{\"tool\":\"find_references\",\"symbol\":\"useTheme\",\"glob\":\"*.tsx\"}\n   · 比整文件读便宜得多：先看结构 / 找调用点，再决定读哪一段"),

            new ToolDescriptor("goto_definition", "在编辑器里打开文件并请求「转到定义」（借用 Visual Studio 自带能力）", "{\"path\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"}}", "[\"path\"]", ToolDispatch.Orchestration, false, ApprovalCatalog.Read, 15,
                "转到定义：{\"tool\":\"goto_definition\",\"path\":\"src/App.tsx\",\"line\":42}\n   · 会在编辑器里借用 Visual Studio 自带的「转到定义」跳转，供用户肉眼确认"),

            new ToolDescriptor("semantic_search", "混合检索：正文 + 符号名 + 路径三路加权，按功能/概念找相关代码片段（不知道目标在哪时优先用它，再精读，省 token）", "{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"query\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 16,
                "混合检索（正文 / 符号名 / 路径三路加权，**不知道目标在哪时优先用它**）：{\"tool\":\"semantic_search\",\"query\":\"登录表单校验\",\"limit\":8}\n   · 比直接整文件读省得多：先用它定位到片段，再决定要不要 read_file 精读；同一轮里可以一次发多个检索（会并行执行）"),

            new ToolDescriptor("get_type_hierarchy", "查一个类型的继承链（基类 / 接口 / 已知派生类型）", "{\"type\":{\"type\":\"string\"}}", "[\"type\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 17,
                "类型继承链：{\"tool\":\"get_type_hierarchy\",\"type\":\"HttpClient\"}\n   · 返回基类 / 实现的接口与已知派生类型"),

            new ToolDescriptor("find_implementations", "查实现了某接口 / 继承了某基类的全部类型", "{\"type\":{\"type\":\"string\"}}", "[\"type\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 18,
                "查找实现：{\"tool\":\"find_implementations\",\"type\":\"IUserService\"}\n   · 返回所有实现该接口 / 继承该基类的类型"),

            new ToolDescriptor("edit_symbol", "结构化编辑：按符号名定位代码块再改写（比 replace_in_file 更抗格式漂移）", "{\"path\":{\"type\":\"string\"},\"symbol\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\",\"description\":\"replace / replace_body / insert_before / insert_after / delete\"},\"new_text\":{\"type\":\"string\"}}", "[\"path\",\"symbol\",\"new_text\"]", ToolDispatch.Local, true, ApprovalCatalog.Edit, 19,
                "结构化编辑（按符号锚点改写）：{\"tool\":\"edit_symbol\",\"path\":\"src/App.tsx\",\"symbol\":\"CalculateTotal\",\"mode\":\"replace_body\",\"new_text\":\"...\"}\n   · mode：replace（整块含签名）/ replace_body（只换块内）/ insert_before / insert_after / delete\n   · 改一个已存在的类型 / 方法时优先用它：不必原样抄写 old_text，不受缩进/换行差异影响"),

            new ToolDescriptor("browser_action", "浏览器自动化：导航 / 读正文 / 点击 / 填表 / 执行脚本 / 截图 / 读 console", "{\"action\":{\"type\":\"string\"},\"url\":{\"type\":\"string\"},\"selector\":{\"type\":\"string\"},\"value\":{\"type\":\"string\"},\"script\":{\"type\":\"string\"}}", "[\"action\"]", ToolDispatch.External, true, ApprovalCatalog.Browser, 20,
                "浏览器自动化（前端自测 / 按报错截图定位 UI 问题）：{\"tool\":\"browser_action\",\"action\":\"navigate\",\"url\":\"http://localhost:3000\"}\n   · action：navigate / read / html / click / fill / evaluate / screenshot / console / wait\n   · 配合 selector（CSS）操作元素；用 console 读页面报错，用 screenshot 生成截图存档\n   · 省略 url 时会自动使用助手探测到的本地开发地址（见「工作区环境」）"),

            new ToolDescriptor("run_tests", "运行工作区测试（自动识别 dotnet test / npm test / pytest / cargo test / go test；dotnet test 会返回结构化通过/失败统计与失败用例）", "{\"filter\":{\"type\":\"string\",\"description\":\"仅 dotnet test 生效：--filter 表达式\"}}", "[]", ToolDispatch.External, true, ApprovalCatalog.Command, 21,
                "运行测试：{\"tool\":\"run_tests\"} 或 {\"tool\":\"run_tests\",\"filter\":\"FullyQualifiedName~Login\"}\n   · 自动按技术栈选命令（dotnet test / npm test / pytest / cargo test / go test）；filter 仅对 dotnet test 生效\n   · dotnet test 会返回结构化的通过/失败统计与失败用例堆栈，可直接据此定位与修复"),

            new ToolDescriptor("rename_symbol", "跨文件重命名符号（Roslyn 语义级，只改真正绑定到该符号的标识符；逐文件审批后落盘）", "{\"symbol\":{\"type\":\"string\"},\"new_name\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"}}", "[\"symbol\",\"new_name\"]", ToolDispatch.Orchestration, true, ApprovalCatalog.Edit, 22,
                "跨文件重命名：{\"tool\":\"rename_symbol\",\"symbol\":\"OldName\",\"new_name\":\"NewName\",\"glob\":\"*.cs\"}\n   · Roslyn 语义级：只改真正绑定到该符号的标识符，自动跳过字符串、注释与同名局部变量；会逐文件列出差异卡等你确认\n   · 重命名一个已存在的类型 / 方法 / 字段时优先用它，不要手写多处 replace_in_file"),

            new ToolDescriptor("debug_inspect", "只读查看调试器：断点列表 / 调用栈 / 局部变量与参数 / 表达式求值 / 状态", "{\"action\":{\"type\":\"string\",\"description\":\"list_breakpoints / get_stack / get_locals / evaluate / status\"},\"expression\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"action\"]", ToolDispatch.External, false, ApprovalCatalog.Read, 23,
                "查看调试器（只读）：{\"tool\":\"debug_inspect\",\"action\":\"get_stack\"} 或 {\"action\":\"evaluate\",\"expression\":\"user.Name\"}\n   · action：list_breakpoints / get_stack / get_locals / evaluate / status；仅在 VS 里处于调试、且有活动栈帧时有效"),

            new ToolDescriptor("debug_control", "控制调试器：设/删断点、继续、单步、暂停、停止", "{\"action\":{\"type\":\"string\",\"description\":\"set_breakpoint / remove_breakpoint / clear_breakpoints / continue / step_over / step_into / step_out / pause / stop\"},\"path\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"},\"condition\":{\"type\":\"string\"}}", "[\"action\"]", ToolDispatch.External, true, ApprovalCatalog.Command, 24,
                "控制调试器：{\"tool\":\"debug_control\",\"action\":\"set_breakpoint\",\"path\":\"src/App.cs\",\"line\":42,\"condition\":\"i > 3\"}\n   · action：set_breakpoint / remove_breakpoint / clear_breakpoints / continue / step_over / step_into / step_out / pause / stop\n   · 用于「跑测试/复现后断点定位」的闭环：设断点 → 单步 → get_locals / evaluate 看清状态"),

            new ToolDescriptor("search_docs", "在工作区文档（README / docs / *.md / *.txt）中按关键词检索，返回相关段落与行号", "{\"query\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"query\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 25,
                "文档检索（在 README / docs / *.md 里找说明、配置、设计决策）：{\"tool\":\"search_docs\",\"query\":\"上下文压缩\",\"limit\":8}\n   · 只读说明性文档，按相关度返回段落与行号；需要完整内容再用 read_file\n   · 适合回答「这个项目怎么用 / 为什么这么设计 / 某个配置在哪」"),

            new ToolDescriptor("web_search", "联网搜索网页（默认 Bing，可配置 DuckDuckGo / Brave / Tavily / Serper），返回标题、URL 与摘要", "{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"query\"]", ToolDispatch.External, true, ApprovalCatalog.Browser, 26,
                "联网搜索（需要最新信息 / 外部文档 / 排错线索时用）：{\"tool\":\"web_search\",\"query\":\"Visual Studio 2022 VSIX net472 HttpClient TLS1.2\",\"limit\":5}\n   · 返回标题、URL 与摘要；需要正文时再用 open_url 打开链接\n   · 默认 Bing 免 Key，可在「设置 → 能力 → 网络搜索」切换提供商"),

            new ToolDescriptor("list_dir", "列出目录内容（树形，目录带 / 后缀；支持递归深度）", "{\"path\":{\"type\":\"string\"},\"depth\":{\"type\":\"integer\",\"description\":\"递归深度 1-4，默认 1\"}}", "[]", ToolDispatch.Local, false, ApprovalCatalog.Read, 27,
                "列目录（树形）：{\"tool\":\"list_dir\",\"path\":\"src\",\"depth\":2}\n   · path 省略 = 工作区根；depth 默认 1（只看一层），最大 4\n   · 目录带 / 后缀；会自动跳过 bin / obj / node_modules 等忽略目录，并尊重 .gitignore"),

            new ToolDescriptor("read_multiple_files", "一次读取多个文件（带行号），适合同时看几个相关文件", "{\"paths\":{\"type\":\"string\",\"description\":\"多个相对路径，用换行或分号分隔\"},\"max_chars_each\":{\"type\":\"integer\",\"description\":\"单文件字符上限，默认 8000，最大 20000\"}}", "[\"paths\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 28,
                "批量读文件：{\"tool\":\"read_multiple_files\",\"paths\":\"src/A.ts\\nsrc/B.ts\"}\n   · paths 用换行或分号分隔（最多 20 个）；每个文件默认最多 8000 字符\n   · 多个文件互不依赖时优先用它，比连续发多个 read_file 更省往返"),

            new ToolDescriptor("delete_file", "删除工作区里的一个文件（需审批，可撤销）", "{\"path\":{\"type\":\"string\"}}", "[\"path\"]", ToolDispatch.Local, true, ApprovalCatalog.Edit, 29,
                "删除文件：{\"tool\":\"delete_file\",\"path\":\"src/Old.tsx\"}\n   · 只删文件、不删目录；落盘前会出「删除文件」差异卡等你确认\n   · 删除前先确认没有引用（可先用 find_references）；删除后可用「撤销修改」还原"),

            new ToolDescriptor("move_file", "移动或重命名文件（需审批；不会自动改引用）", "{\"from\":{\"type\":\"string\"},\"to\":{\"type\":\"string\"}}", "[\"from\",\"to\"]", ToolDispatch.Local, true, ApprovalCatalog.Edit, 30,
                "移动 / 重命名文件：{\"tool\":\"move_file\",\"from\":\"src/Old.tsx\",\"to\":\"src/new/Old.tsx\"}\n   · 目标已存在时拒绝覆盖；目标目录会自动创建；落盘前会出确认卡\n   · 只移动文件本身，不会自动更新 import / 引用；C# 符号重命名请用 rename_symbol"),

            new ToolDescriptor("git_status", "查看 git 工作区状态（当前分支 + 未提交改动）", "{}", "[]", ToolDispatch.Local, false, ApprovalCatalog.Read, 31,
                "Git 状态：{\"tool\":\"git_status\"}\n   · 返回当前分支、已暂存 / 未暂存 / 未跟踪的文件；审查改动与生成提交信息前先看它"),

            new ToolDescriptor("git_diff", "查看 git 差异（工作区 / 暂存区，可按文件过滤）", "{\"staged\":{\"type\":\"string\",\"description\":\"填 true 查看已暂存差异（git diff --cached）\"},\"path\":{\"type\":\"string\"},\"stat\":{\"type\":\"string\",\"description\":\"填 true 只看统计\"}}", "[]", ToolDispatch.Local, false, ApprovalCatalog.Read, 32,
                "Git 差异：{\"tool\":\"git_diff\"} 或 {\"tool\":\"git_diff\",\"staged\":\"true\",\"path\":\"src/App.tsx\"}\n   · 默认看工作区未暂存差异；staged=true 看暂存区差异；stat=true 只看文件级统计\n   · 审查未提交改动、生成提交信息时优先用它，不要用 run_command 手写 git diff"),

            new ToolDescriptor("git_log", "查看最近提交（oneline）", "{\"limit\":{\"type\":\"integer\",\"description\":\"默认 20，最大 100\"},\"path\":{\"type\":\"string\"}}", "[]", ToolDispatch.Local, false, ApprovalCatalog.Read, 33,
                "提交历史：{\"tool\":\"git_log\",\"limit\":10} 或 {\"tool\":\"git_log\",\"path\":\"src/App.tsx\"}\n   · 用来看最近改了什么、提交信息风格与相关文件历史"),

            new ToolDescriptor("git_show", "查看某个提交的详情与改动", "{\"rev\":{\"type\":\"string\",\"description\":\"HEAD / HEAD~1 / 分支名 / 提交哈希\"},\"stat\":{\"type\":\"string\",\"description\":\"填 true 只看统计\"}}", "[\"rev\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 34,
                "查看提交：{\"tool\":\"git_show\",\"rev\":\"HEAD\"} 或 {\"tool\":\"git_show\",\"rev\":\"HEAD~1\",\"stat\":\"true\"}\n   · 默认输出完整补丁；stat=true 只看统计；rev 不允许以 - 开头"),

            new ToolDescriptor("git_stage", "暂存 git 改动（需审批）", "{\"paths\":{\"type\":\"string\",\"description\":\"多个相对路径，用换行或分号分隔\"},\"all\":{\"type\":\"string\",\"description\":\"填 true 暂存全部改动\"}}", "[]", ToolDispatch.External, true, ApprovalCatalog.Command, 35,
                "暂存改动：{\"tool\":\"git_stage\",\"paths\":\"src/App.tsx\\nsrc/theme.ts\"} 或 {\"tool\":\"git_stage\",\"all\":\"true\"}\n   · 敏感文件会被拒绝暂存；暂存后可用 git_diff(staged=true) 复核；确认无误后再 git_commit"),

            new ToolDescriptor("git_commit", "提交已暂存改动（需审批；不会 push）", "{\"message\":{\"type\":\"string\",\"description\":\"提交信息，遵循 Conventional Commits\"},\"add_all\":{\"type\":\"string\",\"description\":\"填 true 先暂存全部改动再提交\"}}", "[\"message\"]", ToolDispatch.External, true, ApprovalCatalog.Command, 36,
                "提交：{\"tool\":\"git_commit\",\"message\":\"feat: 登录页增加表单校验\"}\n   · 只提交已暂存内容；add_all=true 会先 git add -A；不会自动 push\n   · 提交前建议先 git_status + git_diff(staged=true) 复核，并在回答里说明提交哈希"),
        ];

        // 工具名 → 描述符（大小写不敏感；模型偶尔会改动大小写）
        private static readonly Dictionary<string, ToolDescriptor> ByName = BuildIndex();

        private static Dictionary<string, ToolDescriptor> BuildIndex()
        {
            var map = new Dictionary<string, ToolDescriptor>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var tool in Definitions)
            {
                map[tool.Name] = tool;
            }

            return map;
        }

        /// <summary>
        /// 全部工具（schema 顺序）
        /// </summary>
        public static IList<ToolDescriptor> All => Definitions;

        /// <summary>
        /// 按名字查一个工具；未知返回 <c>null</c>
        /// </summary>
        public static ToolDescriptor Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            return ByName.TryGetValue(name.Trim(), out var tool) ? tool : null;
        }

        /// <summary>
        /// 工具的执行分派路径；未知工具按「本地」处理（会走到通用分支并报未知工具）
        /// </summary>
        public static ToolDispatch DispatchOf(string name)
        {
            var tool = Find(name);
            return tool?.Dispatch ?? ToolDispatch.Local;
        }

        /// <summary>
        /// 是否是破坏性工具（会改磁盘或有外部副作用；规划模式据此拦截）
        /// </summary>
        public static bool IsDestructive(string name)
        {
            return Find(name)?.Destructive == true;
        }

        /// <summary>
        /// 工具归属的自动批准类别；未知返回空串
        /// </summary>
        public static string ApprovalCategoryOf(string name)
        {
            return Find(name)?.ApprovalCategory ?? string.Empty;
        }

        /// <summary>
        /// 某个工具在当前模式下是否要暴露给模型。
        /// </summary>
        /// <remarks>
        /// 规划模式只暴露只读（非破坏性）工具：既省掉一大段用不上的 schema / 提示词
        /// （工具清单是每一步都重复发送的**固定前缀**，越短越省），也避免模型在只查看阶段尝试写文件。
        /// </remarks>
        public static bool IsOffered(ToolDescriptor tool, bool planMode)
        {
            return tool != null && (!planMode || !tool.Destructive);
        }

        /// <summary>
        /// 当前模式下要暴露的工具集合（schema 顺序）。非规划模式即全量。
        /// </summary>
        public static IList<ToolDescriptor> Offered(bool planMode)
        {
            if (!planMode)
            {
                return Definitions;
            }

            var list = new List<ToolDescriptor>(Definitions.Length);
            foreach (var tool in Definitions)
            {
                if (!tool.Destructive)
                {
                    list.Add(tool);
                }
            }

            return list;
        }

        /// <summary>
        /// 按提示词编号排序的工具清单（供系统提示词生成编号列表），按模式过滤
        /// </summary>
        public static IEnumerable<ToolDescriptor> PromptOrdered(bool planMode = false)
        {
            var ordered = new List<ToolDescriptor>(Offered(planMode));
            ordered.Sort((a, b) => a.PromptIndex.CompareTo(b.PromptIndex));
            return ordered;
        }

        /// <summary>
        /// 构造 OpenAI 兼容的 <c>tools</c> 数组 JSON（按模式过滤）
        /// </summary>
        public static string BuildToolsJson(bool planMode = false)
        {
            var tools = Offered(planMode);
            var builder = new StringBuilder("[");
            for (var i = 0; i < tools.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var tool = tools[i];
                builder.Append("{\"type\":\"function\",\"function\":{\"name\":\"").Append(tool.Name)
                       .Append("\",\"description\":\"").Append(ToolCallTranslator.Escape(tool.Description))
                       .Append("\",\"parameters\":{\"type\":\"object\",\"properties\":")
                       .Append(tool.Properties)
                       .Append(",\"required\":").Append(tool.Required)
                       .Append("}}}");
            }

            return builder.Append(']').ToString();
        }

        /// <summary>
        /// 构造 Anthropic 原生 Messages API 的 <c>tools</c> 数组 JSON：
        /// <c>{"name":..,"description":..,"input_schema":{...}}</c>（按模式过滤）。
        /// 最后一个工具打一个 <c>cache_control</c> 断点：工具清单在一轮任务里每步都重复发送，
        /// 缓存命中后按折扣价计费（Anthropic 提示缓存的推荐做法）。
        /// </summary>
        public static string BuildAnthropicToolsJson(bool planMode = false)
        {
            var tools = Offered(planMode);
            var builder = new StringBuilder("[");
            for (var i = 0; i < tools.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var tool = tools[i];
                builder.Append("{\"name\":\"").Append(tool.Name)
                       .Append("\",\"description\":\"").Append(ToolCallTranslator.Escape(tool.Description))
                       .Append("\",\"input_schema\":{\"type\":\"object\",\"properties\":")
                       .Append(tool.Properties)
                       .Append(",\"required\":").Append(tool.Required)
                       .Append('}');

                if (i == tools.Count - 1)
                {
                    builder.Append(",\"cache_control\":{\"type\":\"ephemeral\"}");
                }

                builder.Append('}');
            }

            return builder.Append(']').ToString();
        }
    }
}
