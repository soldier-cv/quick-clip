# Windows 兼容性说明

QuickClip 基于 **.NET 8 + WPF** 开发，目标平台为：

| 系统 | 支持情况 |
| --- | --- |
| **Windows 11** | ✅ 完整支持（推荐，Mica 材质） |
| **Windows 10 1809+**（含 19041+） | ✅ 完整支持（Acrylic 材质） |
| Windows 10 早期版本（&lt; 1809） | ❌ 不支持 |
| **Windows 8 / 8.1** | ❌ 不支持 |
| **Windows 7** | ❌ **不支持** |
| 非 Windows（macOS / Linux） | ❌ 不支持 |

> **结论：当前发布的 `QuickClip.exe` 不能在 Windows 7 上运行。**  
> 原因是硬性依赖（.NET 8 运行时、Win10 SDK TFM、WinRT OCR、现代 Fluent 材质），不是简单改编译选项即可兼容。

架构：x64（`win-x64` 自包含单文件）。

---

## 1. 功能支持矩阵

| 功能模块 | Windows 10 (1809+) | Windows 11 | 技术底层 |
| :--- | :---: | :---: | :--- |
| 快捷键接管（`Win+V`） | 🟢 | 🟢 | `RegisterHotKey` + `WH_KEYBOARD_LL` 回退 |
| 剪贴板监听 | 🟢 | 🟢 | `AddClipboardFormatListener` |
| 离线二维码生成/识别 | 🟢 | 🟢 | `ZXing.Net` + `QRCoder` |
| SQLite 历史（条数 + 24h 淘汰） | 🟢 | 🟢 | `Microsoft.Data.Sqlite` |
| 键盘粘贴回填 | 🟢 | 🟢 | `SendInput` |
| UI 材质 | Acrylic | Mica / Acrylic | WPF-UI；远程显示自动降级 |
| 离线 OCR | 🟢（需语言包） | 🟢 | `Windows.Media.Ocr`（WinRT） |

---

## 2. 核心适配细节

### 2.1 UI 材质

- Windows 11（Build ≥ 22000）：优先 **Mica**
- Windows 10：使用 **Acrylic**
- 远程桌面 / 虚拟显示驱动：关闭半透明，改用不透明深色背景 + 软件渲染，避免黑屏

### 2.2 离线 OCR

- 依赖系统内置 `Windows.Media.Ocr`（Windows 10 1511+）
- 需安装对应 OCR 语言包；缺失时界面会提示，并可配置 Ollama / OpenAI 作为可选引擎

### 2.3 打包部署

- .NET 8 自包含单文件（`PublishSingleFile` + `SelfContained`）
- 绿色免安装，**不要求**目标机预装 .NET，但 **OS 必须是 x64 的 Windows 10/11**

### 2.4 热键与安全软件

- 可配置热键优先 `RegisterHotKey`；`Win+V` 通常由低级键盘钩子接管
- 个别杀软可能拦截全局钩子，需放行

### 2.5 自启动、管理员与更新

- 开机自启：写 `HKCU\...\Run`，无需管理员
- 以管理员重启：走 UAC；用户取消则不退出当前实例
- 检查更新：仅用户主动点击时访问 GitHub Releases API

---

## 3. 为何不做 Windows 7

| 依赖 | Win7 |
| --- | --- |
| .NET 8 | 官方不支持 |
| `net8.0-windows10.0.19041.0` | 面向 Win10 API |
| WPF-UI + Mica/Acrylic | 面向现代 Windows |
| `Windows.Media.Ocr` | 系统无此 API |

若未来需要 Win7，需独立精简分支（旧运行时 + 去掉 WinRT/现代材质），与主线分离维护，成本高、收益低，**当前不做**。
