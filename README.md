# QuickClip

Windows 本地剪贴板管理器。安装后按 `Win + V` 打开面板，复制过的文字、链接、图片都会记下来，再选一条贴回去。数据只存在本机，不上传。

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6.svg)](docs/SPECIFICATION.md#五-系统兼容性渲染降级与运行环境)
[![Gitee](https://img.shields.io/badge/Gitee-QuickClip-C71D23.svg)](https://gitee.com/huaxudong/quick-clip)

<p align="center">
  <img src="docs/assets/preview.png" width="560" alt="QuickClip 主面板">
</p>

---

## 下载安装

1. 先装 [.NET 8 桌面运行时 x64](https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe)（已装过可跳过）。
2. 下载 `QuickClip-Setup-win-x64.exe`：
   - [Gitee Releases](https://gitee.com/huaxudong/quick-clip/releases)（国内）
   - [GitHub Releases](https://github.com/soldier-cv/quick-clip/releases)
3. 安装后托盘会出现图标。关掉主窗口不会退出，要从托盘右键选「退出」。

需要开机自动运行时，打开设置勾选「开机自启动」。

---

## 使用说明

### 1. 日常复制粘贴

1. 在任意软件里复制（`Ctrl + C`），QuickClip 会自动记下。
2. 点要粘贴的位置，按 `Win + V` 打开面板。
3. 点选一条，再 `Enter` 或双击，内容会贴进刚才那个窗口。

也可以：

- 按 `1` ~ `9` 直接贴列表里第 1～9 条
- `Shift + Enter` 只贴纯文本（不要网页/Word 的格式）
- `Ctrl + C` 只把这条拷回系统剪贴板，不立刻粘贴
- 搜索框输入关键字，或拼音首字母（如 `sjjg` 找「设计架构」）

### 2. 窗口置顶（连续往同一个窗口贴）

填表、对照文档时，先 `Ctrl + P` 或点标题栏图钉，让面板钉住。  
之后失焦不会收起，粘贴后也不会自动关掉。贴完用方向键自己选下一条。

不置顶时，粘贴后面板会收起，避免挡着工作。

### 3. 收集栈（按复制顺序逐条粘贴）

适合：从网页/表格里连续复制多个字段，再到另一个窗口按顺序填。

1. 按 `Ctrl + Shift + S` 打开收集栈，桌面右下会出现小浮条。
2. 依次复制要填的内容（或把一段多行文字「拆分」成多条）。
3. 点目标输入框，按 `Ctrl + V`：每按一次贴下一条，贴完自动停。
4. 再按一次 `Ctrl + Shift + S`，或点浮条退出。

### 4. 常用短语

面板顶部切到「常用短语」，用来存固定回复、代码片段、办公模板。

- 粘贴时会自动替换占位符，例如 `{date}` `{time}` `{guid}` `{username}` `{clipboard}`
- 需要临时改几个字时用「微调填入」，改完 `Ctrl + Enter` 贴出，原模板不变

完整占位符见面板帮助或设置页说明。

### 5. 图片、二维码、OCR、翻译、贴图

| 你想做的事 | 怎么做 |
| :--- | :--- |
| 看大图 | 悬停图片卡片 |
| 把图钉在桌面上对照 | 卡片「贴图」：滚轮缩放，`Ctrl + 滚轮` 调透明度，`Esc` 或双击关掉 |
| 把文字发给手机 | 悬停文本/链接卡片，扫弹出的二维码 |
| 识别截图里的二维码 | 复制那张图，卡片上会抽出链接/文本，可复制或双击色块直接粘贴 |
| 图转文字 | 图片卡片点 OCR，结果可先改再复制 |
| 翻译 | 文本卡片点翻译，展开译文后可替换粘贴 |

OCR / 翻译可在设置里选系统自带、离线模型或自己的 AI 接口。不配也能用系统 OCR 和微软 / Google 翻译。

### 6. 设置与托盘

托盘右键：打开面板、暂停捕获、检查更新、退出。  
复制密码等不想留下记录时，勾选「暂停捕获」；面板底部会显示「已暂停捕获」。

设置里可改主题、字体、快捷键、历史条数上限，以及是否接管系统 `Win + V`。接管前会备份系统剪贴板相关设置，关闭接管、退出或卸载时按备份还原。

---

## 快捷键

除 `Win + V` 和 `1 ~ 9` 外，其余可在设置里改。

| 按键 | 作用 |
| :--- | :--- |
| `Win + V` | 打开 / 隐藏面板 |
| `Enter` | 粘贴选中项 |
| `Shift + Enter` | 纯文本粘贴选中项 |
| `1` ~ `9` | 粘贴第 1～9 条 |
| `↑` / `↓` | 上下选择 |
| `Ctrl + C` | 复制选中项到系统剪贴板 |
| `Delete` | 删除选中项 |
| `Ctrl + P` | 窗口置顶 |
| `Ctrl + Shift + V` | 全局：把剪贴板最新内容以纯文本贴出 |
| `Ctrl + Shift + S` | 开/关收集栈 |
| `Ctrl + Enter` | 短语微调窗：应用并粘贴 |
| `Esc` | 隐藏面板 / 退出收集栈 / 关掉贴图或弹窗 |

---

## 常见问题

**按 `Win + V` 没反应？**  
看托盘有没有 QuickClip。没有就从开始菜单再开一次。若弹出的是 Windows 自带剪贴板，到设置里启用「接管系统剪贴板」。管理员窗口下系统可能拦热键，点一下目标窗口后再试。

**手机复制的内容电脑上看不到？**  
QuickClip 不自己做云同步。用微信输入法、系统互联等把内容同步到电脑剪贴板后，会自动出现在列表里。

**数据在哪？会不会上传？**  
全部在 `%LOCALAPPDATA%\QuickClip\`（历史库、缩略图、设置、更新包、日志）。不注册、不上传剪贴内容。

**怎么彻底退出 / 卸载？**  
退出：托盘右键「退出」。卸载用系统「应用和功能」，会按备份还原系统剪贴板设置。

**更新怎么装？**  
设置 → 关于 →「检查更新…」。发现新版本会自动下载，点「立即更新」后退出并静默安装。

---

## 功能一览

- 接管 `Win + V`，记录文本 / 链接 / 图片 / 文件，保留 HTML / RTF 格式
- 拼音首字母搜索，条目可钉在列表顶部
- 收集栈顺序粘贴，常用短语 + 动态占位符 + 微调填入
- 桌面贴图、二维码生成与识别、OCR、翻译
- 多套主题；开机自启会后台预热，减少第一次按 `Win + V` 的等待

<p align="center">
  <img src="docs/assets/preview-image.png" width="560" alt="图片预览">
</p>

<p align="center">
  <img src="docs/assets/qr-generate.png" width="360" alt="生成二维码">
  &nbsp;
  <img src="docs/assets/qr-decode.png" width="360" alt="识别二维码">
</p>

<p align="center">
  <img src="docs/assets/themes.png" width="720" alt="主题">
</p>

---

## 从源码构建

Windows 10 1809+ / 11，安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet run --project src/QuickClip/QuickClip.csproj

dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=false -o publish/fdd
```

安装包用 Inno Setup 编译 `setup/QuickClip.iss`。规格与开发约定见 [docs/SPECIFICATION.md](docs/SPECIFICATION.md)、[AGENT.md](AGENT.md)，版本记录见 [CHANGELOG.md](CHANGELOG.md)。

---

## 致谢

感谢 [@wlight](https://github.com/wlight) 以及所有反馈、Star 和推荐的朋友。交互上参考了 OneClip、Snipaste 与 [WPF-UI](https://github.com/lepoco/wpfui)。

本项目基于 [MIT 许可证](LICENSE)。
