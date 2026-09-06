# 酒馆战棋视觉规则自动化工具实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 构建一款完全本地运行的 Windows 桌面工具，在 16:9 简体中文酒馆战棋画面中识别商店、手牌、场面和发现界面，并按人工规则安全执行刷新、购买、上场、发现和卖出。

**架构：** 使用 WPF 承载规则编辑器、悬浮层和确认弹窗；核心层以不可变 `GameSnapshot` 驱动纯函数规则引擎，视觉层只负责从局部截图生成状态，输入层只执行带布局版本的单步动作。所有动作都经过“计划—执行—重新识别—验证”闭环，观察模式复用同一计划但禁用真实输入。

**技术栈：** C#、.NET 9、WPF、OpenCvSharp、Microsoft.Data.Sqlite、xUnit、Windows Graphics Capture/GDI 回退、Win32 SendInput 与全局热键。

---

## 计划范围与阶段

本计划按可独立运行的纵向切片推进：任务 1–3 产出可测试的规则内核；任务 4–6 产出可工作的观察模式；任务 7 接入受保护的键鼠执行；任务 8 完成卡库、回放、打包和端到端验收。任何任务都不得在其测试未通过时启用下一阶段的真实输入。

## 文件结构

```text
BattlegroundsVisionAgent.sln
Directory.Build.props                         # 全局编译、分析器和 nullable 规则
src/
  BattlegroundsVisionAgent.App/
    App.xaml                                  # WPF 入口与资源
    App.xaml.cs                               # 依赖组装与异常兜底
    MainWindow.xaml                           # 目标牌列表 + 规则详情
    MainWindow.xaml.cs                        # 仅窗口生命周期
    ViewModels/MainViewModel.cs               # 配置、运行和观察模式命令
    Views/StatusOverlayWindow.xaml             # 游戏上方状态悬浮条
    Views/SellConfirmationWindow.xaml          # 临时卖怪确认
  BattlegroundsVisionAgent.Core/
    Domain/GameSnapshot.cs                    # 单帧不可变游戏状态
    Domain/CardObservation.cs                 # 识别到的卡牌及位置
    Domain/CardRule.cs                        # 单牌规则
    Domain/AutomationAction.cs                # 单步动作联合类型
    Rules/AutomationPlanner.cs                # 状态机优先级纯函数
    Rules/ActionVerifier.cs                   # 动作前后快照校验
    Runtime/AutomationCoordinator.cs          # 采集、计划、执行、复核循环
    Runtime/RunState.cs                       # 运行/暂停/停止状态
    Configuration/AppSettings.cs              # 用户设置模型
    Configuration/SettingsRepository.cs       # JSON 设置持久化
  BattlegroundsVisionAgent.Vision/
    Capture/IFrameSource.cs                   # 截图接口
    Capture/WindowsFrameSource.cs             # Windows 窗口局部截图
    Geometry/NormalizedRect.cs                # 标准化坐标
    Geometry/LayoutLocator.cs                 # 16:9 锚点和区域定位
    Geometry/FrameStabilityDetector.cs        # 连续帧稳定判断
    Recognition/CardMatcher.cs                # 哈希召回 + 局部特征复核
    Recognition/DigitRecognizer.cs            # 金币和等级数字模板
    Recognition/SceneRecognizer.cs            # 购物/战斗/发现/未知场景
    Recognition/SnapshotRecognizer.cs         # 聚合为 GameSnapshot
    Catalog/CardCatalog.cs                    # 卡牌元数据查询
    Catalog/CardCatalogUpdater.cs             # 暂存、校验、原子更新
    Catalog/CardFeatureStore.cs                # SQLite 特征缓存
  BattlegroundsVisionAgent.Input/
    IInputExecutor.cs                         # 输入执行抽象
    SafeInputExecutor.cs                      # 焦点、版本和停止令牌门禁
    WindowsInputBackend.cs                    # SendInput 键鼠后端
    GlobalHotkeyService.cs                    # F7/F8 注册与释放
    ActionBindingResolver.cs                  # 快捷键或鼠标后端选择
  BattlegroundsVisionAgent.Replay/
    ReplayFrameSource.cs                      # 从录屏帧驱动识别
    ActionPlanRecorder.cs                     # 只记录计划，不输入
tests/
  BattlegroundsVisionAgent.Core.Tests/
    AutomationPlannerTests.cs
    ActionVerifierTests.cs
    SettingsRepositoryTests.cs
  BattlegroundsVisionAgent.Vision.Tests/
    NormalizedRectTests.cs
    FrameStabilityDetectorTests.cs
    CardMatcherTests.cs
    SnapshotRecognizerTests.cs
  BattlegroundsVisionAgent.Input.Tests/
    SafeInputExecutorTests.cs
    ActionBindingResolverTests.cs
  BattlegroundsVisionAgent.Replay.Tests/
    ReplayScenarioTests.cs
testdata/
  manifests/                                  # 脱敏样本标签，不提交原始账号画面
  synthetic/                                  # 合成测试图
scripts/
  verify.ps1                                  # restore/build/test 汇总
  package.ps1                                 # win-x64 自包含发布
```

