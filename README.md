# QuickClip

`Win + V` 唤起的本地剪贴板。图片进列表就能看，文本能生成二维码，截到的码会自动解析。纯离线，不改系统剪贴板。

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6.svg)](docs/COMPATIBILITY.md)

<p align="center">
  <img src="docs/assets/preview.png" width="420" alt="QuickClip 主面板：二维码解析、图片预览、链接与搜索">
</p>

---

## 图片预览

截图、表情包复制进来就是卡片，列表里直接看缩略图。

<p align="center">
  <img src="docs/assets/preview-image.png" width="520" alt="剪贴板图片卡片与缩略图">
</p>

## 二维码双向

文本 / 链接悬停即可生成，点一下放大给手机扫。复制一张码，自动抽出链接。

<p align="center">
  <img src="docs/assets/qr-generate.png" width="360" alt="悬停生成二维码，手机扫码接力">
  &nbsp;
  <img src="docs/assets/qr-decode.png" width="360" alt="图片中的二维码自动识别并展示链接">
</p>

## 还有这些

- ⚡ **`Win + V` 静默接管**：钩子吞掉 Win 键，不误弹开始菜单；其它热键优先 `RegisterHotKey`
- 🔍 **拼音首字母**：`设计架构` 搜 `sjjg`
- 🔤 **Windows 原生 OCR**：系统引擎；也可配 Ollama / OpenAI
- 📌 **窗口置顶**：`Ctrl+P` / 图钉，失焦不藏
- 🧹 **条数上限**：默认最多 233 条，超限淘汰最旧非置顶；超大文本/图片不入库，文件只记路径
- 🎨 **多主题**：Terminal / One Dark / GitHub / Nord …

---

## 下载

从 [Releases](https://github.com/soldier-cv/quick-clip/releases) 下载：

| 包 | 说明 |
| --- | --- |
| `QuickClip-Setup-win-x64.exe` | **推荐**。体积小，需已安装 [.NET 8 桌面运行时 x64](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe) |
| `QuickClip.exe` | 绿色单文件，自带运行时，免安装、体积较大 |

启动后会延迟检查更新并下载到 `%LOCALAPPDATA%\QuickClip\updates\`，可在设置中关闭自动检查。

- **安装版**：点「立即更新」打开 Setup
- **绿色版**：点「打开下载目录」，退出 QuickClip 后用新 exe 替换原来的文件（不自动覆盖正在运行的程序）

## 从源码构建

环境：Windows 10 1809+ / Windows 11、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet run --project src/QuickClip/QuickClip.csproj

# 绿色单文件
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

# 安装包载荷（再本机用 Inno Setup 编译 setup/QuickClip.iss）
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=false -o publish/fdd
```

`publish/` 已加入 `.gitignore`。

## 默认快捷键

除 `Win + V` 与 `1~9` 外，可在设置中改。

| 按键 | 功能 |
| --- | --- |
| `Win + V` | 唤起 / 隐藏主面板 |
| `Ctrl + Shift + V` | 任意程序中纯文本粘贴 |
| `1 ~ 9` | 快速粘贴对应序号 |
| `↑` / `↓` | 移动选中 |
| `Enter` | 粘贴当前项 |
| `Shift + Enter` | 纯文本粘贴当前项 |
| `Ctrl + C` | 复制当前项 |
| `Ctrl + P` | 窗口置顶 |
| `Delete` | 删除当前项 |
| `Esc` | 隐藏面板 |

## 数据与配置

`%LOCALAPPDATA%\QuickClip\`：`quickclip.db`、`previews\`、`updates\`、`settings.json`、`debug.log`。

设置在面板 ⚙ 或托盘里。关闭窗口只是藏到托盘；退出请用托盘右键。

## 文档

- [架构设计](docs/DESIGN.md) · [兼容性](docs/COMPATIBILITY.md) · [安全与隐私](docs/SECURITY.md)
- [文档索引](docs/README.md) · [贡献指南](CONTRIBUTING.md) · [变更记录](CHANGELOG.md)

## 技术栈

C# / .NET 8 + WPF（WPF-UI）· Win32 钩子与剪贴板 · ZXing + QRCoder · Windows.Media.Ocr · SQLite · PinYinConverterCore

## 许可证

**[MIT](LICENSE)**。使用、修改、商用、闭源衍生都可以，副本里保留版权与许可声明即可。无担保。
