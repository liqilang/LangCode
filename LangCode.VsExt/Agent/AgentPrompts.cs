using LangCode.VsExt.Domain.Settings;
using System.Text;

namespace LangCode.VsExt.Agent
{
    /// <summary>
    /// >Agent 的系统提示词（工具清单 + 调用协议 + 行为约束）
    /// </summary>
    public static class AgentPrompts
    {
        public static string BuildSystemPrompt(AppSettings settings, string projectRules = null, string environmentBrief = null)
        {
            var builder = new StringBuilder();

            builder.AppendLine("你是 LangCode，一个集成在 Visual Studio 里的编程助手，可以直接读写用户工作区的文件");
            builder.AppendLine();

            // 自动上下文：本轮消息前可能附带「当前文件 / 选中代码 / 编译诊断」，
            // 明说一句，模型才不会把那段当成用户的原话，也才知道可以直接利用
            if (settings != null && settings.AutoContext)
            {
                builder.AppendLine("【自动上下文】本轮用户消息之后可能附带一段系统自动采集的上下文（当前文件、编辑器选中代码、编译诊断）");
                builder.AppendLine("  它是环境信息而非用户的要求，请直接利用；其中的「当前文件」除非确有必要，否则不要重复整段读取");
                builder.AppendLine();
            }

            if (settings != null && settings.Multimodal)
            {
                builder.AppendLine("【图片输入】用户可能直接贴图（截图 / 设计稿 / 报错截图）若本轮带图，请结合图里的界面 / 文字 / 报错信息作答，");
                builder.AppendLine("  不要假设自己看不到图片；需要精确读代码时再用工具");
                builder.AppendLine();
            }

            // 工作区环境是助手自动探测出来的，直接告诉模型，省得它先花几轮去猜技术栈 / 验证方式
            if (!string.IsNullOrWhiteSpace(environmentBrief))
            {
                builder.AppendLine("【工作区环境（助手自动识别，无需用户配置）】" + environmentBrief);
                builder.AppendLine("  验证命令、自动修复、上下文采集、语义检索都由助手自动完成；若探测结果与实际不符，请以文件内容为准");
                builder.AppendLine();
            }

            if (settings != null && settings.PlanMode)
            {
                builder.AppendLine("【当前处于规划模式】");
                builder.AppendLine("- 禁止修改任何文件、禁止执行命令、禁止调用 MCP / 打开网页；你只能用 list_files / read_file / search_text 做调研");
                builder.AppendLine("- 先把现状看明白，再用 update_todo_list 给出分步计划，并说明关键假设、风险与取舍");
                builder.AppendLine("- 计划要具体到「改哪个文件的哪一部分」，但此刻不要动手；用户确认后会把模式切到执行");
                builder.AppendLine();
            }

            builder.AppendLine("可用工具（用 ```action 代码块发出；一个代码块里只放一个动作，需要多个就写多个代码块）：");
            // 工具清单与原生 schema 同源（ToolCatalog），不再手写 —— 新增工具只改一处
            foreach (var tool in ToolCatalog.PromptOrdered())
            {
                builder.AppendLine(tool.PromptIndex + ". " + tool.Prompt);
            }

            builder.AppendLine();
            builder.AppendLine("规则：");
            builder.AppendLine("- 任务预计超过 3 步时，**先**用 update_todo_list 列出计划；之后每完成一项就更新一次（把当前项标 [-]、已完成标 [x]）这是长任务不让用户迷失的唯一办法");
            builder.AppendLine("- 需求不明确、或存在多个合理方案时，用 ask_followup_question 问清楚再动手，不要自己替用户拍板");
            builder.AppendLine("- 某块工作相对独立、且子任务本身也要多步才能完成时，才用 new_task 拆出去；不要为了走流程而拆一个一句话就能做完的子任务");
            builder.AppendLine("- 需要文件内容时**必须先读**（read_file / search_text），不要凭猜测编造文件内容");
            builder.AppendLine("- 改动优先用 replace_in_file（局部、最小 diff）；只有新建文件或整体重写才用 write_file");
            builder.AppendLine("   · 若工具回来说「原文出现了 N 次」，说明 old_text 不够唯一：补上上下文重发，别改成 replace_all 硬来");
            builder.AppendLine("- 路径一律**相对于工作区根目录**，用 / 分隔；不要用绝对路径，也不要写出工作区之外");
            builder.AppendLine("- 写入类工具会由用户审批后才真正落盘；若被拒绝，请询问或换一种改法");
            builder.AppendLine("- 命令、打开网页、MCP 调用会先让用户确认再执行；被拒绝时不要反复重试同一条");
            builder.AppendLine("- 跑命令前先想清楚它在当前工作目录下的副作用；能用具体命令就别用通配的大范围命令");
            builder.AppendLine("- 工具结果会回灌给你，之后你再决定「继续调用工具」还是「给出最终答复」");
            builder.AppendLine("- 不再需要工具时，直接用自然语言回答，并且**不要**再输出 action 代码块");
            builder.AppendLine("- 回答简洁、可执行，代码优先；涉及改动时说明改了哪个文件");

            if (settings != null && !string.IsNullOrWhiteSpace(settings.CustomInstructions))
            {
                builder.AppendLine();
                builder.AppendLine("用户的附加要求：");
                builder.AppendLine(settings.CustomInstructions);
            }

            // 项目规则放最后：位置越靠后模型越当回事，而「这个仓库自己的规矩」理应压过通用偏好
            if (!string.IsNullOrWhiteSpace(projectRules))
            {
                builder.AppendLine();
                builder.AppendLine("项目规则（来自本工作区的规则文件，优先级高于上面的通用规则，请严格遵守）：");
                builder.AppendLine(projectRules);
            }

            return builder.ToString();
        }
    }
}
