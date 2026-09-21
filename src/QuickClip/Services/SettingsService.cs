using System.IO;
using System.Text.Json;
using System.Windows.Input;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>应用设置：本地 JSON 持久化（%LOCALAPPDATA%\QuickClip\settings.json），变更后自动保存并广播事件。</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 兼容手写或第三方工具生成的小写键名
        PropertyNameCaseInsensitive = true
    };

    private readonly string _settingsPath;

    /// <summary>设置发生变更（已保存到磁盘）时触发，供热键注册、自启动等联动。</summary>
    public event Action? Changed;

    /// <summary>全局“纯文本粘贴”热键组合（固定 Ctrl+Shift+V，不可改键）。</summary>
    public HotkeyBinding PlainPasteHotkey { get; private set; } = HotkeyBinding.PlainPasteDefault;

    /// <summary>是否启用全局纯文本粘贴热键。</summary>
    public bool PlainPasteEnabled { get; private set; } = true;

    /// <summary>是否开机自启动。</summary>
    public bool AutoStart { get; private set; }

    /// <summary>
    /// 是否接管系统剪贴板（关闭 Windows 剪贴板历史 + 让 Explorer 释放 Win+V）。
    /// 默认开启；关闭时按接管前快照恢复系统状态，且之后启动不再接管。
    /// </summary>
    public bool TakeOverSystemClipboard { get; private set; } = true;

    /// <summary>外观主题（默认 Terminal）。</summary>
    public AppTheme Theme { get; private set; } = AppTheme.Terminal;

    /// <summary>Toast 提示卡片尺寸（默认 Medium 标准）。</summary>
    public ToastSize ToastSize { get; private set; } = ToastSize.Medium;

    public void SetToastSize(ToastSize size)
    {
        if (ToastSize == size) return;
        ToastSize = size;
        Save();
    }

    /// <summary>主窗口是否前端置顶（固定在最前，失焦不自动隐藏）。仅由 Ctrl+P / 图钉切换，设置页不再暴露。</summary>
    public bool WindowAlwaysOnTop { get; private set; }

    /// <summary>是否记忆面板拖拽后的位置（默认 true）。</summary>
    public bool RememberWindowPosition { get; private set; } = true;

    /// <summary>上次面板位置 X 坐标（null 表示默认屏幕右侧）。</summary>
    public double? WindowPositionX { get; private set; }

    /// <summary>上次面板位置 Y 坐标（null 表示默认屏幕垂直居中）。</summary>
    public double? WindowPositionY { get; private set; }

    /// <summary>更新面板位置并持久化保存。</summary>
    public void SetWindowPosition(double x, double y)
    {
        if (WindowPositionX == x && WindowPositionY == y) return;
        WindowPositionX = x;
        WindowPositionY = y;
        Save(raiseChanged: false);
    }

    /// <summary>连续粘贴模式：按 Enter 粘贴后保持面板激活，并自动选至下一项，方便连续粘贴多条内容。</summary>
    public bool ContinuousPasteMode { get; private set; }

    /// <summary>
    /// 剪贴板历史数据库位置（null 表示默认本地库）。
    /// 设置页已不再支持自定义；仍读取旧配置以兼容已有 settings.json。
    /// </summary>
    public string? DatabasePath { get; private set; }

    /// <summary>OCR 识别引擎。</summary>
    public OcrEngineType OcrEngine { get; private set; } = OcrEngineType.System;

    /// <summary>离线模型档（仅引擎为 Local 时生效）。默认高精度 Medium。</summary>
    public OcrLocalPack OcrLocalPack { get; private set; } = OcrLocalPack.Medium;

    /// <summary>自定义离线模型目录（仅 OcrLocalPack=Custom）。</summary>
    public string OcrCustomDir { get; private set; } = string.Empty;

    // ---------- 模型配置（支持多组配置项 / OpenAI 兼容协议 / Ollama） ----------

    /// <summary>多组模型配置项列表。</summary>
    public List<AiProfile> AiProfiles { get; private set; } = new();

    /// <summary>OCR 识别当前绑定的模型配置 ID。</summary>
    public string? OcrProfileId { get; private set; }

    /// <summary>文本翻译当前绑定的模型配置 ID。</summary>
    public string? TranslationProfileId { get; private set; }

    /// <summary>获取 OCR 绑定的模型配置（无匹配时回退首个配置或默认配置）。</summary>
    public AiProfile GetOcrProfile()
    {
        return GetProfile(OcrProfileId) ?? AiProfiles.FirstOrDefault() ?? new AiProfile();
    }

    /// <summary>获取文本翻译绑定的模型配置（无匹配时回退首个配置或默认配置）。</summary>
    public AiProfile GetTranslationProfile()
    {
        return GetProfile(TranslationProfileId) ?? AiProfiles.FirstOrDefault() ?? new AiProfile();
    }

    /// <summary>根据 ID 获取指定模型配置。</summary>
    public AiProfile? GetProfile(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return AiProfiles.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>添加新模型配置并持久化。</summary>
    public void AddAiProfile(AiProfile profile)
    {
        if (profile == null) return;
        AiProfiles.Add(profile);
        Save();
    }

    /// <summary>更新已有模型配置并持久化。</summary>
    public void UpdateAiProfile(AiProfile profile)
    {
        if (profile == null) return;
        int idx = AiProfiles.FindIndex(p => string.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            AiProfiles[idx] = profile;
            Save();
        }
    }

    /// <summary>删除指定模型配置（至少保留一组）。</summary>
    public void DeleteAiProfile(string profileId)
    {
        if (AiProfiles.Count <= 1) return;
        int removed = AiProfiles.RemoveAll(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            if (string.Equals(OcrProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                OcrProfileId = AiProfiles[0].Id;
            }
            if (string.Equals(TranslationProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            {
                TranslationProfileId = AiProfiles[0].Id;
            }
            Save();
        }
    }

    /// <summary>更新 OCR 绑定的模型配置 ID 并持久化。</summary>
    public void SetOcrProfileId(string? profileId)
    {
        if (OcrProfileId == profileId) return;
        OcrProfileId = profileId;
        Save();
    }

    /// <summary>更新文本翻译绑定的模型配置 ID 并持久化。</summary>
    public void SetTranslationProfileId(string? profileId)
    {
        if (TranslationProfileId == profileId) return;
        TranslationProfileId = profileId;
        Save();
    }

    // 向下兼容别名与全局委托
    public string AiApiUrl => GetTranslationProfile().ApiUrl;
    public string? AiApiKey => GetTranslationProfile().ApiKey;
    public Dictionary<string, List<string>> AiCachedModels { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public string VisionApiUrl => GetOcrProfile().ApiUrl;
    public string? VisionApiKey => GetOcrProfile().ApiKey;
    public Dictionary<string, List<string>> VisionApiCachedModels => AiCachedModels;

    public IReadOnlyList<string> GetCachedModelsForUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Array.Empty<string>();
        }

        string key = NormalizeVisionApiUrlKey(url);
        return AiCachedModels.TryGetValue(key, out var list) && list != null
            ? list
            : Array.Empty<string>();
    }

    public void SetCachedModelsForUrl(string? url, IEnumerable<string> models)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        string key = NormalizeVisionApiUrlKey(url);
        var list = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
        {
            return;
        }

        AiCachedModels[key] = list;
        Save();
    }

    private static string NormalizeVisionApiUrlKey(string url) => url.Trim().TrimEnd('/');

    // ---------- OCR 识别专属配置 ----------

    /// <summary>OCR 识别所使用的 AI 视觉模型名（需支持图片输入）。</summary>
    public string OcrVisionModel { get; private set; } = DefaultOcrVisionModel;

    /// <summary>OCR 视觉接口识别提示词（Prompt）。</summary>
    public string OcrPrompt { get; private set; } = DefaultOcrPrompt;

    // 向下兼容别名
    public string VisionApiModel => OcrVisionModel;
    public string VisionApiPrompt => OcrPrompt;

    public const string DefaultOcrPrompt =
        "请精确识别图片中的全部文字与内容，严格保留原始版面结构：\n" +
        "1. 表格内容必须提取并整理为标准的 Markdown 表格；\n" +
        "2. 并列的卡片、表单或统计数据，请保持对应关系，以“标签: 数值”的键值对形式呈现；\n" +
        "3. 保留标题和层级关系，不要输出多余的解释或问候，直接输出排版结果。";

    public const string DefaultVisionApiPrompt = DefaultOcrPrompt;

    // ---------- 文本翻译专属配置 ----------

    /// <summary>文本翻译引擎（微软公共通道 / Google公共通道 / AI 大模型翻译）。</summary>
    public TranslationEngineType TranslationEngine { get; private set; } = TranslationEngineType.Bing;

    /// <summary>文本翻译所使用的 AI 模型名。</summary>
    public string TranslationModel { get; private set; } = DefaultTranslationModel;

    /// <summary>文本翻译提示词（支持 {target_lang} 占位符）。</summary>
    public string TranslationPrompt { get; private set; } = DefaultTranslationPrompt;

    public const string DefaultTranslationPrompt =
        "你是一位精通多国语言的专业翻译官。请将用户提供的文本准确翻译为目标语言：{target_lang}。\n" +
        "要求：\n" +
        "1. 翻译风格地道自然、通顺优雅，符合目标语言的表达习惯；\n" +
        "2. 严格保留原文的段落格式、排版结构、代码块、Markdown 格式及特殊符号；\n" +
        "3. 仅输出翻译后的文本内容，不要包含任何额外的问候、解释或标注。";

    private const string DefaultAiApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string DefaultOcrVisionModel = "gpt-4o-mini";
    private const string DefaultTranslationModel = "gpt-4o-mini";
    private const string DefaultVisionApiUrl = DefaultAiApiUrl;
    private const string DefaultVisionApiModel = DefaultOcrVisionModel;
    private const string DefaultOllamaUrl = "http://localhost:11434/api/generate";
    private const string DefaultOllamaModel = "llava";

    /// <summary>历史最大条数（含置顶；超出淘汰最旧非置顶）。默认 233。</summary>
    public int MaxHistoryItems { get; private set; } = 233;

    /// <summary>仅记录文本/链接，忽略图片与文件。</summary>
    public bool TextOnlyCapture { get; private set; }

    /// <summary>暂停捕获：临时不记录任何剪贴板内容（复制密码等敏感内容时使用）。</summary>
    public bool CapturePaused { get; private set; }

    /// <summary>启动后延迟检查 GitHub 并下载对应渠道安装包。默认开启。</summary>
    public bool AutoCheckUpdates { get; private set; } = true;

    /// <summary>上次静默检查时间（UTC）。用于 24 小时节流。</summary>
    public DateTime? LastUpdateCheckUtc { get; private set; }

    public const int DefaultMaxHistoryItems = 233;
    public const int MinMaxHistoryItems = 50;
    public const int AbsoluteMaxHistoryItems = 2000;

    // ---------- 捕获体积上限（仅决定是否写入历史；绝不改写系统剪贴板，粘贴到别处不受影响） ----------

    /// <summary>单条文本/链接最大字符数；超限不记历史。</summary>
    public const int MaxCaptureTextChars = 2 * 1024 * 1024; // 2M chars ≈ 大段文本

    /// <summary>图片落盘预览最大字节；超限不记历史（系统剪贴板位图仍在，可正常粘贴）。</summary>
    public const long MaxCaptureImageBytes = 30L * 1024 * 1024; // 30 MB

    /// <summary>图片像素上限（宽×高）；超限不解码落盘，避免超大图拖死进程。</summary>
    public const long MaxCaptureImagePixels = 40L * 1000 * 1000; // 40MP

    // ---------- 面板内快捷键（面板获得焦点时生效） ----------

    public HotkeyBinding PasteSelectedHotkey { get; private set; } = HotkeyBinding.PasteSelectedDefault;
    public HotkeyBinding PasteSelectedPlainHotkey { get; private set; } = HotkeyBinding.PasteSelectedPlainDefault;
    public HotkeyBinding CopySelectedHotkey { get; private set; } = HotkeyBinding.CopySelectedDefault;
    public HotkeyBinding TogglePinHotkey { get; private set; } = HotkeyBinding.TogglePinDefault;
    public HotkeyBinding DeleteSelectedHotkey { get; private set; } = HotkeyBinding.DeleteSelectedDefault;
    public HotkeyBinding HidePanelHotkey { get; private set; } = HotkeyBinding.HidePanelDefault;
    public HotkeyBinding MoveUpHotkey { get; private set; } = HotkeyBinding.MoveUpDefault;
    public HotkeyBinding MoveDownHotkey { get; private set; } = HotkeyBinding.MoveDownDefault;
    public HotkeyBinding StartStackHotkey { get; private set; } = HotkeyBinding.StartStackDefault;
    public HotkeyBinding StopStackHotkey { get; private set; } = HotkeyBinding.StopStackDefault;
    public HotkeyBinding StackModeHotkey => StartStackHotkey;
    public string TranslationTargetLanguage { get; private set; } = "zh";

    public SettingsService(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    /// <summary>从磁盘加载设置；文件缺失或解析失败时回退默认值。</summary>
    public void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            var dto = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_settingsPath), JsonOptions);
            if (dto == null)
            {
                return;
            }

            // 全局纯文本粘贴固定 Ctrl+Shift+V；旧版自定义组合忽略
            PlainPasteHotkey = HotkeyBinding.PlainPasteDefault;
            PlainPasteEnabled = dto.PlainPasteEnabled ?? true;
            AutoStart = dto.AutoStart ?? false;
            TakeOverSystemClipboard = dto.TakeOverSystemClipboard ?? true;
            WindowAlwaysOnTop = dto.WindowAlwaysOnTop ?? false;
            ContinuousPasteMode = dto.ContinuousPasteMode ?? false;
            RememberWindowPosition = dto.RememberWindowPosition ?? true;
            WindowPositionX = dto.WindowPositionX;
            WindowPositionY = dto.WindowPositionY;
            Theme = ParseTheme(dto.Theme);
            if (dto.ToastSize is { } toastSizeStr && Enum.TryParse<ToastSize>(toastSizeStr, true, out var toastSize))
            {
                ToastSize = toastSize;
            }
            DatabasePath = string.IsNullOrWhiteSpace(dto.DatabasePath) ? null : dto.DatabasePath;

            if (dto.OcrEngine is { } engineName)
            {
                OcrEngine = ParseOcrEngine(engineName);
            }

            if (dto.OcrLocalPack is { } packName && Enum.TryParse<OcrLocalPack>(packName, out var pack))
            {
                OcrLocalPack = pack;
            }

            if (!string.IsNullOrWhiteSpace(dto.OcrCustomDir))
            {
                OcrCustomDir = dto.OcrCustomDir.Trim();
            }

            ApplyAiAndFeatureSettingsFromDto(dto, dto.OcrEngine);

            TextOnlyCapture = dto.TextOnlyCapture ?? false;
            CapturePaused = dto.CapturePaused ?? false;
            AutoCheckUpdates = dto.AutoCheckUpdates ?? true;
            LastUpdateCheckUtc = ParseUtc(dto.LastUpdateCheckUtc);
            MaxHistoryItems = ClampMaxHistory(dto.MaxHistoryItems ?? DefaultMaxHistoryItems);
            TranslationTargetLanguage = string.IsNullOrWhiteSpace(dto.TranslationTargetLanguage) ? "zh" : dto.TranslationTargetLanguage.Trim();

            ApplyPanelHotkeys(dto.PanelHotkeys);

            DebugLog.Log(
                $"已加载设置: 纯文本粘贴={PlainPasteHotkey}({(PlainPasteEnabled ? "启用" : "禁用")}), " +
                $"自启动={AutoStart}, 主题={Theme}, 窗口置顶={WindowAlwaysOnTop}, OCR={OcrEngine}/{OcrLocalPack} " +
                $"AI={DebugLog.DescribeUrl(AiApiUrl)}, OCR模型={OcrVisionModel}, 翻译引擎={TranslationEngine}/{TranslationModel}");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("加载设置失败，使用默认值", ex);
        }
    }

    /// <summary>解析主题；未知或已移除的 Dracula 回退 Terminal。</summary>
    private static AppTheme ParseTheme(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return AppTheme.Terminal;
        }

        if (name.Equals("Dracula", StringComparison.OrdinalIgnoreCase))
        {
            return AppTheme.Terminal;
        }

        return Enum.TryParse(name, ignoreCase: true, out AppTheme theme) && Enum.IsDefined(theme)
            ? theme
            : AppTheme.Terminal;
    }

    /// <summary>更新外观主题并持久化，立即应用到 UI。</summary>
    public void SetTheme(AppTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            theme = AppTheme.Terminal;
        }

        if (Theme == theme)
        {
            ThemeService.Apply(theme);
            return;
        }

        Theme = theme;
        ThemeService.Apply(theme);
        Save();
    }

    public void SetMaxHistoryItems(int max)
    {
        max = ClampMaxHistory(max);
        if (MaxHistoryItems == max)
        {
            return;
        }

        MaxHistoryItems = max;
        Save();
    }

    public void SetTextOnlyCapture(bool enabled)
    {
        if (TextOnlyCapture == enabled) return;
        TextOnlyCapture = enabled;
        Save();
    }

    /// <summary>暂停 / 恢复剪贴板捕获。</summary>
    public void SetCapturePaused(bool paused)
    {
        if (CapturePaused == paused)
        {
            return;
        }

        CapturePaused = paused;
        Save();
    }

    /// <summary>切换「接管系统剪贴板」；持久化由调用方负责联动注册表。</summary>
    public void SetTakeOverSystemClipboard(bool enabled)
    {
        if (TakeOverSystemClipboard == enabled)
        {
            return;
        }

        TakeOverSystemClipboard = enabled;
        Save();
    }

    public void SetAutoCheckUpdates(bool enabled)
    {
        if (AutoCheckUpdates == enabled)
        {
            return;
        }

        AutoCheckUpdates = enabled;
        Save();
    }

    public void SetLastUpdateCheckUtc(DateTime utc)
    {
        LastUpdateCheckUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        // 时间戳不应广播 Changed：后台线程保存会牵动热键重新注册
        Save(raiseChanged: false);
    }

    private static DateTime? ParseUtc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParse(
            text,
            null,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTime value)
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : null;
    }

    public static int ClampMaxHistory(int value) =>
        Math.Clamp(value, MinMaxHistoryItems, AbsoluteMaxHistoryItems);

    private void ApplyPanelHotkeys(PanelHotkeysData? data)
    {
        if (data == null)
        {
            return;
        }

        if (data.PasteSelected?.ToBinding() is { HasKey: true } paste)
            PasteSelectedHotkey = paste;
        if (data.PasteSelectedPlain?.ToBinding() is { HasKey: true } pastePlain)
            PasteSelectedPlainHotkey = pastePlain;
        if (data.CopySelected?.ToBinding() is { HasKey: true } copy)
            CopySelectedHotkey = copy;
        if (data.TogglePin?.ToBinding() is { HasKey: true } pin)
            TogglePinHotkey = pin;
        if (data.DeleteSelected?.ToBinding() is { HasKey: true } del)
            DeleteSelectedHotkey = del;
        if (data.HidePanel?.ToBinding() is { HasKey: true } hide)
            HidePanelHotkey = hide;
        if (data.MoveUp?.ToBinding() is { HasKey: true } up)
            MoveUpHotkey = up;
        if (data.MoveDown?.ToBinding() is { HasKey: true } down)
            MoveDownHotkey = down;
        if (data.StartStack?.ToBinding() is { HasKey: true } startStack)
            StartStackHotkey = startStack;
        if (data.StopStack?.ToBinding() is { HasKey: true } stopStack)
            StopStackHotkey = stopStack;
    }

    /// <summary>启用/禁用全局纯文本粘贴（组合固定 Ctrl+Shift+V）。</summary>
    public void SetPlainPasteEnabled(bool enabled)
    {
        if (PlainPasteHotkey == HotkeyBinding.PlainPasteDefault && PlainPasteEnabled == enabled)
        {
            return;
        }

        PlainPasteHotkey = HotkeyBinding.PlainPasteDefault;
        PlainPasteEnabled = enabled;
        Save();
    }

    /// <summary>更新开机自启动状态并持久化。</summary>
    public void SetAutoStart(bool enabled)
    {
        if (AutoStart == enabled)
        {
            return;
        }

        AutoStart = enabled;
        Save();
    }

    /// <summary>更新主窗口前端置顶状态并持久化（由面板快捷键 / 图钉调用）。</summary>
    public void SetWindowAlwaysOnTop(bool enabled)
    {
        if (WindowAlwaysOnTop == enabled)
        {
            return;
        }

        WindowAlwaysOnTop = enabled;
        Save();
    }

    /// <summary>更新连续粘贴模式并持久化。</summary>
    public void SetContinuousPasteMode(bool enabled)
    {
        if (ContinuousPasteMode == enabled)
        {
            return;
        }

        ContinuousPasteMode = enabled;
        Save();
    }

    /// <summary>更新面板内某一快捷键并持久化。</summary>
    public void SetPanelHotkey(PanelHotkeyAction action, HotkeyBinding binding)
    {
        if (GetPanelHotkey(action) == binding)
        {
            return;
        }

        switch (action)
        {
            case PanelHotkeyAction.PasteSelected:
                PasteSelectedHotkey = binding;
                break;
            case PanelHotkeyAction.PasteSelectedPlain:
                PasteSelectedPlainHotkey = binding;
                break;
            case PanelHotkeyAction.CopySelected:
                CopySelectedHotkey = binding;
                break;
            case PanelHotkeyAction.TogglePin:
                TogglePinHotkey = binding;
                break;
            case PanelHotkeyAction.DeleteSelected:
                DeleteSelectedHotkey = binding;
                break;
            case PanelHotkeyAction.HidePanel:
                HidePanelHotkey = binding;
                break;
            case PanelHotkeyAction.MoveUp:
                MoveUpHotkey = binding;
                break;
            case PanelHotkeyAction.MoveDown:
                MoveDownHotkey = binding;
                break;
            case PanelHotkeyAction.StartStack:
                StartStackHotkey = binding;
                break;
            case PanelHotkeyAction.StopStack:
                StopStackHotkey = binding;
                break;
        }

        Save();
    }

    /// <summary>将全部面板快捷键恢复为默认值。</summary>
    public void ResetPanelHotkeys()
    {
        PasteSelectedHotkey = HotkeyBinding.PasteSelectedDefault;
        PasteSelectedPlainHotkey = HotkeyBinding.PasteSelectedPlainDefault;
        CopySelectedHotkey = HotkeyBinding.CopySelectedDefault;
        TogglePinHotkey = HotkeyBinding.TogglePinDefault;
        DeleteSelectedHotkey = HotkeyBinding.DeleteSelectedDefault;
        HidePanelHotkey = HotkeyBinding.HidePanelDefault;
        MoveUpHotkey = HotkeyBinding.MoveUpDefault;
        MoveDownHotkey = HotkeyBinding.MoveDownDefault;
        StartStackHotkey = HotkeyBinding.StartStackDefault;
        StopStackHotkey = HotkeyBinding.StopStackDefault;
        Save();
    }

    public HotkeyBinding GetPanelHotkey(PanelHotkeyAction action) => action switch
    {
        PanelHotkeyAction.PasteSelected => PasteSelectedHotkey,
        PanelHotkeyAction.PasteSelectedPlain => PasteSelectedPlainHotkey,
        PanelHotkeyAction.CopySelected => CopySelectedHotkey,
        PanelHotkeyAction.TogglePin => TogglePinHotkey,
        PanelHotkeyAction.DeleteSelected => DeleteSelectedHotkey,
        PanelHotkeyAction.HidePanel => HidePanelHotkey,
        PanelHotkeyAction.MoveUp => MoveUpHotkey,
        PanelHotkeyAction.MoveDown => MoveDownHotkey,
        PanelHotkeyAction.StartStack => StartStackHotkey,
        PanelHotkeyAction.StopStack => StopStackHotkey,
        _ => HotkeyBinding.PasteSelectedDefault
    };

    public static HotkeyBinding GetPanelHotkeyDefault(PanelHotkeyAction action) => action switch
    {
        PanelHotkeyAction.PasteSelected => HotkeyBinding.PasteSelectedDefault,
        PanelHotkeyAction.PasteSelectedPlain => HotkeyBinding.PasteSelectedPlainDefault,
        PanelHotkeyAction.CopySelected => HotkeyBinding.CopySelectedDefault,
        PanelHotkeyAction.TogglePin => HotkeyBinding.TogglePinDefault,
        PanelHotkeyAction.DeleteSelected => HotkeyBinding.DeleteSelectedDefault,
        PanelHotkeyAction.HidePanel => HotkeyBinding.HidePanelDefault,
        PanelHotkeyAction.MoveUp => HotkeyBinding.MoveUpDefault,
        PanelHotkeyAction.MoveDown => HotkeyBinding.MoveDownDefault,
        PanelHotkeyAction.StartStack => HotkeyBinding.StartStackDefault,
        PanelHotkeyAction.StopStack => HotkeyBinding.StopStackDefault,
        _ => HotkeyBinding.PasteSelectedDefault
    };

    /// <summary>更新 OCR 识别引擎并持久化。</summary>
    public void SetOcrEngine(OcrEngineType engine)
    {
        if (OcrEngine == engine)
        {
            return;
        }

        OcrEngine = engine;
        Save();
    }

    /// <summary>更新离线模型档并持久化。</summary>
    public void SetOcrLocalPack(OcrLocalPack pack)
    {
        if (!Enum.IsDefined(pack))
        {
            pack = OcrLocalPack.Medium;
        }

        if (OcrLocalPack == pack)
        {
            return;
        }

        OcrLocalPack = pack;
        Save();
    }

    /// <summary>更新自定义离线模型目录并持久化。</summary>
    public void SetOcrCustomDir(string? directory)
    {
        string next = string.IsNullOrWhiteSpace(directory) ? string.Empty : directory.Trim();
        if (OcrCustomDir == next)
        {
            return;
        }

        OcrCustomDir = next;
        Save();
    }

    // ---------- AI 与特性配置更新方法 ----------

    /// <summary>更新通用模型配置（首个配置项）并持久化。</summary>
    public void SetAiConfig(string apiUrl, string? apiKey)
    {
        var profile = AiProfiles.FirstOrDefault();
        if (profile == null)
        {
            profile = new AiProfile();
            AiProfiles.Add(profile);
        }

        string nextUrl = string.IsNullOrWhiteSpace(apiUrl)
            ? profile.ApiUrl
            : MigrateVisionEndpoint(apiUrl);
        string? nextKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (profile.ApiUrl == nextUrl && profile.ApiKey == nextKey)
        {
            return;
        }

        profile.ApiUrl = nextUrl;
        profile.ApiKey = nextKey;
        Save();
    }

    /// <summary>更新 OCR 视觉模型名并持久化。</summary>
    public void SetOcrVisionModel(string model)
    {
        string nextModel = string.IsNullOrWhiteSpace(model) ? DefaultOcrVisionModel : model.Trim();
        if (OcrVisionModel == nextModel)
        {
            return;
        }

        OcrVisionModel = nextModel;
        Save();
    }

    /// <summary>更新 OCR 视觉识别提示词并持久化。</summary>
    public void SetOcrPrompt(string prompt)
    {
        string nextPrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultOcrPrompt : prompt.Trim();
        if (OcrPrompt == nextPrompt)
        {
            return;
        }

        OcrPrompt = nextPrompt;
        Save();
    }

    /// <summary>更新文本翻译引擎并持久化。</summary>
    public void SetTranslationEngine(TranslationEngineType engine)
    {
        if (TranslationEngine == engine)
        {
            return;
        }

        TranslationEngine = engine;
        Save();
    }

    /// <summary>更新文本翻译模型名并持久化。</summary>
    public void SetTranslationModel(string model)
    {
        string nextModel = string.IsNullOrWhiteSpace(model) ? DefaultTranslationModel : model.Trim();
        if (TranslationModel == nextModel)
        {
            return;
        }

        TranslationModel = nextModel;
        Save();
    }

    /// <summary>更新文本翻译提示词并持久化。</summary>
    public void SetTranslationPrompt(string prompt)
    {
        string nextPrompt = string.IsNullOrWhiteSpace(prompt) ? DefaultTranslationPrompt : prompt.Trim();
        if (TranslationPrompt == nextPrompt)
        {
            return;
        }

        TranslationPrompt = nextPrompt;
        Save();
    }

    /// <summary>更新文本翻译目标语言并持久化。</summary>
    public void SetTranslationTargetLanguage(string lang)
    {
        string nextLang = string.IsNullOrWhiteSpace(lang) ? "zh" : lang.Trim();
        if (TranslationTargetLanguage == nextLang)
        {
            return;
        }

        TranslationTargetLanguage = nextLang;
        Save();
    }

    /// <summary>
    /// 旧版视觉接口更新方法：同步更新通用 AI 配置及 OCR 视觉模型和提示词。
    /// </summary>
    public void SetVisionApiConfig(string baseUrl, string model, string? apiKey, string? prompt = null)
    {
        var profile = GetOcrProfile();
        string nextUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? profile.ApiUrl
            : MigrateVisionEndpoint(baseUrl);
        string nextModel = string.IsNullOrWhiteSpace(model) ? OcrVisionModel : model.Trim();
        string? nextKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        string nextPrompt = string.IsNullOrWhiteSpace(prompt) ? OcrPrompt : prompt.Trim();

        bool changed = false;
        if (profile.ApiUrl != nextUrl || profile.ApiKey != nextKey)
        {
            profile.ApiUrl = nextUrl;
            profile.ApiKey = nextKey;
            changed = true;
        }
        if (OcrVisionModel != nextModel)
        {
            OcrVisionModel = nextModel;
            changed = true;
        }
        if (OcrPrompt != nextPrompt)
        {
            OcrPrompt = nextPrompt;
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    /// <summary>更新视觉接口识别提示词并持久化（兼容旧方法）。</summary>
    public void SetVisionApiPrompt(string prompt) => SetOcrPrompt(prompt);

    /// <summary>旧版 Ollama / OpenAI 枚举合并为 VisionApi。</summary>
    internal static OcrEngineType ParseOcrEngine(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return OcrEngineType.System;
        }

        if (name.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OpenAi", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("VisionApi", StringComparison.OrdinalIgnoreCase))
        {
            return OcrEngineType.VisionApi;
        }

        return Enum.TryParse(name, ignoreCase: true, out OcrEngineType engine)
            ? engine
            : OcrEngineType.System;
    }

    /// <summary>
    /// 从 DTO 加载并迁移模型配置列表、OCR/翻译专属模型及关联绑定。
    /// </summary>
    private void ApplyAiAndFeatureSettingsFromDto(SettingsData dto, string? originalEngine)
    {
        // 1. 加载多组模型配置或从旧单配置迁移
        AiProfiles.Clear();
        if (dto.AiProfiles != null && dto.AiProfiles.Count > 0)
        {
            foreach (var p in dto.AiProfiles)
            {
                if (string.IsNullOrWhiteSpace(p.ApiUrl)) continue;
                AiProfiles.Add(new AiProfile
                {
                    Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N") : p.Id.Trim(),
                    Name = string.IsNullOrWhiteSpace(p.Name) ? "模型配置" : p.Name.Trim(),
                    ApiUrl = MigrateVisionEndpoint(p.ApiUrl),
                    ApiKey = string.IsNullOrWhiteSpace(p.ApiKey) ? null : p.ApiKey.Trim(),
                    CachedModels = p.CachedModels ?? new List<string>()
                });
            }
        }

        if (AiProfiles.Count == 0)
        {
            string legacyUrl = DefaultAiApiUrl;
            string? legacyKey = null;

            if (!string.IsNullOrWhiteSpace(dto.AiApiUrl))
            {
                legacyUrl = MigrateVisionEndpoint(dto.AiApiUrl);
                legacyKey = string.IsNullOrWhiteSpace(dto.AiApiKey) ? null : dto.AiApiKey.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(dto.VisionApiUrl) ||
                     !string.IsNullOrWhiteSpace(dto.VisionApiModel) ||
                     dto.VisionApiKey != null)
            {
                if (!string.IsNullOrWhiteSpace(dto.VisionApiUrl))
                {
                    legacyUrl = MigrateVisionEndpoint(dto.VisionApiUrl);
                }
                legacyKey = string.IsNullOrWhiteSpace(dto.VisionApiKey) ? null : dto.VisionApiKey.Trim();
            }
            else
            {
                bool legacyOllama = originalEngine != null &&
                                    originalEngine.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
                bool openAiCustom =
                    !string.IsNullOrWhiteSpace(dto.OpenAiApiKey) ||
                    (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl) &&
                     !dto.OpenAiBaseUrl.Trim().TrimEnd('/').Equals(DefaultAiApiUrl, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(dto.OpenAiModel) &&
                     !dto.OpenAiModel.Trim().Equals(DefaultOcrVisionModel, StringComparison.OrdinalIgnoreCase));

                if (legacyOllama && !openAiCustom)
                {
                    legacyUrl = !string.IsNullOrWhiteSpace(dto.OllamaBaseUrl)
                        ? MigrateOllamaEndpoint(dto.OllamaBaseUrl)
                        : DefaultOllamaUrl;
                    legacyKey = null;
                }
                else if (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl) ||
                         !string.IsNullOrWhiteSpace(dto.OpenAiModel) ||
                         !string.IsNullOrWhiteSpace(dto.OpenAiApiKey))
                {
                    if (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl))
                    {
                        legacyUrl = MigrateOpenAiEndpoint(dto.OpenAiBaseUrl);
                    }
                    legacyKey = string.IsNullOrWhiteSpace(dto.OpenAiApiKey) ? null : dto.OpenAiApiKey.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(dto.OllamaBaseUrl))
                {
                    legacyUrl = MigrateOllamaEndpoint(dto.OllamaBaseUrl);
                }
            }

            var defaultProfile = new AiProfile
            {
                Id = "default",
                Name = "默认配置",
                ApiUrl = legacyUrl,
                ApiKey = legacyKey,
                CachedModels = new List<string>()
            };

            if (dto.AiCachedModels != null && dto.AiCachedModels.TryGetValue(NormalizeVisionApiUrlKey(legacyUrl), out var cached))
            {
                defaultProfile.CachedModels = new List<string>(cached);
            }
            else if (dto.VisionApiCachedModels != null && dto.VisionApiCachedModels.TryGetValue(NormalizeVisionApiUrlKey(legacyUrl), out var vCached))
            {
                defaultProfile.CachedModels = new List<string>(vCached);
            }

            AiProfiles.Add(defaultProfile);
        }

        // 2. 缓存字典兼容维护
        if (dto.AiCachedModels != null)
        {
            AiCachedModels = new Dictionary<string, List<string>>(dto.AiCachedModels, StringComparer.OrdinalIgnoreCase);
        }
        else if (dto.VisionApiCachedModels != null)
        {
            AiCachedModels = new Dictionary<string, List<string>>(dto.VisionApiCachedModels, StringComparer.OrdinalIgnoreCase);
        }

        // 绑定 OCR 与 翻译所选配置 ID
        if (!string.IsNullOrWhiteSpace(dto.OcrProfileId) && GetProfile(dto.OcrProfileId) != null)
        {
            OcrProfileId = dto.OcrProfileId.Trim();
        }
        else
        {
            OcrProfileId = AiProfiles[0].Id;
        }

        if (!string.IsNullOrWhiteSpace(dto.TranslationProfileId) && GetProfile(dto.TranslationProfileId) != null)
        {
            TranslationProfileId = dto.TranslationProfileId.Trim();
        }
        else
        {
            TranslationProfileId = AiProfiles[0].Id;
        }

        // 3. OCR 视觉模型与提示词
        if (!string.IsNullOrWhiteSpace(dto.OcrVisionModel))
        {
            OcrVisionModel = dto.OcrVisionModel.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(dto.VisionApiModel))
        {
            OcrVisionModel = dto.VisionApiModel.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(dto.OpenAiModel))
        {
            OcrVisionModel = dto.OpenAiModel.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(dto.OllamaModel))
        {
            OcrVisionModel = dto.OllamaModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.OcrPrompt))
        {
            OcrPrompt = dto.OcrPrompt.Trim();
        }
        else if (!string.IsNullOrWhiteSpace(dto.VisionApiPrompt))
        {
            OcrPrompt = dto.VisionApiPrompt.Trim();
        }

        // 4. 文本翻译配置
        if (dto.TranslationEngine is { } tEngineStr &&
            Enum.TryParse<TranslationEngineType>(tEngineStr, true, out var tEngine))
        {
            TranslationEngine = tEngine;
        }

        if (!string.IsNullOrWhiteSpace(dto.TranslationModel))
        {
            TranslationModel = dto.TranslationModel.Trim();
        }

        if (!string.IsNullOrWhiteSpace(dto.TranslationPrompt))
        {
            TranslationPrompt = dto.TranslationPrompt.Trim();
        }
    }

    /// <summary>
    /// 兼容旧配置：仅填到 host 或 /v1 时，补全为可直接 POST 的完整路径。
    /// 新配置应直接保存完整 URL，程序运行时不再拼接。
    /// </summary>
    internal static string MigrateOpenAiEndpoint(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        // 已是 chat/completions 或其它完整路径则不动
        if (trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 历史默认：https://api.openai.com/v1 或任意以 /v1 结尾的 base
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed + "/chat/completions";
        }

        return trimmed;
    }

    /// <summary>兼容旧配置：仅填 Ollama 根地址时补全 /api/generate。</summary>
    internal static string MigrateOllamaEndpoint(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        if (trimmed.Contains("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 常见旧值：http://localhost:11434
        return trimmed + "/api/generate";
    }

    /// <summary>按地址形态补全路径：Ollama 根地址 → /api/generate，以 /v1 结尾 → /chat/completions。</summary>
    internal static string MigrateVisionEndpoint(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        if (trimmed.Contains("/api/generate", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/api/chat", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.Contains(":11434") ||
            trimmed.Contains("ollama", StringComparison.OrdinalIgnoreCase))
        {
            return MigrateOllamaEndpoint(trimmed);
        }

        return MigrateOpenAiEndpoint(trimmed);
    }

    /// <summary>写入磁盘；raiseChanged 为 false 时只落盘（用于更新检查时间戳）。</summary>
    public void Save(bool raiseChanged = true)
    {
        try
        {
            var dto = new SettingsData
            {
                PlainPaste = HotkeyData.FromBinding(PlainPasteHotkey),
                PlainPasteEnabled = PlainPasteEnabled,
                AutoStart = AutoStart,
                TakeOverSystemClipboard = TakeOverSystemClipboard,
                WindowAlwaysOnTop = WindowAlwaysOnTop,
                ContinuousPasteMode = ContinuousPasteMode,
                RememberWindowPosition = RememberWindowPosition,
                WindowPositionX = WindowPositionX,
                WindowPositionY = WindowPositionY,
                Theme = Theme.ToString(),
                ToastSize = ToastSize.ToString(),
                // 不再写入 DatabasePath：设置页已移除自定义路径；旧文件中的字段读入后也不会再回写
                OcrEngine = OcrEngine.ToString(),
                OcrLocalPack = OcrLocalPack.ToString(),
                OcrCustomDir = string.IsNullOrWhiteSpace(OcrCustomDir) ? null : OcrCustomDir,
                AiProfiles = AiProfiles.Select(p => new AiProfileData
                {
                    Id = p.Id,
                    Name = p.Name,
                    ApiUrl = p.ApiUrl,
                    ApiKey = p.ApiKey,
                    CachedModels = p.CachedModels.Count > 0 ? p.CachedModels : null
                }).ToList(),
                OcrProfileId = OcrProfileId,
                TranslationProfileId = TranslationProfileId,
                AiApiUrl = AiApiUrl,
                AiApiKey = AiApiKey,
                AiCachedModels = AiCachedModels.Count > 0 ? AiCachedModels : null,
                OcrVisionModel = OcrVisionModel,
                OcrPrompt = OcrPrompt,
                TranslationEngine = TranslationEngine.ToString(),
                TranslationModel = TranslationModel,
                TranslationPrompt = TranslationPrompt,
                // 向下兼容旧版属性
                VisionApiUrl = AiApiUrl,
                VisionApiModel = OcrVisionModel,
                VisionApiKey = AiApiKey,
                VisionApiPrompt = OcrPrompt,
                VisionApiCachedModels = AiCachedModels.Count > 0 ? AiCachedModels : null,
                MaxHistoryItems = MaxHistoryItems,
                TextOnlyCapture = TextOnlyCapture,
                CapturePaused = CapturePaused,
                AutoCheckUpdates = AutoCheckUpdates,
                LastUpdateCheckUtc = LastUpdateCheckUtc?.ToUniversalTime().ToString("o"),
                TranslationTargetLanguage = TranslationTargetLanguage,
                PanelHotkeys = new PanelHotkeysData
                {
                    PasteSelected = HotkeyData.FromBinding(PasteSelectedHotkey),
                    PasteSelectedPlain = HotkeyData.FromBinding(PasteSelectedPlainHotkey),
                    CopySelected = HotkeyData.FromBinding(CopySelectedHotkey),
                    TogglePin = HotkeyData.FromBinding(TogglePinHotkey),
                    DeleteSelected = HotkeyData.FromBinding(DeleteSelectedHotkey),
                    HidePanel = HotkeyData.FromBinding(HidePanelHotkey),
                    MoveUp = HotkeyData.FromBinding(MoveUpHotkey),
                    MoveDown = HotkeyData.FromBinding(MoveDownHotkey),
                    StartStack = HotkeyData.FromBinding(StartStackHotkey),
                    StopStack = HotkeyData.FromBinding(StopStackHotkey)
                }
            };
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(dto, JsonOptions));
            DebugLog.Log("设置已保存");
        }
        catch (Exception ex)
        {
            DebugLog.LogException("保存设置失败", ex);
        }

        if (raiseChanged)
        {
            Changed?.Invoke();
        }
    }
}

/// <summary>设置 JSON 的根结构。</summary>
public sealed class SettingsData
{
    public HotkeyData? PlainPaste { get; set; }
    public bool? PlainPasteEnabled { get; set; }
    public bool? AutoStart { get; set; }
    public bool? TakeOverSystemClipboard { get; set; }
    public bool? WindowAlwaysOnTop { get; set; }
    public bool? ContinuousPasteMode { get; set; }
    public bool? RememberWindowPosition { get; set; }
    public double? WindowPositionX { get; set; }
    public double? WindowPositionY { get; set; }
    public string? Theme { get; set; }
    public string? ToastSize { get; set; }
    public string? DatabasePath { get; set; }
    public string? OcrEngine { get; set; }
    public string? OcrLocalPack { get; set; }
    public string? OcrCustomDir { get; set; }
    public List<AiProfileData>? AiProfiles { get; set; }
    public string? OcrProfileId { get; set; }
    public string? TranslationProfileId { get; set; }
    public string? AiApiUrl { get; set; }
    public string? AiApiKey { get; set; }
    public Dictionary<string, List<string>>? AiCachedModels { get; set; }
    public string? OcrVisionModel { get; set; }
    public string? OcrPrompt { get; set; }
    public string? TranslationEngine { get; set; }
    public string? TranslationModel { get; set; }
    public string? TranslationPrompt { get; set; }
    public string? VisionApiUrl { get; set; }
    public string? VisionApiModel { get; set; }
    public string? VisionApiKey { get; set; }
    public string? VisionApiPrompt { get; set; }
    public Dictionary<string, List<string>>? VisionApiCachedModels { get; set; }
    /// <summary>旧字段，仅读取以迁移到 VisionApi*。</summary>
    public string? OllamaBaseUrl { get; set; }
    public string? OllamaModel { get; set; }
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiModel { get; set; }
    public string? OpenAiApiKey { get; set; }
    public int? MaxHistoryItems { get; set; }
    public bool? TextOnlyCapture { get; set; }
    public bool? CapturePaused { get; set; }
    public bool? AutoCheckUpdates { get; set; }
    public string? LastUpdateCheckUtc { get; set; }
    public string? TranslationTargetLanguage { get; set; }
    public PanelHotkeysData? PanelHotkeys { get; set; }
}

/// <summary>模型配置项的 JSON 结构。</summary>
public sealed class AiProfileData
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? ApiUrl { get; set; }
    public string? ApiKey { get; set; }
    public List<string>? CachedModels { get; set; }
}