## 任务 1：建立可重复构建的解决方案

**文件：**
- 创建：`BattlegroundsVisionAgent.sln`
- 创建：`Directory.Build.props`
- 创建：`src/BattlegroundsVisionAgent.App/BattlegroundsVisionAgent.App.csproj`
- 创建：`src/BattlegroundsVisionAgent.Core/BattlegroundsVisionAgent.Core.csproj`
- 创建：`src/BattlegroundsVisionAgent.Vision/BattlegroundsVisionAgent.Vision.csproj`
- 创建：`src/BattlegroundsVisionAgent.Input/BattlegroundsVisionAgent.Input.csproj`
- 创建：`src/BattlegroundsVisionAgent.Replay/BattlegroundsVisionAgent.Replay.csproj`
- 创建：四个对应的 `tests/*.Tests/*.csproj`
- 创建：`tests/BattlegroundsVisionAgent.Core.Tests/SolutionSmokeTests.cs`

- [ ] **步骤 1：创建解决方案、项目和引用**

运行：

```powershell
dotnet new sln -n BattlegroundsVisionAgent
dotnet new wpf -n BattlegroundsVisionAgent.App -o src/BattlegroundsVisionAgent.App -f net9.0
dotnet new classlib -n BattlegroundsVisionAgent.Core -o src/BattlegroundsVisionAgent.Core -f net9.0
dotnet new classlib -n BattlegroundsVisionAgent.Vision -o src/BattlegroundsVisionAgent.Vision -f net9.0
dotnet new classlib -n BattlegroundsVisionAgent.Input -o src/BattlegroundsVisionAgent.Input -f net9.0
dotnet new classlib -n BattlegroundsVisionAgent.Replay -o src/BattlegroundsVisionAgent.Replay -f net9.0
dotnet new xunit -n BattlegroundsVisionAgent.Core.Tests -o tests/BattlegroundsVisionAgent.Core.Tests -f net9.0
dotnet new xunit -n BattlegroundsVisionAgent.Vision.Tests -o tests/BattlegroundsVisionAgent.Vision.Tests -f net9.0
dotnet new xunit -n BattlegroundsVisionAgent.Input.Tests -o tests/BattlegroundsVisionAgent.Input.Tests -f net9.0
dotnet new xunit -n BattlegroundsVisionAgent.Replay.Tests -o tests/BattlegroundsVisionAgent.Replay.Tests -f net9.0
```

将全部项目加入解决方案，并建立 `App → Core/Vision/Input`、`Vision → Core`、`Input → Core`、`Replay → Core/Vision` 以及测试项目到被测项目的引用。

- [ ] **步骤 2：写入严格编译配置**

