# 《班机》架构契约（团队共享，改动须 Lead 批准）

> 产品名：**班机**（Banji）。Windows 课堂管理桌面宠物应用。
> 依据：`C:\Users\qmy\Desktop\《班机》开发文档.docx`。
> 本文件是队友之间唯一的接口契约。**不要修改不属于自己写作用域的文件**；需要新增共享契约时先找 Lead。

## 1. 技术栈与目录

- C# / .NET 8 / WPF，自包含发布 win-x64（`self-contained`，不依赖客户机 .NET）。
- 本地 SDK：`D:\Program\workshop\banji\.dotnet\dotnet.exe`（不要用系统 dotnet，系统没有）。
- 解决方案：`Banji.sln`；主工程 `src/Banji/Banji.csproj`；测试 `src/Banji.Tests/`（控制台测试运行器，不依赖 NuGet）；安装器 `src/Banji.Installer/`。
- 运行数据目录：`%APPDATA%\Banji\`（settings.json / rosters.json / timetable.json / overrides.json / logs）。
- 字体：`src/Banji/Assets/Fonts/HarmonyOS_Sans_SC_*.ttf`（已从用户提供的 zip 解压），嵌入资源 + 安装器安装到系统。
- 音频：`src/Banji/Assets/Audio/*.wav`（清醒一下吧，自合成避免版权问题）。
- 图标：`assets/icons/*.ico|png`，最终产物 `dist/`。

### 构建命令（所有人统一）

```powershell
cd D:\Program\workshop\banji
.\.dotnet\dotnet.exe build Banji.sln -c Debug
.\.dotnet\dotnet.exe run --project src\Banji.Tests\Banji.Tests.csproj -c Debug
.\.dotnet\dotnet.exe run --project src\Banji\Banji.csproj -c Debug
```

如果 `build` 因为别人的文件暂时失败：只修自己作用域内的错误，不要改别人的文件；在 `.\logs\` 无日志需求时直接重试。
UI 截图验证脚本：`tools\screenshot.ps1`（Lead 提供）。

## 2. 文件所有权（写作用域）

| 负责人 | 写作用域 |
|---|---|
| Lead | `Banji.sln`, `src/Banji/Banji.csproj`, `src/Banji/App.xaml*`, `AssemblyInfo.cs`, `Core/Models/**`, `Core/Settings/**`, `Core/Abstractions/**`, `Core/IO/**`, `Core/Infrastructure/**`, `Resources/**`, `Core/Boot/AppServices.cs`, `Core/Boot/ClassClock*.cs` 之外见下, `tools/**`, 根目录 md |
| `core-roster` | `src/Banji/Core/Roster/**（命名空间 Banji.Core.Rosters）`, `src/Banji/Core/Picker/**`, `src/Banji/Views/RosterWindow.*`, `src/Banji/Views/RosterPickerFlyout.*`, `src/Banji/Views/RosterImportWindow.*`, `src/Banji/Views/ManualRosterDialog.*`, `src/Banji.Tests/Roster*.cs`, `src/Banji.Tests/Picker*.cs` |
| `core-schedule` | `src/Banji/Core/Timetable/**`, `src/Banji/Core/Schedule/**`（含 ClassClock）, `src/Banji/Core/Notices/**`, `src/Banji/Views/TimetableWindow.*`, `src/Banji/Views/TimetableImportWindow.*`, `src/Banji/Views/PeriodSettingsWindow.*`, `src/Banji/Views/FullscreenNoticeWindow.*`, `src/Banji.Tests/Schedule*.cs`, `src/Banji.Tests/Timetable*.cs` |
| `pet-ui` | `src/Banji/Views/PetWindow.*`, `src/Banji/Views/BubbleWindow.*`, `src/Banji/Views/MenuFlyout.*`, `src/Banji/Controls/Pet/**`, `src/Banji/Core/Quips/**`, `src/Banji.Tests/Quip*.cs` |
| `features-ui` | `src/Banji/Core/Timers/**`, `src/Banji/Core/Audio/**`, `src/Banji/Controls/Toolbar/**`, `src/Banji/Views/TimerDialog.*`, `src/Banji/Views/TimerTimeoutWindow.*`, `src/Banji/Views/SettingsWindow.*`, `src/Banji/Views/DashboardWindow.*`, `src/Banji.Tests/Timer*.cs` |
| `packaging` | `src/Banji.Installer/**`, `build/**`, `assets/**`, `README.md`, `dist/**`, `src/Banji.Tests/Installer*.cs` |

Lead 会先放好**可编译的占位窗口/服务**（stub），对应负责人直接**整体覆盖**该文件即可；stub 只是保证其他人能编译。新增文件自由，但不要改别人的文件。

## 3. 进程/窗口模型

- 单实例：`Mutex("Banji.SingleInstance")`，第二实例用命名管道 `Banji.Ipc` 向首实例发命令后退出。
- 宠物窗 `PetWindow`：`WindowStyle=None, AllowsTransparency=True, Background=Transparent, Topmost=True, ShowInTaskbar=False, ShowActivated=False`，吸附屏幕右侧、任务栏上方，可拖动。
- 气泡窗 `BubbleWindow`：独立置顶窗，`Topmost` 且每次显示时重新 `SetWindowPos(HWND_TOPMOST)`，保证最高层；只用于对话气泡/抽签结果/计时显示。
- 全屏提示 `FullscreenNoticeWindow`：覆盖主屏、置顶、不抢焦点；上课提示带醒目背景，下课提示仅文字。
- 计时器超时 `TimerTimeoutWindow`：全屏高斯模糊 + 居中“计时器超时” + 关闭按钮，15 秒自动关。
- 所有置顶窗口显示时需要用 `NativeMethods.SetWindowPos(HWND_TOPMOST)` + `ShowActivated=false` 重新压顶，避免被 PPT 放映抢焦点后掉层。
- 高 DPI：`app.manifest` PerMonitorV2；XAML 用设备无关单位（96dpi）。
- 分辨率无关：一律按 `SystemParameters.PrimaryScreenWidth/Height` 与工作区 `SystemParameters.WorkArea` 定位，不写死 1440×960。

## 4. 核心契约（`Banji.Core.Abstractions`，Lead 冻结）

所有服务由 `AppServices.Current` 暴露（见 `Core/Boot/AppServices.cs`）：

```csharp
ISettingsService Settings;      // AppSettings 读写、Changed 事件、Update(mutate)、ResetToDefaults()
IRosterService Rosters;         // 花名册 CRUD、导入预览/导入、Active
IPickService Picker;            // 抽签（约束公平）、RecentPicks、Picked 事件、ResetSession()
ITimetableService Timetable;    // 课表导入/编辑/临时换课/课堂时段/午休
IScheduleService Schedule;      // SnapshotAt(now) -> ScheduleSnapshot（纯函数，可注入时间）
IClassClock Clock;              // 250ms Tick + 上下课/放学 Transition 事件
ITimerService Timers;           // 倒计时/正计时，Tick 事件
IAudioService Audio;            // PlayRandomSting()（清醒一下）
INotificationService Notices;   // 全屏上下课提示/计时器超时
```

窗口打开统一走 `AppServices.Current.ShowSettings()/ShowDashboard()/ShowRosterManager()/ShowTimetableEditor()/ShowTimerDialog(TimerKind)`。
退出走 `AppServices.Current.Shutdown()`。

### 抽签规则（必须单测覆盖）

1. 课堂时长 `Settings.Current.ClassDurationMinutes`（默认 45，可自定义，秒级）为约束窗口。
2. 任意滚动窗口内：某人被抽次数 ≤2，**除非**该窗口内全员都已至少被抽 1 次；
3. 同一个人两次被抽之间**至少间隔 4 次其他人的抽取**；
4. 不用伪随机玩具算法：使用 `RandomNumberGenerator`（CSPRNG）在满足约束的候选集合中等概率抽取，并用“最近最少被抽优先”的加权兜底（约束不可满足时，按间隔/次数最优解抽取，绝不死锁）；
5. 抽签结果通过 `Picked` 事件广播；姓名/学号/两者按 `Settings.Current.PickDisplay` 或花名册覆盖设置显示。
6. 每节课开始（ClassClock.Transition=ClassStarted）可重置“本节课窗口内”的计数；滚动窗口仍按时间戳判断。历史 `PickRecord` 持久化到 `%APPDATA%\Banji\pick-history.json`。

### 时间与课表规则

- `IScheduleService.SnapshotAt(DateTime now)` 纯函数，不依赖真实时钟；所有时间计算都要可测试。
- 课表结构：`WeekTimetable { Periods: List<ClassPeriod>{Index,Name,Start,End}, Breaks: List<BreakSegment>, Days: Dictionary<DayOfWeek, List<TimetableEntry>> }`。
- 课堂时段/午休可自定义；默认周一~周五 8 节课 + 午休。
- 铃声误差 `BellOffsetSeconds ∈ [-60, 60]`，默认 0；**仅在展示/触发上下课提示时对课表时间做偏移**（程序显示时间与真实铃声对齐），底层时间不变。
- 快照包含：当前课、下一节课、距下课、距上课、距放学、剩余节数、当天列表、强调项（下一节课；该节课开始后 10 分钟内仍强调，之后转“历史”普通样式）、`Phase ∈ {NoTimetable, BeforeSchool, InClass, Break, LunchBreak, AfterSchool}`。
- 临时换课：`DayOverride { Date, CourseReplacements: Dictionary<int,string?>, CancelledPeriods: HashSet<int>, Note }`，按日期优先于周课表。
- 上下课提示触发：`ClassClock.Transition` 在跨过（含误差偏移后的）上课/下课/放学时刻时触发一次；精确到秒；程序挂起/休眠恢复后不能补放一堆过期提示（只触发当前时刻最近的）。

### 计时器

- `ITimerService`：倒计时/正计时二选一；`StartCountdown(TimeSpan)` / `StartStopwatch()` / `Cancel()` / `AcknowledgeExpiry()`。
- 每 100～250ms 发 `Tick`；倒计时归零发 `Expired`（只一次），`AppServices.Current.Notices.ShowTimerTimeout()` 由 Lead 接线（TimerService 只发事件）。
- 正计时无超时提醒；可取消；取消后恢复空闲。
- 显示：`TimerSnapshot.DisplayText`（`mm:ss`，超 1 小时 `h:mm:ss`）。

### 抽签/气泡/功能性 UI 的协作点

- 抽签结果：`IPickService.Picked` → PetWindow 显示 10 秒气泡（10 秒由 PetWindow 控制）。
- 计时：PetWindow 订阅 `ITimerService.Tick/StateChanged` 在气泡区域显示。
- 功能区按钮由 `features-ui` 的 `Controls/Toolbar/FunctionsBar` 实现，宠物窗直接 `<toolbar:FunctionsBar/>` 嵌入；按钮文案/顺序：抽签(+)、倒计时、正计时、清醒一下。倒计时/正计时按钮在计时中变成“取消计时”并显示强调样式。
- 菜单折叠面板由 `pet-ui` 的 `MenuFlyout` 实现；选项至少含：显示仪表盘、设置、花名册管理、课表管理、宠物大小（无极滑块+手动输入）、按钮透明度、始终置顶开关、开机自启（可选）、杀死整个程序。

## 5. 视觉规范（默认值，全部可在设置里改）

按钮：圆角矩形 R=8（Win11 输入法预览框观感），透明背景，边框 1.5px，右下 45° 阴影偏移 4px。
- 常态：字/边框 `#317FFF`，透明度 10%
- 悬浮：`#5998FF`，0%
- 按下：`#0051D6`，0%
- 强调：`#FF3030`，0%（如“取消计时”）
- 右键：保持悬浮样式，无额外反馈。
- 自定义入口需有“恢复默认”，已默认时提示“已经是默认了！”，否则“恢复默认成功！”。

气泡：圆形/大圆角，白底 50% 透明（可调），可选高斯模糊，2px `#3E6BFF` 描边，文字 `#2E69FF`，字号随字数自适应；弹出有动画；文字打字机效果（不能肉，间隔 20～40ms/字，可跳过）。
- 抽签样式：顶部左对齐小字号 Thin “抽中的是”，中间固定字号 Bold 居中“学号”，下面灵活大字号 Bold 居中“姓名”。
- 气泡最高层（Topmost + 重新压顶）。

宠物：简单几何图形，两只眼睛，无鼻子嘴巴，可爱、可参考 Claude 橙色小东西；被点击（摸一摸）触发对话气泡；说话内容与高中科目相关（可温柔可吐槽），打字机效果；抽签/计时显示时气泡被征用。
- 宠物整体缩放：`Settings.UiScale`，无上下限（输入框允许 0.05～20，滑块 0.3～3），_无级_。
- 课表面板：展开当天课表（每行一节，下一节强调色 `#FF3030`，上课 10 分钟后转普通），两头顶小三角（一个折叠/展开课表，一个菜单），左下角小三角（向左展开功能按钮）。
- 常驻信息框：每项一行，含 距上课/距下课、距放学+还剩几节；下课时把“距下课”行替换成“下课超时 mm:ss”显示 15 秒后恢复。

字体：HarmonyOS Sans SC，权重 Thin/Light/Regular/Medium/Bold/Black 各自 FontFamily 资源：
`Font.Thin/Light/Regular/Medium/Bold/Black`（Lead 在 `Resources/Theme.xaml` 提供）。

## 6. 数据文件

- `settings.json`：AppSettings 序列化（System.Text.Json，camelCase，容错读取旧版本）。
- `rosters.json`：花名册列表。
- `timetable.json`：周课表 + 课堂时段 + 午休。
- `overrides.json`：临时换课（按日期）。
- `pick-history.json`：抽签历史（保留最近 30 天）。
- 写文件统一走 `Core/IO/SafeJsonStore.cs`（原子写：写 .tmp → File.Replace），避免损坏。

## 7. 测试与验收

- `src/Banji.Tests` 是控制台程序：`dotnet run --project src/Banji.Tests`，输出 `PASS/FAIL` 与退出码；每个 teammate 必须为自己的核心算法加测试（抽签约束统计 5000 次、导入格式、时间边界、计时器、设置 round-trip）。
- 测试不得依赖真实时间/网络/真实屏幕；对时间使用注入 `Func<DateTime>`。
- 验收硬指标：抽签 5000 次无约束违例；课表导入 4 种布局样例全部识别；上下课切换在 `[t-1s, t+1s]` 边界只触发一次；铃声误差 ±60s 生效且默认 0；设置恢复默认提示文案正确；宠物/气泡/全屏提示截图通过。
- 交付：`dist\班机安装程序.exe`（内含字体+本体，可安装、可卸载），`dist\班机.exe` 便携版（可选），`README.md`。

## 8. 协作纪律

1. 只写自己作用域；跨作用域需求发给 Lead（`send_message`）。
2. 每次提交前跑 `dotnet build`，自己作用域内不留编译错误；构建失败可能来自别人正在写的文件，重试或只查自己文件。
3. 不要 `git` 操作、不要 `dotnet publish`、不要动 `dist/`（packaging 除外）。
4. UI 线程：所有窗口/服务 UI 操作走 `Application.Current.Dispatcher`；定时器用 `DispatcherTimer`。
5. 异常不得吞：统一 `Log.Error(ex)` 写 `%APPDATA%\Banji\logs\`。
6. 完成后在共享任务板标记完成，并给 Lead 发一条自包含总结（文件、接口、测试结果、待集成点）。

## 9. 补充约定（2026-10-30，用户反馈后）

1. **宠物外观（用户明确要求）**：极简。身体是简单几何图形（橙色系），**眼睛就是两个纯黑实心小圆点**；不要腮红、不要鼻子、不要嘴巴、不要眼白/高光/瞳孔。`PetStyleSettings.ShowCheeks` 默认已关闭，实现时不要渲染腮红。眨眼动画可以有（小黑点短暂压扁），但整体保持简洁。
2. **窗口构造契约**：所有窗口必须保留**无参构造函数**（AppServices 通过无参构造创建），并保留以下属性（缺一不可）：
   - `TimerDialog.InitialKind`（`Banji.Core.Models.TimerKind`）
   - `RosterImportWindow.InitialFilePath` / `TimetableImportWindow.InitialFilePath`（`string?`）
   - `SettingsWindow.InitialSection`（`string?`）
   可以新增属性，不能删除/改签名。窗口内统一通过 `AppServices.Current` 取服务。
3. **单实例与构建**：构建/运行前先 `Get-Process Banji -ErrorAction SilentlyContinue | Stop-Process -Force`（exe 被占用会导致编译失败）。UI 验证统一用 `tools\qa.ps1`：`restart` → `cmd --cmd ...` → `screenshot -Out logs\xxx.png` → `stop`。多人同时验证会互相抢实例，遇到失败等 10 秒重试即可。
4. **不要修改** `Core/Models/**`、`Core/Settings/**`、`Core/Abstractions/**`、`Core/Boot/AppServices.cs`、`App.xaml*`、`Resources/**`、`Banji.csproj`、`Banji.sln`。确实需要加接口/字段时，先 send_message 找 Lead。
5. 组件内新增样式/资源字典放在自己的目录内（如 `Views/` 下的 `XxxStyles.xaml`），在窗口里 merge，不要动 `Resources/Theme.xaml`。
6. 中文字体统一用 `{StaticResource Font.Thin/Light/Regular/Medium/Bold/Black}`；用 `Font.Bold` 时同时 `FontWeight="Bold"`。禁止硬编码 `FontFamily="Microsoft YaHei"` 等。
7. 所有用户可见文案用简体中文；报错要用友好中文，不要直接抛英文异常给用户看。
8. **时间/随机/文件 IO 逻辑必须可测**：服务构造函数允许注入 `Func<DateTime> now`；随机用 `RandomNumberGenerator`；测试里用假时间。
9. 开发/QA 命令（首实例运行中时，第二个实例会把命令转发给首实例）：
   - `--cmd seed-demo` 生成演示花名册/课表
   - `--cmd pick` 触发抽签；`--cmd timer countdown 120` / `stopwatch` / `cancel`
   - `--cmd notice start|end|timeout` 预览全屏提示
   - `--cmd settings|dashboard|rosters|timetable|periods` 打开窗口
   - `--cmd expand true|false`、`--cmd scale 1.5`、`--cmd exit`

## 10. 第二轮需求契约（2026-11-06，Lead 已实现共享层）

**用户修改意见**：① 宠物改方形/扁扁/两只方形手+两只方形脚，不要圆形、不要橙色；② 气泡多行文字叠字 bug + 气泡改白底、不要高斯模糊；③ 上课提醒必须能自动关闭；④ 课表/信息框去掉卡片背景，纯文字悬浮桌面、文字周围重高斯模糊压暗背景提高可读性。
**新增功能**（用户点名）：一键备份/恢复、考试倒计时牌、随机分组、点名积分、铃声校准（“铃声又慢了”智能侦测）、宠物养成、本地离线台词生成器。**不做**：单双周/分层课表、值日/课代表轮值（等用户另行说明）。

### 10.1 新增共享模型（`Core/Models/Extras.cs`，已实现）
- `CountdownEventItem { Id, Name, Date(DateOnly), Enabled, Color }` + `DaysLeft(today)`
- `ClassBoundary { Time, Kind(ClassStart/ClassEnd/SchoolEnd), Title, PeriodIndex }`
- `GroupingOptions { GroupCount, BalanceByScore, Title }`、`GroupingResult { Title, RosterName, TotalStudents, Groups, ToPlainText() }`、`StudentGroup { Index, Name, Students, AverageScore }`
- `StudentPoints { RosterId, StudentKey, StudentName, StudentId, Points }`
- `PetGrowthStats { FirstUseDate, LastUseDate, DaysUsed, SessionCount, PatCount, PickCount, GroupCount, NoticeCount, Achievements }`
- `Achievement { Id, Title, Description, UnlockedAt }`、`BackupInfo { CreatedAt, AppVersion, RosterCount, StudentCount, HasTimetable, HasSettings, Files }`

### 10.2 新增/变更服务契约
- `IRosterService` 新增：`AddPoints(rosterId, studentKey, delta)`、`ResetPoints(rosterId)`、`TopPoints(rosterId, count)`；`Student` 新增 `Points`。
- `IScheduleService` 新增：`UpcomingBoundaries(now, count)`（返回未来上课/下课/放学边界，已应用铃声误差）。
- 新服务（`AppServices.Current` 暴露）：`IGroupingService Grouping`、`IPetGrowthService Growth`、`IBackupService Backup`。
- `AppServices` 新增方法：`ShowGroupingWindow()`、`ShowRingCalibrationWindow()`、`RestartApplication()`；启动时 `Growth.RecordSessionStart()`；`Picker.Picked` 已自动 `Growth.RecordPick()`；上下课提示已自动 `Growth.RecordNotice()`。**不要重复接线**。
- 宠物摸头请调用 `AppServices.Current.Growth.RecordPat()`（受 `Settings.EnableGrowth` 控制）。
- 新增 QA 命令：`--cmd grouping|ring-calibration|backup-export <path>|backup-inspect <path>|backup-import <path>|restart|set-notice-duration <s>`。

### 10.3 默认值变化（Lead 已改）
- `Bubble.UseBlur=false`、`Bubble.OpacityPercent=0`（完全不透明白底）；气泡多行叠字根因：`BubbleWindow.xaml` 的 `LineHeight="1.45"` 是**绝对像素**（1.45px 行高），必须改为字号倍数换算（约 20px）或删除。
- `PetStyleSettings` 默认雾蓝色方形配色：BodyColor `#8FB6F2`、BodyShadeColor `#6E96DA`、BellyColor `#B7CFF7`；仍保留两个纯黑圆点眼睛、无腮红/鼻子/嘴巴/高光。
- 新增 `AppSettings` 字段：`ShowCountdownOnPet=true`、`CountdownEvents`、`UseLocalQuipGenerator=true`、`EnableGrowth=true`。

### 10.4 第二轮文件作用域
| 负责人 | 新增/变更 |
|---|---|
| `pet-ui` | `Views/PetWindow.*`、`BubbleWindow.*`、`MenuFlyout.*`、`Controls/Pet/**`、`Core/Quips/**`、`Core/Growth/*`（完善） |
| `features-ui` | `Views/SettingsWindow.*`、`DashboardWindow.*`、`Controls/Toolbar/**`、`Core/Backup/**`（完善）、新增 `Views/BackupWindow.*`（如需要） |
| `core-roster` | `Core/Roster/**`、`Core/Grouping/**`、`Views/RosterWindow.*`、`Views/GroupingWindow.*` |
| `core-schedule` | `Core/Schedule/**`、`Core/Notices/**`、`Views/FullscreenNoticeWindow.*`、`Views/RingCalibrationWindow.*` |
| `packaging` | 图标、README、版本 1.1.0、重新打包 dist、安装器自测 |

窗口统一保留无参构造；`SettingsWindow` 继续保留 `InitialSection`；所有新增窗口由 `AppServices` 统一创建。
**禁止**：修改 `Core/Models/**`、`Core/Settings/**`、`Core/Abstractions/**`、`Core/Boot/AppServices.cs`、`App.xaml*`、`Resources/**`、`Banji.csproj`、`Banji.sln`（需要改先找 Lead）。单双周/值日**不要实现**。

## 11. 第三轮需求契约（v1.2.0，2026-11-06）

**用户修改意见**：①「宠物形象」与「头顶课表」大小**分别**调节（以前是整体缩放）；② 宠物头顶课表位置可由用户用**横/纵两个滑块**调整相对位置；③ 本地台词生成器出现“体育这道题”“今天也要和自习好好相处”等不合逻辑搭配，需修复；④ 端侧生成式 AI：内存 16GB（可用约 14GB），可上更大参数模型，并在应用内提供与模型对话的**聊天窗口**。
**版本规则（此后统一）**：语义化版本 `vMAJOR.MINOR.PATCH`；新功能→MINOR（本轮 **v1.2.0**），修 bug→PATCH，不兼容→MAJOR；预发布用 `-rc.N`；不使用 “SP1” 之类后缀。

### 11.1 共享契约（Lead 已实现）
- `AppSettings` 新增：`PetScale`（宠物本体缩放 0.2～5）、`PanelScale`（课表/信息面板缩放 0.4～4）、`PanelOffsetX`/`PanelOffsetY`（面板相对默认位置偏移 DIP，±800）；`UiScale` 保留兼容（默认 1）。
- 新增 `AiSettings Ai`：`ModelPath`/`ModelName`/`LastModelId`/`EnableAiQuips`/`AutoLoadModel`/`Threads`/`ContextSize`/`Temperature`/`MaxTokens`。
- 新服务 `IAiService`（`Banji.Core.Abstractions/Ai.cs`）：`Status/LoadedModelName/LoadedModelPath/ModelDirectory/ModelOptions/StatusChanged/DownloadProgress`、`LoadModelAsync/Unload/TryAutoLoad/DownloadModelAsync/GenerateChatReplyAsync/TryGenerateQuipAsync/TryGenerateGreetingAsync`；DTO：`AiModelOption/AiDownloadProgress/AiChatMessage/AiChatRole/AiModelStatus`。
- `AppServices.Current.Ai` 已接线；`Start()` 调用 `Ai.TryAutoLoad()`；新增窗口方法 `ShowChatWindow()` / `ShowAiModelManager()`。
- NuGet 已加入：`LLamaSharp 0.30.0` + `LLamaSharp.Backend.Cpu 0.30.0`（CPU 推理，win-x64 原生库随发布自解压）。
- 新增 QA 命令：`--cmd chat`、`--cmd ai-models`、`--cmd ai-status`、`--cmd ai-download <modelId>`、`--cmd ai-say <prompt>`。
- 模型目录：`%APPDATA%\Banji\models`；下载源默认 hf-mirror（国内可直连），三档 Qwen2.5 GGUF：0.5B 469MB / 1.5B 1.07GB / 3B 2.01GB。模型**不随安装包分发**，由用户在应用内一键下载或选择本地 gguf。

### 11.2 第三轮文件作用域
| 负责人 | 新增/变更 |
|---|---|
| `ai-chat`（新） | `Core/Ai/**`（替换 stub 为 LLamaSharp 实现）、`Views/ChatWindow.*`、`Views/AiModelDialog.*`、`Tests/Ai*.cs` |
| `pet-ui` | `Views/PetWindow.*`、`MenuFlyout.*`、`Controls/Pet/**`、`Core/Quips/**`：分离缩放/面板偏移、菜单滑块、AI 台词接入、台词语义修复 |
| `features-ui` | `Views/SettingsWindow.*`、`DashboardWindow.*`：新增缩放/位置设置、AI 分区；版本页脚 v1.2.0 |
| `packaging` | v1.2.0 打包、README/RELEASE_NOTES 更新、模型下载说明、安装器自测 |

窗口统一保留无参构造；禁止修改 `Core/Models/**`、`Core/Settings/**`、`Core/Abstractions/**`、`Core/Boot/AppServices.cs`、`App.xaml*`、`Resources/**`、`Banji.csproj`、`Banji.sln`（需改先找 Lead）。

## 12. 第三轮修改契约（v1.3.0，2026-11-06 晚）

**用户反馈**：上课/下课全屏紫底太炸眼、挡住老师 PPT；除计时器外所有提醒改成**气泡**（灵动冒出→轻柔回弹→定住 7 秒→收回）；**右击宠物**直接调出右侧小箭头菜单；宠物与课表可**分别隐藏**；宠物无动态 10 分钟后**自动收进屏幕侧边变成一道粗弧线**（类似手机悬浮球）；课表无操作 10 分钟自动进入**折叠态**（只显示下节课时间和下节课内容）。
**版本**：v1.3.0（新功能 → MINOR）。

### 12.1 共享契约（Lead）
- `AppSettings` 新增：`HidePet`（手动隐藏宠物本体，保留侧边弧）、`PetIdleCollapseMinutes`（默认 10，0=关闭）、`TimetableIdleFoldMinutes`（默认 10，0=关闭）、`NoticeBubbleHoldSeconds`（默认 7）。隐藏课表沿用 `ShowTimetablePanel=false`。
- `INotificationService` 新增：
  - `event EventHandler<NoticeToast>? ToastRequested;`
  - `void ShowToast(NoticeToast toast);`
  - 新模型 `NoticeToast`（`Title`/`Message`/`Kind`/`HoldMilliseconds`）与 `NoticeToastKind{ClassStart,ClassEnd,Generic,Preview}`。
  - `ShowClassStart/ShowClassEnd` 内部改为抛 `ToastRequested`（**不再开全屏窗**）；`ShowTimerTimeout` 仍用全屏（唯一全屏提醒）；`DismissAll` 同时关闭气泡。
- 窗口气泡由 PetWindow 订阅 `ToastRequested` 并弹出 `NoticeBubbleWindow`（pet-ui 实现），无参窗口交给 PetWindow 持有；窗口 `Title="BanjiNotice"`（自动化探测用）。
- 宠物窗新增 QA 方法（App.xaml.cs 命令对接）：`CollapseToEdge()`、`ExpandFromEdge()`、`FoldTimetable()`、`UnfoldTimetable()`；动画/状态放 pet-ui。
- QA 命令：`--cmd collapse-pet`、`--cmd expand-pet`、`--cmd fold-timetable`、`--cmd unfold-timetable`；`--cmd set` 新增 `hidepet`、`petidle`、`tableidle`、`noticehold`。
- 版本号：主程序 csproj = **1.3.0**；`vMAJOR.MINOR.PATCH` 规则不变。

### 12.2 v1.3 文件作用域
| 负责人 | 文件 |
|---|---|
| `pet-ui` | `Views/NoticeBubbleWindow.*`（新）、`Views/PetWindow.*`（订阅气泡/右击菜单/隐身弧/折叠课表/空闲计时）、`Views/MenuFlyout.*`（隐藏开关等）、`Controls/Pet/**`、`Core/Quips/**`（不改语义）、`Tests/PetUi*` |
| `core-schedule` | `Core/Notices/NotificationService.cs`（改气泡事件）、`Views/FullscreenNoticeWindow.*`（只保留计时器用途）、`Tests/Notice*` |
| `features-ui` | `Views/SettingsWindow.*`（提示预览改气泡、自动收起/折叠设置、隐藏开关）、`Tests/Settings*` |
| `packaging` | v1.3.0 README/RELEASE_NOTES/构建/桌面发布夹 |
禁止改共享文件（`AppSettings`、`Abstractions`、`Boot/AppServices`、`App.xaml*`、`Banji.csproj`、`Resources/**`、菜单以外别人的文件）；需要改先找 Lead。