/// <summary>面板内快捷键的 JSON 结构。</summary>
public sealed class PanelHotkeysData
{
    public HotkeyData? PasteSelected { get; set; }
    public HotkeyData? PasteSelectedPlain { get; set; }
    public HotkeyData? CopySelected { get; set; }
    public HotkeyData? TogglePin { get; set; }
    public HotkeyData? DeleteSelected { get; set; }
    public HotkeyData? HidePanel { get; set; }
    public HotkeyData? MoveUp { get; set; }
    public HotkeyData? MoveDown { get; set; }
    public HotkeyData? StartStack { get; set; }
    public HotkeyData? StopStack { get; set; }
}

/// <summary>热键的 JSON 表示（人类可读的字符串形式）。</summary>
public sealed class HotkeyData
{
    public List<string>? Modifiers { get; set; }
    public string? Key { get; set; }

    public HotkeyBinding ToBinding()
    {
        var modifiers = ModifierKeys.None;
        foreach (string? name in Modifiers ?? new List<string>())
        {
            modifiers |= name?.Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" or "windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        }

        System.Windows.Input.Key key = System.Windows.Input.Key.None;
        if (!string.IsNullOrEmpty(Key))
        {
            // Enter / Return 同义
            if (Key.Equals("Enter", StringComparison.OrdinalIgnoreCase) ||
                Key.Equals("Return", StringComparison.OrdinalIgnoreCase))
            {
                key = System.Windows.Input.Key.Enter;
            }
            else if (Key.Equals("Esc", StringComparison.OrdinalIgnoreCase))
            {
                key = System.Windows.Input.Key.Escape;
            }
            else
            {
                Enum.TryParse(Key, ignoreCase: true, out key);
            }
        }

        return new HotkeyBinding(modifiers, HotkeyBinding.NormalizeKey(key));
    }

    public static HotkeyData FromBinding(HotkeyBinding binding)
    {
        var names = new List<string>();
        if ((binding.Modifiers & ModifierKeys.Control) != 0) names.Add("Ctrl");
        if ((binding.Modifiers & ModifierKeys.Alt) != 0) names.Add("Alt");
        if ((binding.Modifiers & ModifierKeys.Shift) != 0) names.Add("Shift");
        if ((binding.Modifiers & ModifierKeys.Windows) != 0) names.Add("Win");

        System.Windows.Input.Key key = HotkeyBinding.NormalizeKey(binding.Key);
        string? keyName = key == System.Windows.Input.Key.None
            ? null
            : key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Return
                ? "Enter"
                : key.ToString();

        return new HotkeyData
        {
            Modifiers = names,
            Key = keyName
        };
    }
}
