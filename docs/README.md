# QuickClip 文档

## 文档索引

| 文档 | 说明 |
| --- | --- |
| [DESIGN.md](DESIGN.md) | 架构与实现设计 |
| [COMPATIBILITY.md](COMPATIBILITY.md) | 系统兼容性（仅 Win10/11 x64） |
| [SECURITY.md](SECURITY.md) | 隐私、密钥与日志约定 |
| [assets/](assets/) | README 配图、品牌图标 |
| [scripts/capture-preview.ps1](scripts/capture-preview.ps1) | 截取主面板预览图 |
| [preview.html](preview.html) | 早期 Fluent 交互 HTML 原型 |
| [themes-preview.html](themes-preview.html) | 主题色板切换原型 |
| [icon-concepts/](icon-concepts/) | 应用图标生成脚本（可选） |

## 仓库结构

```
quick-clip/
├── .github/
│   ├── workflows/
│   │   ├── ci.yml          # push/PR：编译 + 发布冒烟
│   │   └── release.yml     # tag v*：打包并创建 GitHub Release
│   ├── ISSUE_TEMPLATE/
│   └── pull_request_template.md
├── docs/                   # 文档与静态资源
├── src/QuickClip/          # WPF 主工程
├── QuickClip.sln
├── CHANGELOG.md
├── CONTRIBUTING.md
├── LICENSE                 # MIT
└── README.md
```

## CI / 发版

| 触发 | 工作流 | 结果 |
| --- | --- | --- |
| `push` / PR → `master` | `CI` | Debug 编译 + Release 发布冒烟 |
| `git tag v1.0.0 && push --tags` | `Build and Release` | 产出 `QuickClip.exe` 并创建 Release |

```powershell
# 本地调试
dotnet run --project src/QuickClip/QuickClip.csproj

# 本地发版同构
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

构建产物（`bin/`、`obj/`、`publish/`）与运行时数据（`%LOCALAPPDATA%\QuickClip\`）不纳入版本库。

## 许可证

**[MIT License](../LICENSE)**
