# 离线日志质量筛选与场景回归集实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 superpowers:executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 把真实日志自动整理成可审计的质量 manifest 和安全回归测试，并确保新版本卡库导入后能立即生成可用的商店缩略图特征。

**架构：** 在 Vision 项目中新增纯离线的帧质量分析器和日志清单生成器；OfflineVisionProbe 只负责参数解析和报告输出。测试项目通过少量代表帧验证场景安全边界，通过临时目录验证新卡库导入后的特征覆盖，不把大图复制到测试目录。

**技术栈：** .NET 9、OpenCvSharp、System.Text.Json、xUnit、现有 `VisionRecognitionPipeline`/`CardCatalogUpdater`。

---

## 文件清单

- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/FrameQualityAnalyzer.cs`，计算尺寸、亮度、纹理和感知哈希并给出质量结论。
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/LogCurationManifest.cs`，定义 manifest DTO、扫描器和确定性 JSON 输出。
- 修改：`tools/OfflineVisionProbe/Program.cs`，增加 `--curate-logs` 和 `--validate-manifest` 批量命令。
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/FrameQualityAnalyzerTests.cs`，覆盖白屏、黑屏、正常帧和坏图。
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/LogCurationManifestTests.cs`，覆盖路径、场景标签、重复帧和确定性输出。
- 修改：`tests/BattlegroundsVisionAgent.Vision.Tests/CatalogPackageTests.cs`，验证新卡导入后普通特征自动生成且缩略图匹配器可读取。
- 创建：`tests/fixtures/log-quality/live-logs.manifest.json`，保存当前日志的轻量清单，不复制图片。
- 修改：`README.md`，记录离线筛选、manifest 校验和新版本卡库快速验证命令。
- 修改：`docs/PROJECT_PROGRESS.md`，记录当前样本统计、垃圾样本和后续使用方式。

### 任务 1：实现帧质量分析器

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/FrameQualityAnalyzer.cs`
- 测试：`tests/BattlegroundsVisionAgent.Vision.Tests/FrameQualityAnalyzerTests.cs`

- [ ] **步骤 1：编写失败的测试**

  用内存 `Mat` 构造 1920×1080 的纯白、纯黑、纹理图，并断言 `Analyze` 返回对应的 `Garbage` 或 `Good`；对不存在文件断言 `DecodeFailed`。同时断言感知哈希相同的图能被识别为重复候选。

- [ ] **步骤 2：运行测试验证失败**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~FrameQualityAnalyzerTests`

  预期：编译失败，提示 `FrameQualityAnalyzer` 尚不存在。

- [ ] **步骤 3：编写最少实现代码**

  新增不可变结果类型，固定使用 32×18 灰度采样计算均值、标准差、过亮/过暗比例和边缘比例；使用现有 `PerceptualHash.Create` 生成哈希。实现默认规则：解码失败、尺寸小于 32×32、过亮比例 ≥ 0.95 且标准差 < 12、过暗比例 ≥ 0.50 且标准差 < 20 时为 `Garbage`，其余为 `Good` 或 `Review`。所有阈值由 `FrameQualityPolicy` 常量集中定义。

- [ ] **步骤 4：运行测试验证通过**

  运行同上，预期所有质量分析器测试通过。

- [ ] **步骤 5：Commit**

  `git add src/BattlegroundsVisionAgent.Vision/Recognition/FrameQualityAnalyzer.cs tests/BattlegroundsVisionAgent.Vision.Tests/FrameQualityAnalyzerTests.cs`

  `git commit -m "功能：增加离线截图质量分析器"`

### 任务 2：实现日志 manifest 扫描器

**文件：**
- 创建：`src/BattlegroundsVisionAgent.Vision/Recognition/LogCurationManifest.cs`
- 测试：`tests/BattlegroundsVisionAgent.Vision.Tests/LogCurationManifestTests.cs`

