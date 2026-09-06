# 酒馆战棋视觉规则助手：项目接续记录

更新时间：2026-09-06

项目目录：`D:\代码\battlegrounds-vision-agent\.worktrees\vision-agent-v1`

这份文件用于在另一台电脑上继续开发，记录当前完成度、已验证内容和下一步工作。

## 当前已完成

### 卡库与目标牌界面

- 接入国服官网酒馆战棋公开数据同步流程。
- 卡库使用本地 SQLite 保存，卡图保存在 `data\catalog\cards`。
- 支持版本化卡库更新包，校验清单、数据库版本、图片路径和图片可解码性。
- 主界面目标牌列表显示本地卡图。
- 支持按名称或卡牌 ID 搜索。
- 支持按酒馆本数筛选。
- 支持本数从低到高、从高到低排序。
- 修复深色背景下下拉框文字不可见的问题。

### 截图与视觉校准

- `WindowsFrameSource` 已改为从屏幕合成画面捕获炉石前台窗口，解决硬件渲染窗口 DC 读到旧画面的问题。
- 炉石窗口通过进程名识别，避免把官网网页误认为游戏窗口。
- “截图预览”支持 3 秒倒计时截图和导入 PNG。
- 当前截图确认可以捕获到购物阶段画面。
- 预览窗口支持框选：商店、手牌、战场、金币、本数、发现区域。
- 新增“生成购物阶段配置”按钮：
  - 将区域草稿转换成运行时实际读取的 `data\vision\profile.json`。
  - 自动生成商店 7 槽、手牌 10 槽、战场 7 槽。
- 从购物阶段截图生成商店/手牌/战场锚点和购物场景样本。
- 已生成的配置可以被 `VisionProfileAssets.Load` 和布局识别器加载。
- 原来的 `.calibration.json` 仍是区域草稿；生成 `profile.json` 后才会进入运行时识别链路。
- 已用当前教程购物阶段截图完成一次实际标定：商店、手牌、战场、金币、本数共 5 个区域。
- 已修正商店与战场区域的重叠：商店框覆盖当前商店卡行，战场框从商店下方开始。
- 本次生成的运行时资产位于 `data\vision`，包括 `profile.json`、三个区域锚点和 `scenes\shopping.png`。
- 已放宽稳定帧检测对炉石背景、倒计时和发光动画的误判阈值；实机验证已从“等待稳定画面”推进到场景判断，并在战斗/英雄选择界面安全暂停。
- 新版程序每次启动默认恢复为“观察模式·只记录”，不会因上次保存的设置而自动进入真实执行。
- 启动时会同步显示卡库加载状态；卡库已加载但尚未选择目标牌时显示“已加载卡库，待选择目标牌”。

### 安全与测试

- 观察模式不发送键鼠输入。
- 非购物阶段、未知界面、金币/本数未知、卡牌未知或窗口失焦时应保持暂停。
- 已加入区域校准、原生截图绑定、卡库界面和 profile 构建测试。
- 最新整项目测试：147 项通过。
- 最新发布构建已成功，输出：`dist\win-x64\BattlegroundsVisionAgent.App.exe`。

### 最近一次实机复核（2026-09-06）

- 已启动最新发布程序并确认卡库正常加载：274 张卡牌，版本 `vblizzard-1edd1e423cbdc1e9`。
- 已修正启动状态文字：卡库加载完成但尚未选目标牌时显示“已加载卡库，待选择目标牌”。
- 已在观察模式下勾选第一张目标牌“催眠机器人”，确认“开始运行”按钮可用并成功进入识别循环。
- 实机画面先出现购物阶段，随后进入战斗/发现选择画面；进入非购物画面时程序显示“等待可操作阶段”，没有发送游戏输入，符合安全策略。
- 本次复核全程为观察模式，没有执行购买、刷新、上场或其他键鼠操作。
- 下一次复核需要在识别循环运行期间再次捕获明确的购物画面，确认购物画面会显示“购物阶段，等待数字/卡牌识别”，并继续完善数字与卡牌模板。

## 当前仍未完成

1. 数字模板采集：金币和酒馆本数目前只有区域框选，没有数字值模板，因此运行时会把它们视为未知并安全等待；界面会明确显示“购物阶段，等待数字/卡牌识别”，不再误报成“等待购物阶段”。
2. 真实卡牌识别验证：需要在多个购物阶段截图上验证卡图 ORB 特征和卡槽裁剪效果。
3. 购物/战斗/发现阶段的多样本场景模板和稳定帧选择。
4. 手牌、战场中的重叠卡牌和空槽识别优化。
5. 在明确购物画面下完成一次完整的“识别—规划—重新识别—验证”回放闭环；当前已完成启动和非购物阶段安全等待验证。
6. 识别通过后，才考虑在用户明确启用的情况下测试真实输入；目前不要直接使用执行模式。

## 在另一台电脑上继续

在项目根目录运行 PowerShell：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup-dev.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\verify.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

如果系统已经有 .NET 9 SDK，也可以直接运行 `dotnet`；项目脚本优先使用项目内的 `tools\dotnet\dotnet.exe`。

### 继续校准

1. 打开发布目录中的 `BattlegroundsVisionAgent.App.exe`。
2. 让炉石进入购物阶段并保持前台。
3. 点击“截图预览”→“3 秒后截图”，或者导入已有购物阶段 PNG。
4. 在截图上框选商店、手牌、战场；金币、本数可以同时框选。
5. 点击“生成购物阶段配置”。
6. 回到主界面，开启“观察模式 · 只记录”，勾选目标牌后再点击“开始运行”。

当前这一步只验证布局和观察结果，不应期待程序已经能自动执行购买或刷新。

## 重要文件

- `src\BattlegroundsVisionAgent.App\Views\CapturePreviewWindow.xaml`：截图与校准界面。
- `src\BattlegroundsVisionAgent.App\Views\CapturePreviewWindow.xaml.cs`：截图、框选和生成配置入口。
- `src\BattlegroundsVisionAgent.Vision\Recognition\VisionProfileBuilder.cs`：从购物截图生成 `profile.json`。
- `src\BattlegroundsVisionAgent.Vision\Recognition\VisionRecognitionPipeline.cs`：加载视觉 profile。
- `src\BattlegroundsVisionAgent.Vision\Recognition\TemplateLayoutRecognizer.cs`：布局和槽位识别。
- `src\BattlegroundsVisionAgent.Vision\Capture\WindowsFrameSource.cs`：Windows 炉石窗口截图。
- `src\BattlegroundsVisionAgent.Vision\Catalog\BlizzardCatalogSyncService.cs`：国服卡库同步。
- `tests\BattlegroundsVisionAgent.Vision.Tests\VisionProfileBuilderTests.cs`：配置生成和布局自检测试。

## Git 接续说明

当前目录原本的 `.git` 是指向旧电脑路径的 worktree 文件，旧路径不存在；现在已经重新初始化并接入远程仓库。`dist`、`bin`、`obj` 等生成物已经由 `.gitignore` 排除；卡库运行数据仍不通过 Git 提交。

当前远程仓库：`https://github.com/xieee333/battle.git`，主分支为 `main`。项目源码、测试和视觉标定资产已推送。

如果另一台电脑需要继续，先执行：

```powershell
git remote -v
git pull --ff-only origin main
```
