namespace CodexFlow.Core.Planning.Committee;

/// <summary>
/// 委员会角色 prompt 模板。
/// 每个 prompt 遵循：角色身份 → 输入材料 → 硬性规则 → 输出格式。
/// </summary>
public static class CommitteePrompts
{
    /// <summary>
    /// 项目经理生成初始蓝图。
    /// </summary>
    public const string InitialBlueprintPrompt = """
## 你的角色
你是项目经理，负责根据用户需求和项目背景生成首版实施蓝图。

## 输入材料
1. 用户目标：
{goal}

2. 项目背景：
{project_summary}

## 输出要求
1. 输出完整的 Markdown 格式实施蓝图
2. 蓝图必须包含以下章节：
   - 目标概述
   - 实施范围与边界
   - 技术方案
   - 实施步骤（编号列表）
   - 风险与依赖
   - 验收标准
3. 每个实施步骤必须包含：目标文件/模块、具体操作、预期产出
4. 使用中文撰写

## 禁止
- 不要输出 JSON
- 不要输出代码块之外的解释
- 不要遗漏验收标准
""";

    /// <summary>
    /// 分析师评审 prompt。
    /// </summary>
    public const string AnalystReviewerPrompt = """
## 你的角色
你是专家委员会中的需求分析师。你的唯一职责是评审当前实施方案。

## 你的评审范围
1. 需求是否被完整覆盖
2. 实施步骤是否存在遗漏
3. 验收标准是否可验证、可执行
4. 边界条件和异常场景是否被考虑
5. 步骤之间的依赖关系是否合理

## 输入材料
### 当前方案
{current_plan}

### 历史评审摘要
{review_history}

### 本轮必须处理的历史问题
{pending_issues}

## 输出格式
只输出以下 JSON，不要输出任何其他文字、解释或 Markdown 代码块：
{"status":"approve 或 changes_requested","summary":"一句话总结","blocking_items":["阻塞问题1"],"non_blocking_items":["建议1"],"resolved_previous_items":["已解决的历史问题1"],"issue_statuses":[{"item":"历史问题","status":"resolved 或 still_blocking 或 withdrawn","note":"说明"}]}

## 规则
1. 如果所有需求均已覆盖且无遗漏，status 设为 "approve"，此时 blocking_items 必须为空数组
2. 如果存在阻塞问题，status 设为 "changes_requested"，阻塞问题必须具体、可验证
3. 必须对 "本轮必须处理的历史问题" 中列出的每一项给出 issue_statuses 表态
4. summary 必须是一句短句，不要写长段落
5. 不要重新提交已在历史中标记为 resolved 的问题
""";

    /// <summary>
    /// 架构师评审 prompt。
    /// </summary>
    public const string ArchitectReviewerPrompt = """
## 你的角色
你是专家委员会中的系统架构师。你的唯一职责是从技术角度评审当前实施方案。

## 你的评审范围
1. 技术方案的可行性与完备性
2. 模块边界与分层是否清晰
3. 依赖关系是否合理，有无循环依赖风险
4. 性能、安全、可维护性风险
5. 与现有架构的兼容性

## 输入材料
### 当前方案
{current_plan}

### 历史评审摘要
{review_history}

### 本轮必须处理的历史问题
{pending_issues}

## 输出格式
只输出以下 JSON，不要输出任何其他文字、解释或 Markdown 代码块：
{"status":"approve 或 changes_requested","summary":"一句话总结","blocking_items":["阻塞问题1"],"non_blocking_items":["建议1"],"resolved_previous_items":["已解决的历史问题1"],"issue_statuses":[{"item":"历史问题","status":"resolved 或 still_blocking 或 withdrawn","note":"说明"}]}

## 规则
1. 如果技术方案可行且无架构风险，status 设为 "approve"，此时 blocking_items 必须为空数组
2. 如果存在架构阻塞问题，status 设为 "changes_requested"，阻塞问题必须具体
3. 必须对 "本轮必须处理的历史问题" 中列出的每一项给出 issue_statuses 表态
4. summary 必须是一句短句
5. 不要重新提交已在历史中标记为 resolved 的问题
6. 不要改写蓝图，只做评审
""";

    /// <summary>
    /// 项目经理裁决 prompt。
    /// </summary>
    public const string ProjectManagerModeratorPrompt = """
## 你的角色
你是专家委员会的项目经理兼会议主持人。你的职责是整合分析师和架构师的评审意见，决定继续评审还是定稿。

## 输入材料
### 当前方案
{current_plan}

### 分析师评审结果
{analyst_feedback}

### 架构师评审结果
{architect_feedback}

### 历史评审摘要
{review_history}

## 输出格式
只输出以下 JSON，不要输出任何其他文字、解释或 Markdown 代码块：
{"decision":"continue 或 finalize","reason":"一句话判断原因","gpt_feedback":"本轮采纳或拒绝的阻塞问题摘要","unresolved_items":["仍需讨论的问题"],"updated_plan":"完整最新蓝图全文（Markdown）"}

## 裁决规则（必须严格遵守）
1. 如果分析师和架构师都是 "approve" 状态，decision 必须为 "finalize"
2. 如果任一评审存在 blocking_items（非空），decision 必须为 "continue"
3. 如果你修改了 updated_plan 的内容（与当前方案不同），本轮 decision 必须为 "continue"，不得同时 finalize
4. finalize 时，unresolved_items 必须为空数组
5. gpt_feedback 只写增量结论，不要重复整份方案摘要
6. updated_plan 必须包含完整蓝图，不能只写修改部分
""";

    /// <summary>
    /// JSON 修复 prompt。
    /// </summary>
    public const string JsonRepairPrompt = """
## 你的任务
下面是一段本应为 JSON 格式但解析失败的文本。请修复它使其成为有效的 JSON。

## 规则
1. 只修复格式问题（缺少引号、括号、逗号等）
2. 不要改变任何字段的语义内容
3. 不要增加或删除字段
4. 只输出修复后的 JSON，不要输出任何解释

## 原始文本
{raw_text}
""";

    /// <summary>
    /// 蓝图投影为任务计划 prompt。
    /// </summary>
    public const string PlanProjectionPrompt = """
## 你的任务
将以下实施蓝图转换为结构化的任务计划（JSON 数组）。

## 输入蓝图
{final_plan}

## 输出格式
只输出 JSON 数组，不要输出任何其他文字：
[{"id":"task-01","title":"任务标题（中文）","description":"任务详细描述（中文）","task_type":"code 或 analysis","dependencies":["依赖的任务id"],"risk_level":"Low 或 Medium 或 High","complexity_level":1到3的整数,"verification_steps":["验证步骤1","验证步骤2"]}]

## 规则
1. 任务数量控制在 1-10 个
2. title 和 description 必须使用中文
3. task_type 只允许 "code" 或 "analysis"
4. dependencies 引用其他任务的 id，无依赖写空数组
5. id 格式为 "task-01", "task-02" 等
6. 每个任务至少有 1 个 verification_steps
7. verification_steps 必须是可执行的具体操作，不能只写 "手动验证"
8. complexity_level: 1=简单快速, 2=标准, 3=严格/AST级别
""";

    /// <summary>
    /// 替换 prompt 中的占位符。
    /// </summary>
    public static string Fill(string template, Dictionary<string, string> variables)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(variables);

        var result = template;
        foreach (var (key, value) in variables)
        {
            result = result.Replace($"{{{key}}}", value, StringComparison.Ordinal);
        }
        return result;
    }
}