创建 `Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

- [ ] **步骤 3：编写解决方案烟雾测试**

创建 `tests/BattlegroundsVisionAgent.Core.Tests/SolutionSmokeTests.cs`：

```csharp
namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void TestRunner_IsOperational() => Assert.True(true);
}
```

- [ ] **步骤 4：运行构建和测试**

运行：

```powershell
dotnet build BattlegroundsVisionAgent.sln -c Debug
dotnet test BattlegroundsVisionAgent.sln -c Debug --no-build
```

预期：构建 0 个警告、0 个错误；测试至少 1 个通过、0 个失败。

- [ ] **步骤 5：提交脚手架**

```powershell
git add BattlegroundsVisionAgent.sln Directory.Build.props src tests
git commit -m "build: 初始化桌面应用解决方案"
```

## 任务 2：定义不可变领域模型和设置持久化

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Core/Domain/GameSnapshot.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Domain/CardObservation.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Domain/CardRule.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Domain/AutomationAction.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Configuration/AppSettings.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Configuration/SettingsRepository.cs`
- 创建：`tests/BattlegroundsVisionAgent.Core.Tests/SettingsRepositoryTests.cs`

- [ ] **步骤 1：编写设置往返失败测试**

```csharp
using BattlegroundsVisionAgent.Core.Configuration;
using BattlegroundsVisionAgent.Core.Domain;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class SettingsRepositoryTests
{
    [Fact]
    public async Task SaveThenLoad_PreservesRuleAndSafetyDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bva-{Guid.NewGuid():N}.json");
        var repository = new SettingsRepository(path);
        var settings = AppSettings.CreateDefault() with
        {
            ReservedHandSlots = 2,
            MinimumGold = 2,
            Rules = [new CardRule("CARD_001", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.PlayThenSell, 1, true)]
        };

        await repository.SaveAsync(settings, CancellationToken.None);
        var loaded = await repository.LoadAsync(CancellationToken.None);

        Assert.Equal(settings, loaded);
        File.Delete(path);
    }
}
```

- [ ] **步骤 2：运行测试并确认失败**

运行：

```powershell
dotnet test tests/BattlegroundsVisionAgent.Core.Tests --filter SaveThenLoad_PreservesRuleAndSafetyDefaults
```

预期：FAIL，编译器报告 `AppSettings`、`CardRule` 或 `SettingsRepository` 不存在。

- [ ] **步骤 3：实现最小领域类型**

在 `CardRule.cs` 定义：

```csharp
namespace BattlegroundsVisionAgent.Core.Domain;

public enum CardDisposition { Keep, PlayThenSell }

public readonly record struct PurchaseLimit(int? Count)
{
    public static PurchaseLimit Unlimited => new(null);
    public static PurchaseLimit Exactly(int count) => count > 0
        ? new(count)
        : throw new ArgumentOutOfRangeException(nameof(count));
}

public sealed record CardRule(
    string CardId,
    PurchaseLimit PurchaseLimit,
    CardDisposition NormalAction,
    CardDisposition TripleAction,
    int DiscoverPriority,
    bool ProtectedOnBoard);
```

在 `CardObservation.cs` 和 `GameSnapshot.cs` 定义标准化坐标、区域、阶段和快照；所有集合使用 `IReadOnlyList<T>`，并包含 `LayoutVersion`、`Confidence`、`CapturedAt`。在 `AutomationAction.cs` 定义 `None`、`Refresh`、`Buy`、`Play`、`Sell`、`ChooseDiscover`、`PauseForUser` 和 `Stop` 动作记录。

- [ ] **步骤 4：实现 JSON 设置仓库**

`SettingsRepository` 使用 `System.Text.Json`、临时文件和 `File.Move(temp, target, true)` 原子替换。`AppSettings.CreateDefault()` 必须返回 `ReservedHandSlots = 2`、`PauseHotkey = "F7"`、`EmergencyStopHotkey = "F8"`、空规则列表和观察模式开启。

- [ ] **步骤 5：运行领域测试**

运行：

```powershell
dotnet test tests/BattlegroundsVisionAgent.Core.Tests
```

预期：全部通过。

- [ ] **步骤 6：提交领域模型**

```powershell
git add src/BattlegroundsVisionAgent.Core tests/BattlegroundsVisionAgent.Core.Tests
git commit -m "feat: 添加游戏快照与规则配置模型"
```