- [ ] **步骤 1：编写失败的测试**

  测试扫描临时目录中的 Shopping、Combat、Discover 和 Unknown 文件；断言文件名后缀解析为 `sourceLabel`，质量分析结果写入 `metrics`，Combat 的 `expectedActionable` 为 false。再放入两份相同图片，断言后一个样本为 `duplicate`。最后连续生成两次 JSON，去掉可选时间字段后字节完全一致。

- [ ] **步骤 2：运行测试验证失败**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~LogCurationManifestTests`

  预期：编译失败，提示 manifest 扫描器类型不存在。

- [ ] **步骤 3：编写最少实现代码**

  新增 `LogCurationScanner.Scan(root, includeGlobs)` 和 `LogCurationManifest.Save(path)`；使用稳定的相对路径排序，识别 `Shopping`/`Discover`/`Combat`/`Unknown` 后缀，质量为 Garbage 时强制 `include=false`，Combat/Discover/Unknown 强制 `expectedActionable=false`。对损坏图片记录 `reason` 后继续扫描；manifest 中保存源提交、策略版本和全部指标。

- [ ] **步骤 4：运行测试验证通过**

  运行同上，预期所有 manifest 测试通过。

- [ ] **步骤 5：Commit**

  `git add src/BattlegroundsVisionAgent.Vision/Recognition/LogCurationManifest.cs tests/BattlegroundsVisionAgent.Vision.Tests/LogCurationManifestTests.cs`

  `git commit -m "功能：增加日志清单扫描与质量标注"`

### 任务 3：接入 OfflineVisionProbe 批量命令

**文件：**
- 修改：`tools/OfflineVisionProbe/Program.cs`
- 测试：`tests/BattlegroundsVisionAgent.Vision.Tests/LogCurationManifestTests.cs` 增加参数/输出格式断言（调用共享服务，不启动子进程）。

- [ ] **步骤 1：编写失败的测试**

  为 `LogCurationReportFormatter` 写测试，断言输出包含五类计数、垃圾原因计数、待复核数量，并按路径排序。

- [ ] **步骤 2：运行测试验证失败**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~LogCurationManifestTests`

  预期：因格式化器不存在而失败。

- [ ] **步骤 3：编写最少实现代码**

  把参数解析扩展为 `--curate-logs <logsDir> --manifest <manifestPath> [--source-commit <sha>]` 和 `--validate-manifest <manifestPath> [--repo-root <path>]`；命令只读本地文件，不加载炉石窗口。报告使用固定文化设置和固定排序，退出码为 0（无错误）、1（存在 Garbage/Review 或路径问题）、2（参数错误）。

- [ ] **步骤 4：运行测试验证通过**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~LogCurationManifestTests`；再运行 `tools\dotnet\dotnet.exe run --project tools\OfflineVisionProbe -- --help`，确认帮助文本列出两个命令。

- [ ] **步骤 5：Commit**

  `git add tools/OfflineVisionProbe/Program.cs tests/BattlegroundsVisionAgent.Vision.Tests/LogCurationManifestTests.cs`

  `git commit -m "功能：为离线探针增加日志筛选命令"`

### 任务 4：生成真实日志 manifest 并加入场景回归

**文件：**
- 创建：`tests/fixtures/log-quality/live-logs.manifest.json`
- 创建：`tests/BattlegroundsVisionAgent.Vision.Tests/LogQualityRegressionTests.cs`
- 修改：`tests/BattlegroundsVisionAgent.Vision.Tests/BattlegroundsVisionAgent.Vision.Tests.csproj`（仅在需要时补充 fixture 复制设置）

- [ ] **步骤 1：编写失败的回归测试**

  从 manifest 选择代表性 Shopping、Discover、Combat、Unknown、Garbage 样本；调用现有 `VisionRecognitionPipeline` 或 `SceneRecognizer`，断言 Combat/Discover/Unknown/Garbage 不可操作，Shopping 不得因非购物场景规则被放行；断言已知白屏和连接过渡帧的 `include=false`。

- [ ] **步骤 2：运行测试验证失败**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~LogQualityRegressionTests`

  预期：初版 manifest 不存在或缺少样本时失败，确保测试不是空跑。

