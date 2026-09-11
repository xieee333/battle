# Battlegrounds Vision Agent

面向《炉石传说》酒馆战棋的本地视觉识别与规则自动化工具。

当前 `feature/vision-agent-v1` 已完成规则内核、视觉识别基础、规则编辑界面、观察模式、受保护的输入层和回放闭环。真实游戏接入仍应先使用观察模式和脱敏回放验证。

## 当前能力

- 通过不可变 `GameSnapshot` 驱动规则规划和动作后验证。
- 支持商店、手牌、场面和发现候选的识别接口，以及本地卡库和 SQLite 特征缓存。
- 观察模式只记录拟执行动作，不发送键鼠输入。
- 执行模式要求炉石窗口前台、快照可操作、布局版本匹配，并支持 F7 暂停和 F8 紧急停止。
- 视觉监听器保留最近约 4 秒的连续帧，结合金币、商店槽位、手牌和战场变化判断购买事件；买入后马上上场也会标记为“购买并上场”。
- 商店槽位优先通过每张牌顶部的紫色等级徽章动态定位，适配商店 5/6/7 张牌的居中布局；检测不到徽章时回退到校准槽位。
- 商店卡牌使用专用缩略图特征：从官网完整卡图提取插画区域，再与游戏商店牌面匹配；金色卡牌可匹配到普通卡 ID，低置信度结果保持 UNKNOWN。卡库同时记录随从/法术类型，法术不会冒充随从。
- 手牌数量只根据手牌区上缘的彩色卡边连续段估计，适配扇形重叠的 4/9 张样本；不会读取右下角的 `9/9`、`5/12` 容量文字，也不会用该文字伪造数量。
- 金币优先通过底部金币槽的亮/暗状态计数：早期回合只有少量实际槽位时只数亮槽，后期出现 10 个槽位时忽略暗槽；金币条完全不存在时才回退到数字模板。
- 回放场景覆盖购买、三连奖励、发现、卖出和卖出后的场面重定位。

## 离线日志质量筛选

可以直接使用 `OfflineVisionProbe` 扫描已有日志，不需要打开 EXE、启动炉石或配置 AI API：

```powershell
dotnet run --project .\tools\OfflineVisionProbe -- --curate-logs .\logs `
  --manifest .\tests\fixtures\log-quality\live-logs.manifest.json `
  --source-commit (git rev-parse HEAD)

dotnet run --project .\tools\OfflineVisionProbe -- --validate-manifest `
  .\tests\fixtures\log-quality\live-logs.manifest.json --repo-root .
```

筛选结果会区分 `Shopping`、`Discover`、`Combat`、`Unknown` 和垃圾样本，并记录亮度、纹理、重复帧和排除原因。命令即使发现垃圾/待复核样本也会写出 manifest；退出码为 1 是提醒人工复核，不代表程序崩溃。后续把新截图放入 `logs` 后重复运行即可更新清单。

在质量清单基础上，可以生成版本自适应训练索引：

```powershell
dotnet run --project .\tools\OfflineVisionProbe -- --build-training-index `
  .\tests\fixtures\log-quality\live-logs.manifest.json `
  --output .\tests\fixtures\log-quality\vision-training.index.json `
  --repo-root . --catalog .\data\catalog\catalog.db
```

索引只收录 `Good + Include + 有场景标签 + logs/live-frames` 的原始帧，并记录当前卡库版本、卡牌数量和指纹；`--max-per-scene N` 可为每个场景做确定性的均匀抽样。它是混合识别的训练数据索引，不是绑定某一版本卡名的神经网络权重：卡牌名称始终从当前 `catalog.db` 动态读取，人工在识别校验窗口确认的卡槽样本才会作为可复用正样本。

如果同时使用炉石传说盒子的快捷键（例如指向商店随从后按住并松开 W 购买），盒子仍然负责执行按键，本项目只读取执行前后的画面变化，不会抢占 W，也不会把一次刷新误判成购买。由于外部盒子没有向本项目提供按键事件，日志中的购买确认来自视觉证据；若卡牌本身仍识别为 UNKNOWN，只能确认“发生了购买/上场”，不能安全给出具体卡牌 ID。

## 卡库版本更新

卡库与程序逻辑分离，应用启动时从 `data\catalog\catalog.db` 读取当前版本。主界面的“更新卡库”按钮可以导入版本化 `.zip` 更新包，更新包必须包含：

```text
catalog.manifest.json
catalog.db
cards/*.png
```

`catalog.manifest.json` 至少包含 `version`、`updatedAt` 和 `cards`（每项包含 `cardId`、`nameZhCn`、`tier`、`imagePath`，可选 `kind`）。程序会校验版本、卡牌元数据、类型、图片路径、图片可解码性和压缩包路径安全性；新版本通过后原子替换旧卡库，同版本跳过，旧版本拒绝，失败时保留旧版本。缺少普通卡特征时，更新过程会从卡图自动生成并缓存 ORB 特征。

清单示例：

```json
{
  "version": "2026.09.06",
  "updatedAt": "2026-09-06T08:00:00+08:00",
  "cards": [
    {
      "cardId": "GAME_001",
      "nameZhCn": "示例随从",
      "tier": 2,
      "imagePath": "cards/GAME_001.png"
    }
  ]
}
```