## 任务 3：实现纯函数规则状态机和动作验证

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Core/Rules/AutomationPlanner.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Rules/ActionVerifier.cs`
- 创建：`tests/BattlegroundsVisionAgent.Core.Tests/AutomationPlannerTests.cs`
- 创建：`tests/BattlegroundsVisionAgent.Core.Tests/ActionVerifierTests.cs`

- [ ] **步骤 1：编写优先级失败测试**

```csharp
using BattlegroundsVisionAgent.Core.Domain;
using BattlegroundsVisionAgent.Core.Rules;

namespace BattlegroundsVisionAgent.Core.Tests;

public sealed class AutomationPlannerTests
{
    [Fact]
    public void Plan_DiscoverPreemptsTripleAndShop()
    {
        var snapshot = SnapshotFactory.Shopping(
            discover: [SnapshotFactory.Card("CARD_B", zone: CardZone.Discover)],
            hand: [SnapshotFactory.Golden("CARD_A")],
            shop: [SnapshotFactory.Card("CARD_A", zone: CardZone.Shop)]);
        var settings = SnapshotFactory.Settings(
            new CardRule("CARD_B", PurchaseLimit.Unlimited, CardDisposition.Keep,
                CardDisposition.Keep, 1, false));

        var action = new AutomationPlanner().Plan(snapshot, settings);

        var choose = Assert.IsType<ChooseDiscoverAction>(action);
        Assert.Equal("CARD_B", choose.CardId);
    }

    [Fact]
    public void Plan_FullBoardRequestsUserBeforeTemporarySale()
    {
        var snapshot = SnapshotFactory.FullBoardWithGoldenInHand("CARD_A");
        var action = new AutomationPlanner().Plan(snapshot, SnapshotFactory.Settings());
        Assert.IsType<PauseForUserAction>(action);
    }
}
```

同时在测试项目创建 `SnapshotFactory.cs`，集中构建确定性的商店、手牌、场面和发现快照，禁止各测试复制大段构造代码。

- [ ] **步骤 2：运行测试并确认失败**

```powershell
dotnet test tests/BattlegroundsVisionAgent.Core.Tests --filter "AutomationPlannerTests"
```

预期：FAIL，`AutomationPlanner` 不存在。

- [ ] **步骤 3：按固定优先级实现最小 Planner**

`Plan` 必须按顺序返回首个合法动作：停止条件、发现、三连/奖励、空间清理、购买、刷新、等待。发现候选按 `DiscoverPriority` 升序选择；没有规则命中时使用注入的 `IRandomSelector`，使测试可固定选择结果。

```csharp
public AutomationAction Plan(GameSnapshot snapshot, AppSettings settings)
{
    if (!snapshot.IsActionable) return new StopAction("scene-not-actionable");
    if (snapshot.DiscoverOptions.Count > 0) return PlanDiscover(snapshot, settings);
    if (snapshot.HasPendingTripleReward) return PlanTriple(snapshot, settings);
    if (snapshot.FreeHandSlots < settings.ReservedHandSlots) return PlanSpaceRecovery(snapshot, settings);
    var purchase = PlanPurchase(snapshot, settings);
    return purchase ?? PlanRefresh(snapshot, settings) ?? new NoneAction("waiting");
}
```

- [ ] **步骤 4：为布局版本和动作结果编写失败测试**

`ActionVerifierTests` 至少覆盖：布局版本变化使旧动作失效、购买后目标从商店消失且进入手牌才成功、卖出后目标消失才成功、刷新后商店指纹变化才成功。

- [ ] **步骤 5：实现 ActionVerifier 并运行测试**

运行：

```powershell
dotnet test tests/BattlegroundsVisionAgent.Core.Tests
```

预期：状态机和验证器测试全部通过。

- [ ] **步骤 6：提交规则内核**

```powershell
git add src/BattlegroundsVisionAgent.Core/Rules tests/BattlegroundsVisionAgent.Core.Tests
git commit -m "feat: 实现安全规则状态机"
```

## 任务 4：实现 16:9 坐标、锚点布局和动画稳定检测

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Vision/Geometry/NormalizedRect.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Geometry/LayoutLocator.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Geometry/FrameStabilityDetector.cs`
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/NormalizedRectTests.cs`
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/FrameStabilityDetectorTests.cs`
- 创建：`testdata/synthetic/layout-1920x1080.png`

