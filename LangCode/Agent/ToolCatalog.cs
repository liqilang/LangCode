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
                "整文件写入：{\"tool\":\"write_file\",\"path\":\"src/New.tsx\",\"content\":\"...\"}"),

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
                "拆分子任务：{\"tool\":\"new_task\",\"task\":\"实现登录页的表单校验\",\"context\":\"相关文件 src/Login.tsx\"}\n   · 子任务会带着你给的 task/context 独立跑一轮，做完把结论带回给你"),

            new ToolDescriptor("list_symbols", "列出文件里的类 / 方法 / 函数及行号（C# 走 Roslyn，含签名）", "{\"path\":{\"type\":\"string\"}}", "[\"path\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 13,
                "列出文件符号：{\"tool\":\"list_symbols\",\"path\":\"src/App.tsx\"}"),

            new ToolDescriptor("find_references", "查找符号引用（Roslyn 标识符级，跳过字符串与注释；返回 路径:行号: 内容）", "{\"symbol\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"symbol\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 14,
                "查找符号引用：{\"tool\":\"find_references\",\"symbol\":\"useTheme\",\"glob\":\"*.tsx\"}\n   · 比整文件读便宜得多：先看结构 / 找调用点，再决定读哪一段"),

            new ToolDescriptor("goto_definition", "在编辑器里打开文件并请求「转到定义」（借用 Visual Studio 自带能力）", "{\"path\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"}}", "[\"path\"]", ToolDispatch.Orchestration, false, ApprovalCatalog.Read, 15,
                "转到定义：{\"tool\":\"goto_definition\",\"path\":\"src/App.tsx\",\"line\":42}\n   · 会在编辑器里借用 Visual Studio 自带的「转到定义」跳转，供用户肉眼确认"),

            new ToolDescriptor("semantic_search", "语义检索：按功能/概念关键词找相关代码片段（不知道目标在哪时优先用它，再精读，省 token）", "{\"query\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"query\"]", ToolDispatch.Local, false, ApprovalCatalog.Read, 16,
                "语义检索（按功能 / 概念找相关代码片段，**不知道目标在哪时优先用它**）：{\"tool\":\"semantic_search\",\"query\":\"登录表单校验\",\"limit\":8}\n   · 比直接整文件读省得多：先用它定位到片段，再决定要不要 read_file 精读；同一轮里可以一次发多个检索"),

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

            new ToolDescriptor("rename_symbol", "跨文件重命名符号（Roslyn 语法级，自动跳过字符串/注释；逐文件审批后落盘）", "{\"symbol\":{\"type\":\"string\"},\"new_name\":{\"type\":\"string\"},\"glob\":{\"type\":\"string\"}}", "[\"symbol\",\"new_name\"]", ToolDispatch.Orchestration, true, ApprovalCatalog.Edit, 22,
                "跨文件重命名：{\"tool\":\"rename_symbol\",\"symbol\":\"OldName\",\"new_name\":\"NewName\",\"glob\":\"*.cs\"}\n   · Roslyn 语法级：自动跳过字符串与注释；会逐文件列出差异卡等你确认（比手动 replace_in_file 更可靠）\n   · 重命名一个已存在的类型 / 方法 / 字段时优先用它，不要手写多处 replace_in_file"),

            new ToolDescriptor("debug_inspect", "只读查看调试器：断点列表 / 调用栈 / 局部变量与参数 / 表达式求值 / 状态", "{\"action\":{\"type\":\"string\",\"description\":\"list_breakpoints / get_stack / get_locals / evaluate / status\"},\"expression\":{\"type\":\"string\"},\"limit\":{\"type\":\"integer\"}}", "[\"action\"]", ToolDispatch.External, false, ApprovalCatalog.Read, 23,
                "查看调试器（只读）：{\"tool\":\"debug_inspect\",\"action\":\"get_stack\"} 或 {\"action\":\"evaluate\",\"expression\":\"user.Name\"}\n   · action：list_breakpoints / get_stack / get_locals / evaluate / status；仅在 VS 里处于调试、且有活动栈帧时有效"),

            new ToolDescriptor("debug_control", "控制调试器：设/删断点、继续、单步、暂停、停止", "{\"action\":{\"type\":\"string\",\"description\":\"set_breakpoint / remove_breakpoint / clear_breakpoints / continue / step_over / step_into / step_out / pause / stop\"},\"path\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"},\"condition\":{\"type\":\"string\"}}", "[\"action\"]", ToolDispatch.External, true, ApprovalCatalog.Command, 24,
                "控制调试器：{\"tool\":\"debug_control\",\"action\":\"set_breakpoint\",\"path\":\"src/App.cs\",\"line\":42,\"condition\":\"i > 3\"}\n   · action：set_breakpoint / remove_breakpoint / clear_breakpoints / continue / step_over / step_into / step_out / pause / stop\n   · 用于「跑测试/复现后断点定位」的闭环：设断点 → 单步 → get_locals / evaluate 看清状态"),
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
