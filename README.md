# QuickClip

Windows 平台极速、纯净的本地剪贴板管理工具。基于 .NET 8 与 WPF 构建，原生静默接管 `Win + V`，提供图片即时缩略、二维码双向解析与生成、拼音首字母快速检索与多主题支持。纯离线运行，零云端上传，保护数据隐私。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg)](docs/COMPATIBILITY.md)
[![Platform](https://img.shields.io/badge/Platform-Win--x64-gray.svg)](https://github.com/soldier-cv/quick-clip/releases)

<p align="center">
  <img src="docs/assets/preview.png" width="560" alt="QuickClip 主面板预览">
</p>

---

## 为什么选择 QuickClip

- **极速唤起，静默接管**：底层键盘钩子精准接管 `Win + V` 组合键，彻底告别 Windows 自带剪贴板的卡顿与开始菜单误触发。
- **纯本地运行，隐私无忧**：数据持久化于本地 SQLite 数据库，无任何后台数据上传行为，不破坏系统剪贴板原生链路。
- **富媒体卡片化呈现**：复制的文本、链接、截图与表情包自动分类。图片即时生成清晰缩略图，悬停即可放大预览。
- **二维码双向互通**：
  - 文本/链接悬停一键生成高清二维码，方便手机等移动端扫码互传；
  - 复制包含二维码的图片时，后台毫秒级自动解析提取目标链接。
- **中文拼音首字母检索**：支持拼音首字母搜索（如输入 `sjjg` 即可极速定位 `设计架构`）。
- **离线 OCR 文本提取**：内置 Windows 原生 OCR 支持，同时兼容 PP-OCRv6 本地模型及兼容 OpenAI 协议的视觉接口。
- **现代 Fluent 设计与多主题**：内置 Terminal 终端灰黑、One Dark、GitHub Dark、GitHub Light、Nord 等多套精美主题。

---

## 功能展示

### 1. 核心面板与快捷操作
主面板汇集最近剪贴历史，按文本、链接、图片等类型清晰分块。支持 `1 ~ 9` 数字键一键快速粘贴至目标窗口。

<p align="center">
  <img src="docs/assets/preview.png" width="600" alt="QuickClip 主面板">
</p>

### 2. 图片缩略与大图悬停预览
无需打开第三方图像浏览工具，复制到剪贴板的图片会自动以卡片形式展示，悬停即可查看大图细节。

<p align="center">
  <img src="docs/assets/preview-image.png" width="620" alt="图片缩略与悬停大图预览">
</p>

### 3. 二维码双向解析与生成
打通电脑与移动设备之间的数据传输通道：
- **生成二维码**：悬停在文本或链接条目上，一键弹出二维码提供扫码。
- **识别二维码**：剪贴板中复制含有二维码的图片时，自动识别并提取内部文本或链接。

<p align="center">
  <img src="docs/assets/qr-generate.png" width="380" alt="悬停生成二维码">
  &nbsp;&nbsp;
  <img src="docs/assets/qr-decode.png" width="380" alt="图片二维码自动识别">
</p>

### 4. 丰富的主题配色
支持多种流行配色方案，随心切换，满足不同桌面风格与工作环境的视觉偏好。

<p align="center">
  <img src="docs/assets/themes.png" width="760" alt="QuickClip 主题画廊">
</p>

---

## 快捷键速查

除 `Win + V` 与序号快速粘贴外，其余快捷键均可在设置中灵活自定义。

| 按键 | 功能说明 |
| :--- | :--- |
| `Win + V` | 唤起 / 隐藏 QuickClip 主面板 |
| `Ctrl + Shift + V` | 任意软件中直接以纯文本格式粘贴剪贴板最新内容 |
| `1 ~ 9` | 快速粘贴对应序号的历史条目 |
| `↑` / `↓` | 列表项上下选择 |
| `Enter` | 将当前选中项粘贴至前台目标窗口 |
| `Shift + Enter` | 将当前选中项强制以纯文本格式粘贴 |
| `Ctrl + C` | 复制当前选中项内容到系统剪贴板 |
| `Ctrl + P` | 切换窗口置顶状态（置顶后失焦不隐藏） |
| `Delete` | 删除当前选中项 |
| `Esc` | 隐藏主面板 |

---

## 下载与安装

### 预编译安装包

前往 [GitHub Releases](https://github.com/soldier-cv/quick-clip/releases) 页面下载最新版本：

| 安装包 | 说明 | 运行前置条件 |
| :--- | :--- | :--- |
| `QuickClip-Setup-win-x64.exe` | 轻量安装包，体积小 | 需安装 [.NET 8 桌面运行时 x64](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe) |

> 软件内置静默检查更新功能（可在设置中关闭）。检测到新版本后，点击「立即更新」即可自动完成升级。

---

## 从源码构建

### 环境要求
- Windows 10 (1809 及以上) / Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 构建命令

```powershell
# 1. 本地运行与调试
dotnet run --project src/QuickClip/QuickClip.csproj

# 2. 构建独立发布包 (配合 Inno Setup 编译 setup/QuickClip.iss 生成安装包)
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=false -o publish/fdd
```

---

## 本地数据与配置文件

QuickClip 所有数据均持久化在当前用户的本地应用数据目录：
`%LOCALAPPDATA%\QuickClip\`

- `quickclip.db`：SQLite 剪贴板历史数据库
- `previews\`：图片条目缩略图缓存目录
- `settings.json`：用户设置文件（快捷键、历史记录上限、主题等）
- `updates\`：版本更新安装包缓存
- `debug.log`：运行与异常日志

> 注：托盘图标提供完整的菜单控制。关闭主窗口默认最小化至系统托盘，退出程序请在托盘右键选择「退出」。

---

## 技术选型

- **UI 界面**：C# / .NET 8 + WPF (WPF-UI Fluent 风格)
- **底层交互**：Win32 API 全局底层键盘钩子 (`WH_KEYBOARD_LL`) 与剪贴板消息监听
- **二维码处理**：ZXing.Net + QRCoder
- **OCR 引擎**：Windows.Media.Ocr / RapidOcrNet (PP-OCRv6)
- **数据持久化**：SQLite (Microsoft.Data.Sqlite)
- **检索加速**：PinYinConverterCore 中文拼音索引

---

## 相关文档

- [架构设计与技术实现](docs/DESIGN.md)
- [系统环境与兼容性说明](docs/COMPATIBILITY.md)
- [安全与隐私设计规范](docs/SECURITY.md)
- [代码贡献指南](CONTRIBUTING.md)
- [版本更新记录](CHANGELOG.md)

---

## 开源协议

本项目基于 [MIT 许可证](LICENSE) 分发。商业使用、修改与衍生均受允许，只需在衍生作品中保留原版权声明与许可文件。