- [ ] **步骤 1：编写四分辨率坐标失败测试**

```csharp
[Theory]
[InlineData(1280, 720, 128, 72, 256, 144)]
[InlineData(1600, 900, 160, 90, 320, 180)]
[InlineData(1920, 1080, 192, 108, 384, 216)]
[InlineData(2560, 1440, 256, 144, 512, 288)]
public void ToPixels_ScalesAcrossSupported16By9(
    int width, int height, int x, int y, int w, int h)
{
    var rect = new NormalizedRect(0.10, 0.10, 0.20, 0.20);
    Assert.Equal(new PixelRect(x, y, w, h), rect.ToPixels(width, height));
}
```

- [ ] **步骤 2：运行测试确认失败，再实现 NormalizedRect**

实现时拒绝小于等于零的画面尺寸，并验证归一化矩形完全落在 0–1 范围。

- [ ] **步骤 3：编写稳定检测失败测试**

构造三张合成灰度图：前两张差异超过阈值，后两张相同。断言只有连续两帧平均绝对差低于 2.0 且持续至少 120 ms 时返回稳定。

- [ ] **步骤 4：实现稳定检测和布局定位**

`LayoutLocator` 输入窗口图像和锚点模板，输出带版本号的 `GameLayout`。必须验证 16:9 宽高比容差不超过 1%，锚点置信度达到配置阈值，商店/手牌/场面区域不重叠且在窗口内；否则返回失败结果而非异常坐标。

- [ ] **步骤 5：运行视觉几何测试**

```powershell
dotnet test tests/BattlegroundsVisionAgent.Vision.Tests --filter "NormalizedRectTests|FrameStabilityDetectorTests"
```

预期：全部通过。

- [ ] **步骤 6：提交布局基础**

```powershell
git add src/BattlegroundsVisionAgent.Vision/Geometry tests/BattlegroundsVisionAgent.Vision.Tests testdata/synthetic
git commit -m "feat: 添加十六比九布局与稳定检测"
```

## 任务 5：实现卡库、卡图识别和快照聚合

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Vision/Catalog/CardCatalog.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Catalog/CardCatalogUpdater.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Catalog/CardFeatureStore.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/CardMatcher.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/DigitRecognizer.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/SceneRecognizer.cs`
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/SnapshotRecognizer.cs`
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/CardMatcherTests.cs`
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/SnapshotRecognizerTests.cs`

- [ ] **步骤 1：添加视觉与 SQLite 依赖**

```powershell
dotnet add src/BattlegroundsVisionAgent.Vision package OpenCvSharp4.Windows
dotnet add src/BattlegroundsVisionAgent.Vision package Microsoft.Data.Sqlite
dotnet add tests/BattlegroundsVisionAgent.Vision.Tests package OpenCvSharp4.Windows
```

- [ ] **步骤 2：编写匹配器拒绝未知牌测试**

```csharp
[Fact]
public void Match_ReturnsUnknown_WhenBestCandidateIsBelowThreshold()
{
    using var query = SyntheticImages.Noise(160, 220, seed: 42);
    var store = InMemoryFeatureStore.WithCard("CARD_A", SyntheticImages.SolidCard());
    var matcher = new CardMatcher(store, minimumConfidence: 0.92);

    var result = matcher.Match(query);

    Assert.False(result.IsKnown);
    Assert.Null(result.CardId);
}
```

- [ ] **步骤 3：运行测试确认失败并实现两阶段匹配**

第一阶段用感知哈希取前 5 个候选；第二阶段用 ORB 关键点、Hamming 距离和几何内点比例复核。最终置信度低于阈值时固定返回 unknown。普通和金色模板映射到同一 `CardId`，并单独输出 `IsGolden`。

- [ ] **步骤 4：实现 SQLite 特征仓库与原子更新**

数据库至少包含 `cards(card_id, name_zh_cn, tier, image_path)`、`features(card_id, variant, phash, descriptor)` 和 `catalog_meta(version, updated_at)`。更新器下载到 `catalog.staging`，验证清单、图片可解码和所有 card_id 唯一后，用同卷目录重命名替换；失败时删除暂存内容并保留当前库。

