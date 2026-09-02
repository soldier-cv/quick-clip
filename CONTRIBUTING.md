# 贡献指南

欢迎 fork、提 Issue 与 PR。本项目采用 **MIT** 许可，你可以自由改造与商用。

## 开发环境

- Windows 10 1809+ / Windows 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
dotnet restore QuickClip.sln
dotnet run --project src/QuickClip/QuickClip.csproj
```

## 代码约定

1. **语言**：C# / WPF，文件作用域命名空间，可空引用类型开启。
2. **注释**：公共类型与非显而易见的业务逻辑写清「为什么」；避免废话注释。
3. **日志**：使用 `DebugLog`，**禁止**把 API Key、用户剪贴板全文等敏感信息写入日志。易错路径（下载 HTTP 状态、校验失败、接口超时）要记，URL 只留 host+path。日志会滚动，不要改成无限追加。
4. **设置**：用户数据只落在 `%LOCALAPPDATA%\QuickClip\`，不要写进仓库。
5. **异常**：边界处捕获并记录；不要空 `catch` 后静默吞掉关键失败（日志模块本身除外）。
6. **平台**：不引入 Win7 依赖；联网仅限更新检查（默认可关）、用户自选的云端 OCR，以及用户主动下载的离线模型包。
7. **格式**：遵循仓库根目录 `.editorconfig`。

## 提交前自检

```powershell
dotnet build QuickClip.sln -c Debug
# 有改动时再跑：
dotnet publish src/QuickClip/QuickClip.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

- 不要提交 `bin/`、`obj/`、`publish/`、本地 `settings.json` 或密钥。
- PR 说明「改了什么 / 为什么 / 如何验证」。

## 许可

贡献代码默认以 **MIT** 授权合入本仓库。
