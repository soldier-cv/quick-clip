# QuickClip

Windows 平台极速、纯净的本地剪贴板管理工具。基于 .NET 8 与 WPF 构建，原生静默接管 `Win + V`，提供图片即时缩略、二维码双向解析与生成、拼音首字母快速检索与多主题支持。纯离线运行，零云端上传，保护数据隐私。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg)](docs/COMPATIBILITY.md)
[![Platform](https://img.shields.io/badge/Platform-Win--x64-gray.svg)](https://github.com/soldier-cv/quick-clip/releases)
[![Gitee](https://img.shields.io/badge/Gitee-QuickClip-C71D23.svg)](https://gitee.com/huaxudong/quick-clip)
[![Vibe Coding](https://img.shields.io/badge/Vibe%20Coding-AI%20Collaborative-FF69B4.svg)](AGENT.md)

<p align="center">
  <img src="docs/assets/preview.png" width="560" alt="QuickClip 主面板预览">
</p>

---

## 🌟 致谢与开源寄语

> **“让工具回归本真，让效率触手可及。”**

衷心感谢每一位使用、Star 以及向身边朋友和同事推荐 **QuickClip** 的伙伴！

作为一个每天与代码、文档和各类信息打交道的开发者，我始终坚信：**一个优秀的生产力工具应当是极速、纯粹且无打扰的**。它不应该有繁杂的登录体系，不应该让用户的敏感隐私在云端裸奔，更不应该在按下快捷键时让人陷入烦躁的等待。

QuickClip 凝聚了我对“极致剪贴体验”的全部构想与打磨。如果它在日常工作中哪怕为你节省了一秒钟、减少了一次频繁切换窗口的疲劳，那么这个开源项目的想法与实践就是有价值的。

### 🤖 探索 Vibe Coding：人机协同的生产力实践
本项目是一项深入探索 **Vibe Coding（AI 驱动研发、人机深度协同）** 的开源系统级原生应用实践。从产品交互构思、架构设计、Win32 钩子封装到性能压榨，均由作者与前沿 AI 编程智能体共同雕琢。我们致力于探索并证明：在 AI 赋能的全新时代，开发者完全能够以极致的效率与敏锐的产品直觉，构建出高品质、高稳定性的原生生产力工具。项目规范与 AI 协同指南详见 [AGENT.md](AGENT.md)。

如果你喜欢 QuickClip 的设计理念，**非常欢迎将它推荐给你身边的开发者与办公好友**！每一位用户的认可与推广，都是支撑这个项目不断进化与完善的最大动力。

---

## 💡 为什么 QuickClip 坚定选择“纯本地，不做云同步”？

很多朋友接触到剪贴板管理工具时，常会好奇：“没有自己的云同步账号，多设备（如手机与电脑）之间协同怎么办？”

**答案是：因为成熟的输入法已经把跨端传输做到了极致，无需我们重复造轮子，更无需承担隐私泄露的代价。**

### 1. 技术原理解密：输入法跨端同步，QuickClip 如何无感捕获？
- **跨端流转的底层真相**：如今微信输入法、搜狗输入法或操作系统自带的跨设备互联（如华为分享、MIUI互联等）都深度集成了移动端与 PC 端的流转通道。当你在手机上复制一段文本，微信输入法等工具通过自身的云端信道同步推送到电脑端时，其 Windows 客户端会在本地调用 Win32 原生剪贴板接口（如 `OpenClipboard`、`EmptyClipboard`、`SetClipboardData`）将该内容写入 Windows 系统的原生剪贴板通道。
- **QuickClip 的底层监听机制**：QuickClip 是通过注册 Windows 原生剪贴板监听器（`AddClipboardFormatListener` / `WM_CLIPBOARDUPDATE`），以操作系统底层事件驱动的方式守候在后台。**只要输入法将手机内容写入本地系统剪贴板，QuickClip 会在毫秒内自动捕捉、入库持久化并生成索引**。你拿起手机复制，切回电脑按 `Win + V`，内容早已安躺在列表中。

### 2. 纯本地离线的核心优势：
- 🛡️ **绝对隐私无忧**：剪贴板里往往充斥着银行账号、密码、密钥、身份证号、敏感聊天记录等极端私密的数据。市面上的云同步工具往往要求注册并上传到第三方云服务器，存在不可控的安全与风控风险。QuickClip 数据 100% 留存在本地 SQLite，**零云端上传、零遥测收集**。
- ⚡ **毫秒级零延迟**：不需要网络握手、无需等待远程鉴权，即使断网或在高铁、机房等无网环境，所有操作依然毫秒响应。
- 🎯 **各司其职的协同美学**：让深耕跨端通信的成熟输入法负责设备间传输，让 QuickClip 专注于 Windows 本地最极致的检索、收集出栈、贴图、OCR 与快捷管理。

---

## ✨ 核心特性

- **极速唤起，静默接管**：底层键盘钩子精准接管 `Win + V` 组合键，彻底告别 Windows 自带剪贴板的卡顿与开始菜单误触发。开机自启后台静默预热，彻底根除开机后首次唤醒的冷卡顿。
- **纯本地运行，隐私无忧**：数据持久化于本地 SQLite 数据库，无任何后台数据上传行为；对系统剪贴板只读不改写。托盘「暂停捕获」可在复制敏感内容时临时停记。接管 Windows 剪贴板历史前会先快照原始注册表状态，退出、关闭接管或卸载时精确还原。
- **连续粘贴栈模式（收集栈）**：支持全局快捷键一键开启/关闭切换（默认 `Ctrl + Shift + S`），也可在设置中自由定义。连续复制多个条目入栈或一键多行拆分入栈，在目标窗口按 `Ctrl + V` 依次出栈粘贴，配备迷你悬浮 HUD 实时显示当前进度与下一项预览。
- **常用短语库与丰富动态占位符**：独立分类收纳常用文字、办公回复与代码模板。支持「微调填入」功能，在粘贴前临时追加或修改内容（保留模版占位符、`Ctrl + Enter` 快捷粘贴，无需反复新建短语）。支持丰富的动态占位符并智能忽略大小写：
  - 日期与时间：`{date}`、`{time}`、`{datetime}`、`{year}`、`{month}`、`{day}`、`{weekday}`（如 星期六）、`{week}`（如 周六）
  - 时间戳：`{timestamp}`（秒级）、`{timestamp_ms}`（毫秒级）
  - 唯一标识：`{guid}` / `{uuid}`（标准 36 位连字符）、`{guid_n}`（32 位无连字符）
  - 系统与用户信息：`{username}` / `{user}`、`{computername}` / `{device}`
  - 随机数与剪贴板：`{random4}` / `{random6}`（随机验证码）、`{clipboard}`（当前剪贴板文本）
- **桌面贴图置顶（一键贴图）**：支持在历史图片或截图卡片上一键贴图置顶，将重要参考图以无边框便签形式固定在屏幕最顶层。支持鼠标滚轮无级缩放（20%~400%）、透明度自由调节、任意拖拽与一键关闭。
- **二维码双向互通**：
  - 文本/链接条目一键生成高清二维码，方便移动端手机扫码互传；
  - 复制包含二维码的图片时，后台毫秒级自动解析提取内部文本与链接；支持一键复制，或直接双击二维码预览色块立即粘贴至目标窗口。
- **中文拼音首字母检索**：支持拼音首字母模糊搜索（如输入 `sjjg` 即可极速定位 `设计架构`），海量历史秒级查找。
- **离线与视觉大模型双模 OCR**：内置 Windows 原生 OCR 引擎，同时兼容 PP-OCRv6 本地离线模型及兼容 OpenAI 协议的视觉大模型接口；识别结果可直接原位编辑后再复制。
- **智能文本翻译**：支持中英文及多语种双向翻译，卡片即时展开译文，支持直接替换粘贴；优先调用用户配置的本地或在线大模型，未配置时自动降级至微软翻译或 Google 翻译公共接口。
- **格式保留**：同时保存剪贴板里的 `CF_HTML` / `RTF` 原文，普通粘贴回 Word / 浏览器时格式不丢；`Shift + Enter` 仍可强制纯文本。
- **现代 Fluent 设计与多主题**：内置 Terminal 终端灰黑、One Dark、GitHub Dark、GitHub Light、Nord 等多套精美主题。

---

## 📸 功能视觉展示

### 1. 核心面板与快捷操作
主面板汇集最近剪贴历史，按文本、链接、图片等类型清晰分块。支持 `1 ~ 9` 数字键一键快速粘贴至目标窗口。

<p align="center">
  <img src="docs/assets/preview.png" width="600" alt="QuickClip 主面板">
</p>

### 2. 图片缩略与大图悬停预览
无需打开第三方图像浏览工具，复制到剪贴板的图片会自动以卡片形式展示，悬停即可查看高清大图细节。

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
| `Ctrl + Shift + S` | 开启 / 关闭连续粘贴栈模式（收集栈切换） |
| `1 ~ 9` | 快速粘贴对应序号的历史条目 |
| `↑` / `↓` | 列表项上下选择 |
| `Enter` | 将当前选中项粘贴至前台目标窗口（短语微调窗中为正常换行） |
| `Ctrl + Enter` | 常用短语「微调填入」窗口中快速应用并粘贴 |
| `Shift + Enter` | 将当前选中项强制以纯文本格式粘贴 |
| `Ctrl + C` | 复制当前选中项内容到系统剪贴板 |
| `Ctrl + P` | 切换窗口置顶状态（置顶后失焦不隐藏） |
| `Delete` | 删除当前选中项 |
| `Esc` | 隐藏主面板 / 退出收集栈 / 关闭贴图 / 关闭弹窗 |

---

## 下载与安装

### 预编译安装包

前往 Release 页面下载最新安装包：

- **官方首发**：[GitHub Releases](https://github.com/soldier-cv/quick-clip/releases)
- **国内高速镜像**：[Gitee Releases](https://gitee.com/huaxudong/quick-clip/releases)（国内无障碍高速下载）

| 安装包 | 说明 | 运行前置条件 |
| :--- | :--- | :--- |
| `QuickClip-Setup-win-x64.exe` | 轻量安装包，体积小 | 需安装 [.NET 8 桌面运行时 x64](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe) |

> 软件内置智能静默更新机制（默认采用 **Gitee 国内通道优先，GitHub 备用降级** 策略，国内秒级响应、无障碍直连）。检测到新版本后会自动静默下载并提示，点击「立即更新」即可平滑完成升级。

---

## 从源码构建

### 环境要求
- Windows 10 (1809 及以上) / Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 构建命令

```powershell
# 1. 本地运行与调试
dotnet run --project src/QuickClip/QuickClip.csproj

# 2. 构建发布包 (配合 Inno Setup 编译 setup/QuickClip.iss 生成安装包)
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

## 致谢与灵感来源

### 🤝 贡献者 (Contributors)

感谢所有为 QuickClip 做出贡献的开发者！

<p align="left">
  <a href="https://github.com/wlight" target="_blank" title="@wlight">
    <img src="https://github.com/wlight.png?size=96" width="54" height="54" alt="@wlight" style="border-radius: 50%; margin-right: 8px;" />
  </a>
</p>

### 💡 灵感与优秀设计致谢
QuickClip 的诞生与演进离不开开源社区与优秀生产力工具的启发，特此向以下项目与设计致谢：

- **OneClip**：优秀的剪贴板交互理念与轻量化产品设计，为 QuickClip 在多任务连续粘贴与收集流设计上提供了宝贵灵感。
- **Snipaste**：卓越的贴图置顶与交互体验典范，为 QuickClip 的桌面贴图、缩放与透明度调节功能提供了参考与启发。
- **WPF-UI**：为 WPF 生态带来优美、现代的 Fluent Design 设计语言与控件库。

---

## 开源协议

本项目基于 [MIT 许可证](LICENSE) 分发。商业使用、修改与衍生均受允许，只需在衍生作品中保留原版权声明与许可文件。