`catalog.db` 中的 `cards` 表必须与清单逐项一致，`catalog_meta` 中的版本和时间也必须一致；图片路径使用包内相对路径，不能指向包外文件。

卡库更新通过校验后会以当前版本卡图重建每张普通卡的识别特征；新增卡牌或同卡 ID 换图无需重新编译程序，下一次离线或实时识别会自动使用新特征。若更新包中的图片损坏或元数据不一致，旧卡库保持不变。

点击“同步国服卡库”即可更新，无需登录、Client ID、Secret 或环境变量。来源为国服官网 https://hs.blizzard.cn/battlegrounds/ 使用的公开接口 `https://webapi.blizzard.cn/hs-cards-api-server/api/web/cards/tavern`，以 JSON POST 分页获取随从，使用 `battlegrounds.image` 专用卡图。当前同步范围为随从，不包含英雄、饰品或酒馆法术。旧的凭据启动脚本不再需要。

同步前暂停运行并保留当前规则，完整下载和图片校验后替换卡库；接口出错、分页重复或缺失时保留旧库。当前接口请求不再限制 `bg_card_type=minion`，会从全量结果中保留有酒馆等级的随从和法术（最近一次同步为 274 张随从 + 74 张法术 = 348 张，数量会随版本变化）。版本标识为内容指纹，表示数据变化，不代表国服客户端补丁号。卡图、类型和普通卡 ORB 特征保存在本地；程序启动时会从当前版本卡图生成商店缩略图特征，因此卡库更新后不需要重新标定识别代码。

如果某个伙伴或新卡在接口中没有资料，识别结果会保留为 `UNKNOWN` 并显示待复核；未知卡只阻断“针对这张未知卡”的买/卖/上场/发现选择，不会阻断对已知目标的无关操作，也不会自动猜测名称。

导入本地更新包不要求重新编译程序，也不依赖联网；官网同步则只在用户主动点击“同步官网”时访问接口。两种方式最终都走同一套本地校验和原子替换流程。

制作更新包时，可以使用：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/build-catalog-package.ps1 `
  -SourceDirectory .\data\catalog-v2026.09.06 `
  -OutputPath .\dist\catalog-v2026.09.06.zip
```

## 构建与验证

在 Windows 10/11 的 PowerShell 中先运行一次项目环境脚本：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/setup-dev.ps1
```

它会使用 Microsoft 官方安装脚本，把 x64 .NET 9 SDK 放入项目本地的 `tools\dotnet`，不要求修改系统 PATH。然后运行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

回放清单位于 `testdata/manifests`，原始用户录屏不应提交到 Git。

视觉模板配置放在程序目录的 `data\vision\profile.json`，由 `layout`（商店、手牌、战场、发现、卡槽和金币槽的归一化坐标）、`anchors`（`shop`、`hand`、`board` 锚点图片）、`scenes`（购物/战斗/发现模板图片）和 `digits`（金币数字、酒馆等级、护甲模板图片）组成。金币数字模板只是金币条不存在时的备用方案；早期回合金币条可能尚未出现，程序会在无法确认时显示 `unknown`，不会把不存在的槽误判成 0。截图顶部插件叠加的“第几回合”不在识别链路中。模板文件路径必须相对于 `profile.json`，程序会拒绝越界路径、损坏图片和缺失必需锚点。没有该配置时，“开始运行”会保持暂停，这是预期的安全行为。

截图预览现在支持购物阶段校准：截取或导入购物阶段画面，在截图上分别框选商店、手牌和战场，必要时再框选金币、本数和发现区域，点击“生成购物阶段配置”即可写入 `data\vision\profile.json`。程序会从该样本生成基础锚点、购物场景样本和默认回退卡槽（商店 7、手牌 10、战场 7）；运行时商店会先尝试动态槽位检测。金币和本数数字模板仍是自动操作的安全门槛，未配置时只允许验证布局，不会发送输入。

离线检查不需要打开炉石。使用 GUI 时打开 `dist\win-x64\BattlegroundsVisionAgent.App.exe`，在“截图预览”导入 PNG 后点击“离线识别当前截图”查看场景、金币、本数、护甲和各区域卡槽；点击“详细识别校验”会自动识别并为卡库中已匹配的随从/法术显示卡图缩略图。也可以不启动 GUI，直接运行 `OfflineVisionProbe` 对单张截图生成文字报告。未配置 `profile.json`、卡库缺失或识别置信度不足时会显示未知/待复核，不会发送游戏输入。

## 安全边界

运行时仅使用屏幕画面和模拟键鼠，不读取游戏内存、不注入游戏进程，也不绕过反作弊机制。自动化仍可能违反游戏服务条款，使用者需要自行评估账号风险。低置信度、未知界面、失焦、场满无法安全清理或高风险卖出未确认时，程序应保持不动。

完整产品与技术规格见：

- `docs/superpowers/specs/2026-09-04-battlegrounds-vision-agent-design.md`
- `docs/superpowers/plans/2026-09-04-battlegrounds-vision-agent-implementation.md`

