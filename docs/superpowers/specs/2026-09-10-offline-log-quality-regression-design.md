# 离线日志质量筛选与场景回归集设计

## 1. 背景

`logs/` 已包含一批真实游戏过程截图，既有购物、发现、战斗画面，也有白屏、黑屏连接过渡、插件遮挡和裁切不完整的画面。当前识别管线已经能在运行时把非购物场景拦截为不可操作，但缺少一套可重复、可审计的离线质量清单。没有清单时，后续调整场景阈值、卡牌匹配或操作规划容易把垃圾帧当成有效样本，也容易遗漏“战斗状态禁止买卖”这一安全要求。

本设计把日志整理为可版本化的 manifest，并用 manifest 驱动离线回归测试。它只验证识别和安全边界，不改变实际点击、买卖、刷新或快捷键执行逻辑。

## 2. 目标

1. 自动扫描 `logs/live-frames`、`logs/test-crops` 及现有 crop 目录，识别明显无效或高风险样本。
2. 为每个样本记录预期场景、质量结论、可操作性和排除原因。
3. 建立 Shopping、Discover、Combat、Unknown、Garbage 五类稳定回归样本。
4. 确保战斗帧和发现帧永远不会进入购物操作流程。
5. 让离线检查无需启动 EXE、无需连接炉石、无需 AI API，且结果可重复。
6. 不复制 688 MiB 的图片；测试清单只保存仓库内相对路径和测量结果。

## 3. 非目标

- 不在本阶段实现新的实时输入、买卖、刷新或发现选择动作。
- 不把战斗帧当作卡牌训练候选；战斗帧只用于场景识别和安全暂停回归。
- 不引入云端模型或外部 AI API。
- 不让运行时自动把未经审核的截图加入训练集。
- 不修改当前“观察模式优先、低置信度停止”的安全策略。

## 4. 数据分类

### 4.1 场景类别

| 类别 | 用途 | 默认可操作 |
| --- | --- | --- |
| `Shopping` | 验证酒馆场景、金币/等级、槽位和随从匹配 | 仅在所有前置条件满足时 |
| `Discover` | 验证发现界面和等待选择状态 | 否，暂停购物动作 |
| `Combat` | 验证战斗界面识别和安全暂停 | 否 |
| `Unknown` | 验证未知画面的保守降级 | 否 |
| `Garbage` | 白屏、黑屏、严重裁切、解码失败或重复帧 | 否 |

文件名中的场景后缀只作为初始标签，最终清单同时保留识别结果；两者不一致时标记为 `review`，不能直接作为高置信度训练样本。

### 4.2 质量规则

筛选器至少计算以下指标：

- 图像是否能解码、尺寸是否达到全屏/裁剪样本的最低要求；
- 亮度均值、亮度标准差、过亮像素比例、过暗像素比例；
- 边缘/纹理比例，用于发现纯色或空白帧；
- 感知哈希，用于识别重复帧；
- crop 是否覆盖完整卡牌区域，是否明显只剩边缘或木板；
- 现有 `SceneRecognizer` 的场景与置信度。

默认结论：

- 过亮比例达到 95% 或过暗比例达到 50%，且方差很低：`Garbage`；
- 解码失败、尺寸不足或裁切严重：`Garbage`；
- 与同组样本感知哈希重复：保留一张，其余 `Garbage` 并记录 `duplicate-of`；
- 购物/发现/战斗的完整画面：`include=true`；
- 有插件遮挡、动态提示或无法稳定复核的画面：`review`，只做安全回归，不做训练候选；
- 法术卡或未知卡牌 crop：保留为 UNKNOWN 兜底样本，不强行匹配为随从。

阈值必须集中定义，并在 manifest 中记录版本；筛选器不得依赖当前机器屏幕分辨率。

## 5. Manifest 设计

建议路径：`tests/fixtures/log-quality/live-logs.manifest.json`。

顶层字段：