- [ ] **步骤 5：编写快照聚合测试**

使用注入的假布局器、假匹配器和假数字识别器，断言购物快照包含金币、等级、商店卡牌、手牌空位、场面数量、布局版本和逐项置信度；任一关键锚点失败时 `IsActionable` 为 false。

- [ ] **步骤 6：运行视觉测试**

```powershell
dotnet test tests/BattlegroundsVisionAgent.Vision.Tests
```

预期：全部通过，并包含 unknown 分支。

- [ ] **步骤 7：提交识别纵向切片**

```powershell
git add src/BattlegroundsVisionAgent.Vision tests/BattlegroundsVisionAgent.Vision.Tests
git commit -m "feat: 实现本地卡库与画面识别"
```

## 任务 6：实现规则编辑器、状态悬浮条和观察模式

**文件：**
- 修改：`src/BattlegroundsVisionAgent.App/App.xaml`
- 修改：`src/BattlegroundsVisionAgent.App/App.xaml.cs`
- 修改：`src/BattlegroundsVisionAgent.App/MainWindow.xaml`
- 修改：`src/BattlegroundsVisionAgent.App/MainWindow.xaml.cs`
- 创建：`src/BattlegroundsVisionAgent.App/ViewModels/MainViewModel.cs`
- 创建：`src/BattlegroundsVisionAgent.App/Views/StatusOverlayWindow.xaml`
- 创建：`src/BattlegroundsVisionAgent.App/Views/SellConfirmationWindow.xaml`
- 创建：`src/BattlegroundsVisionAgent.Replay/ActionPlanRecorder.cs`
- 创建：`tests/BattlegroundsVisionAgent.Replay.Tests/ObservationModeTests.cs`

- [ ] **步骤 1：添加 MVVM 依赖并编写观察模式失败测试**

```powershell
dotnet add src/BattlegroundsVisionAgent.App package CommunityToolkit.Mvvm
```

```csharp
[Fact]
public async Task ObservationMode_RecordsPlanWithoutSendingInput()
{
    var input = new SpyInputExecutor();
    var recorder = new ActionPlanRecorder();
    var coordinator = TestCoordinator.Create(input, recorder, observationMode: true);

    await coordinator.TickAsync(SnapshotFactory.ShopWithTarget("CARD_A"), CancellationToken.None);

    Assert.Empty(input.ExecutedActions);
    Assert.IsType<BuyAction>(Assert.Single(recorder.Actions));
}
```

- [ ] **步骤 2：运行测试确认失败并实现 ActionPlanRecorder**

观察模式必须调用真实 Planner 并生成同样的动作计划，只把执行器替换为记录器；禁止在 UI 层复制判断逻辑。

- [ ] **步骤 3：实现“列表 + 详情”主界面**

主窗口包含：卡库搜索、目标牌勾选、购买上限、普通动作、三连动作、发现优先级、场上保护、金币下限、预留手牌位、观察/执行模式切换和开始按钮。无规则或卡库未加载时禁用真实执行按钮。

- [ ] **步骤 4：实现悬浮条和确认弹窗**

悬浮条只显示状态、最近计划、暂停键和停止键，不拦截游戏区域鼠标。`SellConfirmationWindow` 必须返回明确枚举 `ConfirmSell` 或 `KeepStill`；窗口超时、关闭或失焦统一返回 `KeepStill`。

- [ ] **步骤 5：运行 UI 逻辑与观察模式测试**

```powershell
dotnet test tests/BattlegroundsVisionAgent.Replay.Tests --filter ObservationModeTests
dotnet build src/BattlegroundsVisionAgent.App -c Debug
```

预期：测试通过，WPF 项目构建 0 个警告、0 个错误。

- [ ] **步骤 6：人工观察模式检查**

运行 `dotnet run --project src/BattlegroundsVisionAgent.App`，加载合成画面，确认识别框与拟执行动作可见，且 Windows 输入监控工具未收到程序生成的键鼠事件。

- [ ] **步骤 7：提交观察模式**

```powershell
git add src/BattlegroundsVisionAgent.App src/BattlegroundsVisionAgent.Replay tests/BattlegroundsVisionAgent.Replay.Tests
git commit -m "feat: 添加规则界面与安全观察模式"
```