- [ ] **步骤 3：生成并审核 manifest**

  运行：`tools\dotnet\dotnet.exe run --project tools\OfflineVisionProbe -- --curate-logs logs --manifest tests\fixtures\log-quality\live-logs.manifest.json --source-commit 2dd06aa5bf68e5b866183e63a08378fc42de8f42`。

  人工核对白屏 `20260907-203423-650-Unknown.png`、黑屏连接帧 `20260907-213143-760-Unknown.png`、裁切 crop 和法术 crop 的结论；必要时仅在 manifest 中写入明确的人工复核理由。

- [ ] **步骤 4：运行回归测试验证通过**

  运行同上，预期代表性场景和安全断言全部通过。

- [ ] **步骤 5：Commit**

  `git add tests/fixtures/log-quality/live-logs.manifest.json tests/BattlegroundsVisionAgent.Vision.Tests/LogQualityRegressionTests.cs`

  `git commit -m "测试：加入真实日志场景安全回归集"`

### 任务 5：验证新版本卡库可立即识别

**文件：**
- 修改：`tests/BattlegroundsVisionAgent.Vision.Tests/CatalogPackageTests.cs`
- 视测试结果修改：`src/BattlegroundsVisionAgent.Vision/Catalog/CardCatalogUpdater.cs`
- 视测试结果修改：`README.md`

- [ ] **步骤 1：编写失败的测试**

  在现有版本化 package 测试中加入新卡 `CARD_B` 的特征覆盖断言：更新完成后 `CardFeatureStore.GetAll()` 包含每张普通卡；使用同一张新卡图构造 screen thumbnail，`CardThumbnailMatcher` 能返回 `CARD_B`。另外加入“同卡 ID 图片发生变化时重新生成普通特征”的测试，防止新版本图片更新仍使用旧特征。

- [ ] **步骤 2：运行测试验证失败**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj --filter FullyQualifiedName~CatalogPackageTests`

  预期：新卡特征覆盖或图片更新测试失败，明确指出 updater 只补缺失特征。

- [ ] **步骤 3：编写最少实现代码**

  让 package 更新在校验通过后按当前 package 图片为每个普通卡重建/覆盖特征，保持原子替换；更新 README 说明“导入新版本包后无需重新编译，下一次识别自动使用新卡特征”。若重建耗时过长，再以图片内容哈希为键做确定性缓存，不改变结果。

- [ ] **步骤 4：运行测试验证通过**

  运行同上，预期所有 package 测试通过，并额外运行缩略图匹配相关测试。

- [ ] **步骤 5：Commit**

  `git add src/BattlegroundsVisionAgent.Vision/Catalog/CardCatalogUpdater.cs tests/BattlegroundsVisionAgent.Vision.Tests/CatalogPackageTests.cs README.md`

  `git commit -m "功能：确保新版本卡库自动生成识别特征"`

### 任务 6：全量验证和进度文档

**文件：**
- 修改：`README.md`
- 修改：`docs/PROJECT_PROGRESS.md`

- [ ] **步骤 1：运行定向测试**

  运行：`tools\dotnet\dotnet.exe test tests\BattlegroundsVisionAgent.Vision.Tests\BattlegroundsVisionAgent.Vision.Tests.csproj`

- [ ] **步骤 2：运行全量测试和构建**

  运行：`tools\dotnet\dotnet.exe test BattlegroundsVisionAgent.sln --no-restore`；`tools\dotnet\dotnet.exe build BattlegroundsVisionAgent.sln --no-restore`。

- [ ] **步骤 3：运行真实日志批量检查**

  运行 manifest 生成和校验命令，记录 Shopping/Discover/Combat/Unknown/Garbage 数量、垃圾原因和待复核项；确认不启动 EXE、不联网。

- [ ] **步骤 4：更新文档**

  在 README 增加新命令示例和新卡库更新流程，在 PROJECT_PROGRESS 记录实际测试数字、已识别的战斗/发现样本和下一步操作闭环。

- [ ] **步骤 5：Commit**

  `git add README.md docs/PROJECT_PROGRESS.md`

  `git commit -m "文档：补充离线日志筛选与新卡验证流程"`

