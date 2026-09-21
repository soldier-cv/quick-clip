# QuickClip AI 协同开发指南 (AGENT.md)

欢迎来到 **QuickClip** 项目！本项目是一项探索 **Vibe Coding（人机深度协同、AI 驱动研发）** 的开源系统级原生应用实践。为了确保所有参与本项目的 AI 编程智能体能够高效、安全、统一地协同工作，请严格遵守以下开发规范与行为准则。

---

## 🔴 核心开发准则与协作规范 (Core Development Protocols)

1. **功能基线与防退化规范 (CRITICAL)**：
   - 所有参与本项目的开发者与 AI 智能体，在进行任何功能改动、重构或修缺陷前，**必须严格遵守 [docs/SPECIFICATION.md](docs/SPECIFICATION.md)（核心规格、架构设计与需求基线说明书）**；
   - 严禁随意删除、弱化或改坏已有功能与交互体验；任何新功能开发不得以破坏已有功能（如普通模式/置顶模式的粘贴可用性）为代价。
2. **构建与验证规范**：
   - 本项目通过 `dotnet build -c Release` 进行编译与语法合规性检查（确保 0 警告、0 错误）；
   - 涉及剪贴板与底层 Win32 API 交互的逻辑，需通过针对性的冒烟验证确认；避免在未隔离的本地生产环境执行可能污染系统剪贴板历史或注册表状态的操作。
3. **环境感知与兼容性**：
   - 当前开发与目标运行环境为 **Windows 10 (1809+) / Windows 11**。
   - 所有终端命令、文件路径处理及自动化脚本必须原生兼容 Windows PowerShell 环境（避免使用类 Unix 特有且 Windows 缺失的命令）。
4. **交互语言规范**：
   - 无论用户使用何种语言提问，AI **必须始终使用中文** 进行思考、回复、解释与沟通。
5. **Git 提交信息规范 (Commit Message)**：
   - **所有 Git commit message 必须使用清晰、规范的中文描述**（例如：`feat: 增加常用短语动态占位符支持`、`fix: 优化搜索框方向键预览选中项`）。
   - 严禁使用空洞、无意义的纯英文提交（如 `fix bug`、`update`）。
6. **版本号与发布准则**：
   - 严格遵循语义化版本（[Semantic Versioning](https://semver.org/lang/zh-CN/)）规范；
   - 每次发版必须具备实质性的功能增量或重要缺陷修复，并在 `CHANGELOG.md` 中详实记录变更，严禁无实质内容时频繁琐碎发版。

---

## 💡 产品哲学与核心架构认知

在编写任何代码或设计新功能前，AI 必须深刻理解 QuickClip 的产品灵魂：

1. **极速、纯粹、无打扰**：
   - 毫秒级原生唤起（`Win + V`），杜绝任何卡顿感。
   - 针对开机自启场景，必须在后台静默预热视觉树与资源，消除冷启动迟钝。
2. **为什么坚持“纯本地，不做云同步”？**：
   - **机制协同**：现代主流输入法（微信输入法、搜狗输入法等）已具备成熟的跨端加密流转。当手机复制后，输入法客户端通过 Win32 API（`OpenClipboard` / `SetClipboardData`）写入 Windows 系统剪贴板；
   - **原生捕获**：QuickClip 基于 Win32 `AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE` 原生消息流守候，毫秒级无感捕获入库；
   - **隐私护城河**：绝不自建云服务器收集用户的敏感剪贴内容，数据 100% 持久化在本地 SQLite，确保极端隐私安全与无网可用性。
3. **国内镜像生态优先**：
   - 检查更新与安装包下载统一采用 **Gitee 优先（国内高速直连），GitHub 备用降级** 策略。

---

## 🎨 界面文案与交互体验规范 (UI & Copywriting Protocols)

1. **文案精炼克制，杜绝冗余修饰**：
   - 界面功能选项、标签与按钮文本必须直奔主题，避免任何非必要的修饰词。
   - **严禁多余修饰**：功能选择项统一直接展示核心名称（如「Google 翻译」、「微软翻译」），严禁添加「免密」、「直连」、「公共免密通道」等冗余描述。
   - 提示信息（ToolTip、Hint）保持简明准确，直接说明核心前提或功能（如「需代理环境」），杜绝啰嗦口语化。
2. **键盘优先与操作连贯性**：
    - 窗口置顶工作会话：粘贴后面板保持打开，但**绝不自动移动或切换选中项**，交由用户自由决定何时按上下键挑选或继续按 Enter。需要多字段顺序填表时使用收集栈（`Ctrl + Shift + S`），不要再引入独立的「连续粘贴模式」设置项。
   - 交互浮层便捷直达：如二维码预览浮层，支持双击色块直接将内容粘贴到目标窗口并自动关闭预览。

---

## 📐 C# 编码与代码组织规范 (Coding Standards)

### 1. 命名空间与 using 引用规范 (CRITICAL)
- **禁止全限定类名**：严禁在方法体、变量声明、参数列表中直接使用全限定类名（如 `QuickClip.Services.SettingsService`）。
- **必须显式 using**：所有外部或跨命名空间类型必须在文件头部显式 `using` 引入，代码正文中仅使用**简单类名**。
- **唯一豁免**：仅当同一文件中存在类型名歧义冲突时允许使用全限定名。

### 2. 异步与性能规范
- 所有涉及 I/O（文件读写、数据库访问、网络请求）的操作必须使用异步 API，方法命名统一以 `Async` 结尾。
- 网络请求（`HttpClient`）必须显式配置连接与超时时间（`CancellationTokenSource`），严禁使用无超时的无限期挂起等待。

### 3. C# XML 文档注释规范 (Documentation Comments)
所有新建或核心公共类、接口需遵循标准 C# XML 文档注释（`/// <summary>`）：
* 明确说明类的职责、设计初衷与生命周期说明；
* 关键元数据（如负责人、创建/修改时间）可统一规范记录在 XML 注释中，例如：
  ```csharp
  /// <summary>
  /// 剪贴板热键监听与调度服务。
  /// </summary>
  /// <remarks>
  /// 作者: xudong.hua, gemini
  /// 时间: 2026-09-19 14:20 星期六
  /// </remarks>
  ```

---

## 🛠️ 项目常用命令速查

```powershell
# 1. 本地运行与调试
dotnet run --project src/QuickClip/QuickClip.csproj

# 2. 本地 Release 编译验证（0 错误 0 警告）
dotnet build -c Release

# 3. 本地独立发布测试包
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o publish/fdd
```