## 任务 7：实现快捷键、鼠标后端和紧急停止门禁

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Input/IInputExecutor.cs`
- 创建：`src/BattlegroundsVisionAgent.Input/WindowsInputBackend.cs`
- 创建：`src/BattlegroundsVisionAgent.Input/SafeInputExecutor.cs`
- 创建：`src/BattlegroundsVisionAgent.Input/GlobalHotkeyService.cs`
- 创建：`src/BattlegroundsVisionAgent.Input/ActionBindingResolver.cs`
- 创建：`tests/BattlegroundsVisionAgent.Input.Tests/SafeInputExecutorTests.cs`
- 创建：`tests/BattlegroundsVisionAgent.Input.Tests/ActionBindingResolverTests.cs`

- [ ] **步骤 1：编写停止和旧布局失败测试**

```csharp
[Fact]
public async Task Execute_DoesNotSendInput_WhenEmergencyStopped()
{
    var backend = new SpyWindowsInputBackend();
    var runState = new RunState();
    runState.EmergencyStop();
    var executor = new SafeInputExecutor(backend, runState, new AlwaysFocusedGameWindow());

    var result = await executor.ExecuteAsync(
        new RefreshAction(layoutVersion: 7), currentLayoutVersion: 7, CancellationToken.None);

    Assert.False(result.Sent);
    Assert.Empty(backend.Events);
}

[Fact]
public async Task Execute_DoesNotSendInput_WhenLayoutVersionChanged()
{
    var backend = new SpyWindowsInputBackend();
    var executor = TestInputExecutor.Create(backend);
    var result = await executor.ExecuteAsync(
        new SellAction("CARD_A", layoutVersion: 7), currentLayoutVersion: 8, CancellationToken.None);
    Assert.False(result.Sent);
    Assert.Empty(backend.Events);
}
```

- [ ] **步骤 2：运行测试确认失败并实现 SafeInputExecutor**

执行门禁顺序固定为：运行状态、取消令牌、炉石前台窗口、购物阶段、布局版本、动作绑定。任一失败均返回未发送结果及机器可读原因。

- [ ] **步骤 3：实现绑定解析器**

若动作存在经过测试的快捷键绑定，返回 `KeyboardBinding`；否则返回 `MouseBinding`。卖出快捷键失败不得自动回退鼠标，必须返回 `RequiresUserConfirmation`。刷新等低风险动作可由设置允许回退一次。

- [ ] **步骤 4：实现 Win32 输入和全局热键**

`WindowsInputBackend` 封装 `SendInput`，并在异常或 F8 时释放所有由程序按下的键和鼠标按钮。`GlobalHotkeyService` 使用 `RegisterHotKey` 注册 F7/F8，应用退出时在 `finally` 中调用 `UnregisterHotKey`。

- [ ] **步骤 5：运行输入测试**

```powershell
dotnet test tests/BattlegroundsVisionAgent.Input.Tests
```

预期：停止、暂停、失焦、旧布局、快捷键失败和鼠标回退测试全部通过；测试使用 spy 后端，不向真实系统发送输入。

- [ ] **步骤 6：提交输入安全层**

```powershell
git add src/BattlegroundsVisionAgent.Input tests/BattlegroundsVisionAgent.Input.Tests
git commit -m "feat: 添加受保护的键鼠执行与紧急停止"
```

## 任务 8：组装闭环、回放验收和 Windows 打包

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Core/Runtime/AutomationCoordinator.cs`
- 创建：`src/BattlegroundsVisionAgent.Core/Runtime/RunState.cs`
- 创建：`src/BattlegroundsVisionAgent.Replay/ReplayFrameSource.cs`
- 创建：`tests/BattlegroundsVisionAgent.Replay.Tests/ReplayScenarioTests.cs`
- 创建：`testdata/manifests/triple-discover-sell.json`
- 创建：`testdata/manifests/board-shift.json`
- 创建：`scripts/verify.ps1`
- 创建：`scripts/package.ps1`
- 修改：`README.md`

- [ ] **步骤 1：编写完整流程回放失败测试**

