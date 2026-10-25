# SDK 2.0.29 验证、QRE 接入及 DeepSeek1017 续测

## 目标与已核实事实

用户已委派Claude完成SDK修复并指示核对NuGet，验证后更新。SDK源码提交`1afa59912c16312a03aa8baf5ae150f6dd0a538d`，版本2.0.29，新增`VllmDeepseekV4ChatClient`及`DeepseekSafetyTagFilter`；构建/发布CI40203046171、40203046159成功，发布日志显示NuGet.org于2026-10-25 22:38（北京时间）接收包。公共索引/下载暂未可用，不能把CI成功当作正式包消费成功。

SDK V4继承V3，普通/流式文本和推理历史过滤`<ds_safety>…</ds_safety>`及尾随Safe标记；V3默认行为不变。QRE当前0.25.2仍创建V3，因此只改包版本不会启用修复。QRE .codegraph无可用索引，已按工具指示常规查源、不自行索引。旧SDK/QRE、RuleMaker目录中的用户配置/记忆文件及所有封存批次不动。

## 串行闸门与并行归属

1. Root只读核对公开NuGet 2.0.29：等待公开下载，独立空包缓存、仅nuget.org源恢复，检查nuspec仓库提交/版本、实际DLL身份和源提交对应；必要时比对GitHub发布资产包（排除NuGet仓库签名差异）。不覆盖本地NuGet缓存、不用本地pack冒充正式包。
2. 正式包离线行为验证：通过IChatClient实际调用V4普通/流式以及原生Anthropic，覆盖审核标签跨chunk、无标签文本不变、思考与回答通道、tool name/id/arguments不变、usage/finish元数据保留、调用取消、native schema/thinking/tool-result历史；标记清理是已声明预期变换，工具参数不能被清洗或伪称原始文本未变。已发布SDK若暴露真实阻塞缺陷，暂停接入，记录复现，先修组件/新版本再走本链，不在评估脚本绕过。
3. 三方评审本稿通过且上述包验证通过，才改QRE。Root独占版本/包引用/能力矩阵SDK版本/docs，Claude在已/rc验证的独立终端独占provider选择和针对性测试；两者可并行，引用落地后再编译整合。Claude不设总期限、不授权额外真实API。Antigravity独立评审并可只读复核最终报告，不读凭据/raw。
4. QRE本地回归通过→提交推送main→CI和正式发布成功→公开Engine/Models0.25.3空缓存消费及CLI版本/工厂V4核验→RuleMaker引用更新、针对性离线验收→新运行时/输入/参数一次封存→DeepSeek1017生产预检6交换及5次完整任务。

## QRE改动范围

- `Directory.Build.props`统一版本0.25.3；7处SDK引用2.0.28→2.0.29（Models、CLI、Experimental、IntegrationTests和3例子）。现网NuGet QRE最高0.25.2，若发布前被占用则重新确认版本，不覆盖已发包。
- `Models/Providers/VllmModelProviders.cs`：仅明确DeepSeek V4族（`deepseek-v4`边界，含精确`deepseek-v4.1-flash-1017`，大小写/既有前导斜杠按现规则归一化）创建V4；V3、R1及不明确版本名保留原V3路径，不能把所有deepseek模型静默切换。Qwen/Claude等不变。
- 新增`UnitTests/Models/DeepseekV4CompatibilityTests.cs`及必要的selector案例：证明选到实际V4类型，V3/R1类型不变，通过QRE工厂的真实SDK fake HTTP验证普通/流式清理及不清洗工具参数，包含native schema/工具历史正例、精确1017模型名、usage/取消。不是仅测构造名字或直接调用独立过滤函数。
- `Models/Diagnostics/QreTransportCapabilityMatrix.cs` VerifiedSdkVersion2.0.29，重跑已有真实SDK全部矩阵格；保持历史SDK2.0.27冻结fixture字节，不重新录制以掩盖变化。补充V4特定实际行为测试，不能用deepseek-v3覆盖格当作已测V4。出站reasoning历史的清洗需测试原始与发送差异，不把该变换普遍登记到V3或任意模型。
- Changelog/中英README/诊断文档记录版本、V4路由和过滤边界；SkillsWorkflow例子的反射依赖重新验证。其它版本历史说明不机械替换。

## 验收、发布及模型预算

SDK正式包身份和真实客户端离线验证为硬闸门。QRE适当单元测试与完整单元回归，现有矩阵/Anthropic schema与workflow例子通过；无需材料全量E2E。远端Linux/Windows CI和既有发布链全部通过后，空缓存只从公开NuGet恢复Engine/Models0.25.3并证明SDK2.0.29与V4工厂实际生效，不满足则不更新运行时。

RuleMaker业务源仍固定f32ce601b0aec8954aba5379c123696083aaeb87及r1资产，3C第2步23.1.3、输入集/所选输入及评分器/10向量/profile不变。只覆盖正式QRE/SDK依赖后重编译；新DLL哈希独立封存，不能称175旧文件仍相同。已完成27B、Haiku、MAX和QwenFlash结果保留原版本参考，注明新旧版本/协议/非同期混杂，不宣称纯模型因果。不额外重测其它模型，除非另有必要性和授权。

DeepSeek型号必须`deepseek-v4.1-flash-1017`、阿里云Anthropic接口；原预备队列真实POST为0。只使用新私有队列/数据库/端口/密钥/audit/计数器；独立上限64次上游POST（含6预检/全部内部重试），目标5任务，不补抽样、不恢复、不错换无后缀型号、不降级协议。旧所有模型已耗101次单列；新DeepSeek最高64，跨所列批次上限165。温度等采样字段omit、thinking=false、输出4096/收尾512/预检256，40工具/3修复/300秒、累计token不限、窗口262144/0.9/0.8/16000均不变。

官方文档明确1017只保证JSON Object，发出output_config.format.schema不能表述为服务端强制JSON Schema。六交换fake和实际生产预检通过才做5任务，原始raw→SDK→host核验保留；原始审核块被SDK清洗是版本声明行为，不洗工具参数，报文原实体始终完整私有保留。若提供商/凭据/接口拒绝，按原证据区分SDK与配置/服务端原因，不盲目重试。公开脱敏报告、逐任务和实体哈希；完成后核验PID路径只停本批自建进程，保留数据，不动共享服务或IA.Engine版本。

## 回退

任何闸门失败都记录确切层次和已消费，不把mock通过、构造成功、CI或HTTP200单独当业务完成。不原地覆盖旧封存运行时或历史Golden，不提交用户私有配置/raw。QRE更新可回退依赖/路由提交，包版本已发布不可覆盖；发布失败先核实状态，禁止重复推包冒充升级。
