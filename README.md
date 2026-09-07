# Battlegrounds Vision Agent

面向《炉石传说》酒馆战棋的本地视觉识别与规则自动化工具。

当前 `feature/vision-agent-v1` 已完成规则内核、视觉识别基础、规则编辑界面、观察模式、受保护的输入层和回放闭环。真实游戏接入仍应先使用观察模式和脱敏回放验证。

## 当前能力

- 通过不可变 `GameSnapshot` 驱动规则规划和动作后验证。
- 支持商店、手牌、场面和发现候选的识别接口，以及本地卡库和 SQLite 特征缓存。
- 观察模式只记录拟执行动作，不发送键鼠输入。
- 执行模式要求炉石窗口前台、快照可操作、布局版本匹配，并支持 F7 暂停和 F8 紧急停止。
- 视觉监听器保留最近约 4 秒的连续帧，结合金币、商店槽位、手牌和战场变化判断购买事件；买入后马上上场也会标记为“购买并上场”。
- 回放场景覆盖购买、三连奖励、发现、卖出和卖出后的场面重定位。

如果同时使用炉石传说盒子的快捷键（例如指向商店随从后按住并松开 W 购买），盒子仍然负责执行按键，本项目只读取执行前后的画面变化，不会抢占 W，也不会把一次刷新误判成购买。由于外部盒子没有向本项目提供按键事件，日志中的购买确认来自视觉证据；若卡牌本身仍识别为 UNKNOWN，只能确认“发生了购买/上场”，不能安全给出具体卡牌 ID。

## 卡库版本更新

卡库与程序逻辑分离，应用启动时从 `data\catalog\catalog.db` 读取当前版本。主界面的“更新卡库”按钮可以导入版本化 `.zip` 更新包，更新包必须包含：

```text
catalog.manifest.json
catalog.db
cards/*.png
```

`catalog.manifest.json` 至少包含 `version`、`updatedAt` 和 `cards`（每项包含 `cardId`、`nameZhCn`、`tier`、`imagePath`）。程序会校验版本、卡牌元数据、图片路径、图片可解码性和压缩包路径安全性；新版本通过后原子替换旧卡库，同版本跳过，旧版本拒绝，失败时保留旧版本。缺少普通卡特征时，更新过程会从卡图自动生成并缓存 ORB 特征。

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

点击“同步国服卡库”即可更新，无需登录、Client ID、Secret 或环境变量。来源为国服官网 https://hs.blizzard.cn/battlegrounds/ 使用的公开接口 `https://webapi.blizzard.cn/hs-cards-api-server/api/web/cards/tavern`，以 JSON POST 分页获取随从，使用 `battlegrounds.image` 专用卡图。当前同步范围为随从，不包含英雄、饰品或酒馆法术。旧的凭据启动脚本不再需要。

同步前暂停运行并保留当前规则，完整下载和图片校验后替换卡库；接口出错、分页重复或缺失时保留旧库。版本标识为内容指纹，表示数据变化，不代表国服客户端补丁号。卡图和普通卡 ORB 特征保存在本地，可离线使用。

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

视觉模板配置放在程序目录的 `data\vision\profile.json`，由 `layout`（商店、手牌、战场、发现和卡槽的归一化坐标）、`anchors`（`shop`、`hand`、`board` 锚点图片）、`scenes`（购物/战斗/发现模板图片）和 `digits`（金币、酒馆等级、护甲模板图片）组成。金币数字模板必须来自真实游戏画面；模板不是“只有这些金币值”，而是当前已经采样到的值，未采样值会安全地显示为 `unknown`，不会被猜成别的数字。截图顶部插件叠加的“第几回合”不在识别链路中。模板文件路径必须相对于 `profile.json`，程序会拒绝越界路径、损坏图片和缺失必需锚点。没有该配置时，“开始运行”会保持暂停，这是预期的安全行为。

截图预览现在支持购物阶段校准：截取或导入购物阶段画面，在截图上分别框选商店、手牌和战场，必要时再框选金币、本数和发现区域，点击“生成购物阶段配置”即可写入 `data\vision\profile.json`。程序会从该样本生成基础锚点、购物场景样本和默认卡槽（商店 7、手牌 10、战场 7）；金币和本数数字模板仍是自动操作的安全门槛，未配置时只允许验证布局，不会发送输入。

## 安全边界

运行时仅使用屏幕画面和模拟键鼠，不读取游戏内存、不注入游戏进程，也不绕过反作弊机制。自动化仍可能违反游戏服务条款，使用者需要自行评估账号风险。低置信度、未知界面、失焦、场满无法安全清理或高风险卖出未确认时，程序应保持不动。

完整产品与技术规格见：

- `docs/superpowers/specs/2026-09-04-battlegrounds-vision-agent-design.md`
- `docs/superpowers/plans/2026-09-04-battlegrounds-vision-agent-implementation.md`