```csharp
[Fact]
public async Task TripleDiscoverSell_ReplansAfterEverySceneChange()
{
    var scenario = ReplayScenario.Load("testdata/manifests/triple-discover-sell.json");
    var result = await ReplayRunner.RunAsync(scenario, CancellationToken.None);

    Assert.Equal(
        ["Buy:CARD_A", "Play:CARD_A:Golden", "Play:TRIPLE_REWARD",
         "ChooseDiscover:CARD_B", "Sell:CARD_A"],
        result.ActionLabels);
    Assert.All(result.Actions.Zip(result.LayoutVersions.Skip(1)),
        pair => Assert.True(pair.Second > pair.First.LayoutVersion));
}

[Fact]
public async Task BoardShift_NeverUsesCoordinatesFromPreviousLayout()
{
    var scenario = ReplayScenario.Load("testdata/manifests/board-shift.json");
    var result = await ReplayRunner.RunAsync(scenario, CancellationToken.None);
    Assert.DoesNotContain(result.Actions, action => action.UsedStaleLayout);
}
```

- [ ] **步骤 2：运行测试确认失败并实现 Coordinator**

`AutomationCoordinator` 每个 tick 执行：等待稳定帧、识别快照、废弃旧计划、调用 Planner、发布 UI 状态；观察模式记录计划，执行模式通过 `SafeInputExecutor` 发送一次动作；随后强制获取新快照并用 `ActionVerifier` 验证。低风险动作最多恢复两次，高风险卖出失败立即请求人工。

- [ ] **步骤 3：实现回放清单格式和两个固定场景**

清单明确列出每帧文件、时间、期望场景、布局版本、识别对象和期望动作。`triple-discover-sell.json` 覆盖完整三连奖励链；`board-shift.json` 覆盖卖出后剩余随从重新排列。原始用户录屏不进入 Git，只提交脱敏裁剪或合成图。

- [ ] **步骤 4：创建一键验证脚本**

`scripts/verify.ps1`：

```powershell
$ErrorActionPreference = 'Stop'
dotnet restore BattlegroundsVisionAgent.sln
dotnet build BattlegroundsVisionAgent.sln -c Release --no-restore
dotnet test BattlegroundsVisionAgent.sln -c Release --no-build
git diff --check
```

- [ ] **步骤 5：创建自包含打包脚本**

`scripts/package.ps1`：

```powershell
$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot '..\dist\win-x64'
dotnet publish "$PSScriptRoot\..\src\BattlegroundsVisionAgent.App" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $output
```

- [ ] **步骤 6：补充 README 使用与风险说明**

写明观察模式优先流程、卡库更新、规则字段、快捷键测试、F7/F8、日志位置、受支持分辨率、最低配置和服务条款风险。不得描述规避检测或隐藏自动化的方法。

- [ ] **步骤 7：运行完整验证**

```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

预期：restore/build/test 全部成功，`git diff --check` 无输出，`dist/win-x64/BattlegroundsVisionAgent.App.exe` 存在。

- [ ] **步骤 8：人工端到端检查**

先在四种 16:9 合成/回放画面运行观察模式，确认稳定后再在可控测试环境启用输入。逐项验证：金币下限、两格手牌预留、发现优先级、随机兜底、场满确认超时不动、卖出后重定位、F7 暂停和 F8 停止。任何失败都保存对应帧并回到相关单元或回放测试，不直接调低安全阈值。

- [ ] **步骤 9：提交首版闭环**

```powershell
git add src tests testdata scripts README.md
git commit -m "feat: 完成酒馆视觉规则自动化首版闭环"
```

## 最终完成条件

- `scripts/verify.ps1` 在干净检出中退出码为 0。
- 所有输入测试使用 spy 后端，自动测试期间无真实键鼠事件。
- 四种 16:9 回放均在 500 ms 性能预算内产生计划。
- 观察模式和执行模式使用同一个 Planner。
- F8 能从任意状态清空队列并阻止后续输入。
- 三连、奖励、发现、新牌规则和卖出完成完整闭环。
- 场满临时出售必须确认，超时保持不动。
- 卖出后所有旧坐标失效并重新定位。