```json
{
  "schemaVersion": 1,
  "sourceCommit": "2dd06aa5bf68e5b866183e63a08378fc42de8f42",
  "generatedAt": "2026-09-10T00:00:00Z",
  "qualityPolicyVersion": 1,
  "samples": []
}
```

每个样本字段：

```json
{
  "path": "logs/live-frames/20260907-204515-857-Combat.png",
  "sourceLabel": "Combat",
  "expectedScene": "Combat",
  "quality": "good",
  "include": true,
  "expectedActionable": false,
  "expectedPauseReason": "scene-not-actionable",
  "metrics": {
    "width": 1920,
    "height": 1080,
    "brightnessMean": 61.2,
    "brightnessStdDev": 37.9,
    "brightRatio": 0.01,
    "darkRatio": 0.08,
    "edgeRatio": 0.21,
    "perceptualHash": "..."
  },
  "reason": "战斗完整画面，仅用于验证安全暂停"
}
```

`expectedActionable` 是安全断言，不表示用户一定想执行动作。`Garbage` 样本必须包含 `include=false` 和明确的 `reason`；边界样本可使用 `quality=review`，但仍不能触发操作。

## 6. 工具与测试

### 6.1 离线筛选命令

在 `tools/OfflineVisionProbe` 增加批量筛选入口，建议形式：

```text
OfflineVisionProbe --curate-logs <logsDir> --manifest <manifestPath> [--check-existing]
```

要求：

- 默认只读图片和本地 catalog/profile；
- 输出每类数量、垃圾原因统计、场景标签冲突和待人工复核列表；
- 使用稳定排序和固定格式，重复运行生成相同内容（时间字段可通过参数关闭或单独更新）；
- 不修改原始截图；
- 遇到单张损坏图片时记录错误并继续扫描。

### 6.2 回归测试

新增 manifest 驱动测试，至少覆盖：

1. 完整 Shopping 帧识别为 Shopping；满足金币、等级和卡牌约束的样本才允许 `Actionable=true`。
2. Discover 帧识别为 Discover，并产生等待发现选择的暂停原因。
3. Combat 帧识别为 Combat，并产生不可买卖的暂停原因。
4. Unknown 和 Garbage 帧不得产生任何可执行买卖/刷新动作。
5. 法术卡 crop 与未知卡 crop 必须保持 UNKNOWN 或对应的非随从类型。
6. manifest 中所有 `include=false` 样本不会被训练候选选择器采纳。
7. 批量筛选结果具有确定性，阈值版本变更会使测试明确失败而不是静默改变数据。

## 7. 实施顺序

1. 先实现质量指标和 manifest 生成器，使用当前 335 个日志条目生成初版清单。
2. 人工复核白屏、黑屏连接帧、严重裁切 crop 和少量 `review` 边界样本。
3. 加入场景安全回归测试，确保现有观察模式和暂停逻辑不被破坏。
4. 将批量命令接入本地测试说明和 `PROJECT_PROGRESS.md`，记录运行方法及当前样本统计。
5. 运行全量 .NET 测试和离线批量检查后，再考虑把筛选入口接到可视化页面。

## 8. 验收标准

- 335 个日志条目全部出现在 manifest，路径无缺失、无重复、无未分类项。
- 已知白屏 `20260907-203423-650-Unknown.png` 和黑屏连接帧 `20260907-213143-760-Unknown.png` 被标记为 `Garbage` 或 `review`，且不可操作。
- 至少包含 Shopping、Discover、Combat、Unknown、Garbage 各 1 个稳定样本。
- 所有 Combat/Discover/Unknown/Garbage 回归测试均断言不可买卖。
- 正常 Shopping 样本仍能通过现有场景和卡牌安全门槛。
- 离线命令不启动 EXE、不访问网络、不需要 AI API。
- 工作区测试通过，且不会产生被误提交的大型临时文件。

## 9. 后续扩展

完成本设计后，再根据回归结果决定是否增加：多帧稳定确认、战斗/发现 UI 专用区域特征、手牌与战场遮挡处理，以及在用户明确启用后才执行的操作闭环。
