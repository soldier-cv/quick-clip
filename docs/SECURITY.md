# 安全与隐私说明

面向开源协作者与使用者的简要约定。

## 数据落盘位置

所有用户数据仅在本机：

```
%LOCALAPPDATA%\QuickClip\
  quickclip.db      # 剪贴板历史
  previews\         # 图片缩略图
  settings.json     # 热键、自启动、OCR、自动更新等
  updates\          # 已下载的更新包
  debug.log         # 诊断日志
```

仓库与发布包**不包含**上述文件（见 `.gitignore`）。

## 敏感信息

| 项目 | 处理 |
| --- | --- |
| 剪贴板正文 | 仅存本地 SQLite，**不**写入 `debug.log` |
| OpenAI API Key | 仅存本地 `settings.json`；设置页用 PasswordBox；**禁止**写入日志 |
| 网络 | 日常离线。默认会静默访问 GitHub Releases（可关）；用户自选的 Ollama/OpenAI OCR 也会联网。更新包仅从 `github.com` / `*.githubusercontent.com` 下载 |

## 贡献时注意

- 不要在 Issue / PR 中粘贴真实 API Key 或他人剪贴板内容
- 新增日志时只记录长度、类型、路径等元数据，不记录正文与密钥
- 不要提交本机 `settings.json`、`*.db`、`debug.log`

## 漏洞反馈

若发现安全问题，请优先通过私密渠道联系维护者（或 GitHub Security Advisory），避免在公开 Issue 中披露可利用细节。
