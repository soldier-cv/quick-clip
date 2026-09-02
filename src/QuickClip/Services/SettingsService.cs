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

    /// <summary>外观主题（默认 Terminal）。</summary>
    public AppTheme Theme { get; private set; } = AppTheme.Terminal;

    /// <summary>主窗口是否前端置顶（固定在最前，失焦不自动隐藏）。仅由 Ctrl+P / 图钉切换，设置页不再暴露。</summary>
    public bool WindowAlwaysOnTop { get; private set; }

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

    /// <summary>视觉接口完整请求 URL（Ollama 原生或 OpenAI 兼容，须含路径）。</summary>
    public string VisionApiUrl { get; private set; } = DefaultVisionApiUrl;

    /// <summary>视觉模型名（需支持看图）。</summary>
    public string VisionApiModel { get; private set; } = DefaultVisionApiModel;

    /// <summary>视觉接口 API Key（可选；仅保存在本地 settings.json，禁止写日志）。</summary>
    public string? VisionApiKey { get; private set; }

    private const string DefaultVisionApiUrl = "https://api.openai.com/v1/chat/completions";
    private const string DefaultVisionApiModel = "gpt-4o-mini";
    private const string DefaultOllamaUrl = "http://localhost:11434/api/generate";
    private const string DefaultOllamaModel = "llava";

    /// <summary>历史最大条数（含置顶；超出淘汰最旧非置顶）。默认 233。</summary>
    public int MaxHistoryItems { get; private set; } = 233;

    /// <summary>仅记录文本/链接，忽略图片与文件。</summary>
    public bool TextOnlyCapture { get; private set; }

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
            WindowAlwaysOnTop = dto.WindowAlwaysOnTop ?? false;
            Theme = ParseTheme(dto.Theme);
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

            ApplyVisionApiFromDto(dto, dto.OcrEngine);

            TextOnlyCapture = dto.TextOnlyCapture ?? false;
            AutoCheckUpdates = dto.AutoCheckUpdates ?? true;
            LastUpdateCheckUtc = ParseUtc(dto.LastUpdateCheckUtc);
            MaxHistoryItems = ClampMaxHistory(dto.MaxHistoryItems ?? DefaultMaxHistoryItems);

            ApplyPanelHotkeys(dto.PanelHotkeys);

            DebugLog.Log(
                $"已加载设置: 纯文本粘贴={PlainPasteHotkey}({(PlainPasteEnabled ? "启用" : "禁用")}), " +
                $"自启动={AutoStart}, 主题={Theme}, 窗口置顶={WindowAlwaysOnTop}, OCR={OcrEngine}/{OcrLocalPack} " +
                $"vision={DebugLog.DescribeUrl(VisionApiUrl)}");
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

    /// <summary>
    /// 更新视觉接口配置并持久化（apiKey 为空表示清空）。
    /// 地址为完整 endpoint；API Key 仅存本地 settings.json，禁止写入日志。
    /// </summary>
    public void SetVisionApiConfig(string baseUrl, string model, string? apiKey)
    {
        string nextUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? VisionApiUrl
            : MigrateVisionEndpoint(baseUrl);
        string nextModel = string.IsNullOrWhiteSpace(model) ? VisionApiModel : model.Trim();
        string? nextKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (VisionApiUrl == nextUrl && VisionApiModel == nextModel && VisionApiKey == nextKey)
        {
            return;
        }

        VisionApiUrl = nextUrl;
        VisionApiModel = nextModel;
        VisionApiKey = nextKey;
        Save();
    }

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
    /// 新字段优先；否则从旧 Ollama/OpenAI 配置迁移。
    /// 用户同时填过两者时，优先已改过的 OpenAI（含 Key），再回退 Ollama。
    /// </summary>
    private void ApplyVisionApiFromDto(SettingsData dto, string? originalEngine)
    {
        if (!string.IsNullOrWhiteSpace(dto.VisionApiUrl) ||
            !string.IsNullOrWhiteSpace(dto.VisionApiModel) ||
            dto.VisionApiKey != null)
        {
            if (!string.IsNullOrWhiteSpace(dto.VisionApiUrl))
            {
                VisionApiUrl = MigrateVisionEndpoint(dto.VisionApiUrl);
            }

            if (!string.IsNullOrWhiteSpace(dto.VisionApiModel))
            {
                VisionApiModel = dto.VisionApiModel.Trim();
            }

            VisionApiKey = string.IsNullOrWhiteSpace(dto.VisionApiKey) ? null : dto.VisionApiKey.Trim();
            return;
        }

        bool legacyOllama = originalEngine != null &&
                            originalEngine.Equals("Ollama", StringComparison.OrdinalIgnoreCase);
        bool openAiCustom =
            !string.IsNullOrWhiteSpace(dto.OpenAiApiKey) ||
            (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl) &&
             !dto.OpenAiBaseUrl.Trim().TrimEnd('/').Equals(DefaultVisionApiUrl, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(dto.OpenAiModel) &&
             !dto.OpenAiModel.Trim().Equals(DefaultVisionApiModel, StringComparison.OrdinalIgnoreCase));

        if (legacyOllama && !openAiCustom)
        {
            if (!string.IsNullOrWhiteSpace(dto.OllamaBaseUrl))
            {
                VisionApiUrl = MigrateOllamaEndpoint(dto.OllamaBaseUrl);
            }
            else
            {
                VisionApiUrl = DefaultOllamaUrl;
            }

            VisionApiModel = string.IsNullOrWhiteSpace(dto.OllamaModel)
                ? DefaultOllamaModel
                : dto.OllamaModel.Trim();
            VisionApiKey = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl) ||
            !string.IsNullOrWhiteSpace(dto.OpenAiModel) ||
            !string.IsNullOrWhiteSpace(dto.OpenAiApiKey))
        {
            if (!string.IsNullOrWhiteSpace(dto.OpenAiBaseUrl))
            {
                VisionApiUrl = MigrateOpenAiEndpoint(dto.OpenAiBaseUrl);
            }

            if (!string.IsNullOrWhiteSpace(dto.OpenAiModel))
            {
                VisionApiModel = dto.OpenAiModel.Trim();
            }

            VisionApiKey = string.IsNullOrWhiteSpace(dto.OpenAiApiKey) ? null : dto.OpenAiApiKey.Trim();
            return;
        }

        if (!string.IsNullOrWhiteSpace(dto.OllamaBaseUrl) || !string.IsNullOrWhiteSpace(dto.OllamaModel))
        {
            if (!string.IsNullOrWhiteSpace(dto.OllamaBaseUrl))
            {
                VisionApiUrl = MigrateOllamaEndpoint(dto.OllamaBaseUrl);
            }
            else
            {
                VisionApiUrl = DefaultOllamaUrl;
            }

            VisionApiModel = string.IsNullOrWhiteSpace(dto.OllamaModel)
                ? DefaultOllamaModel
                : dto.OllamaModel.Trim();
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
                WindowAlwaysOnTop = WindowAlwaysOnTop,
                Theme = Theme.ToString(),
                // 不再写入 DatabasePath：设置页已移除自定义路径；旧文件中的字段读入后也不会再回写
                OcrEngine = OcrEngine.ToString(),
                OcrLocalPack = OcrLocalPack.ToString(),
                OcrCustomDir = string.IsNullOrWhiteSpace(OcrCustomDir) ? null : OcrCustomDir,
                VisionApiUrl = VisionApiUrl,
                VisionApiModel = VisionApiModel,
                VisionApiKey = VisionApiKey,
                MaxHistoryItems = MaxHistoryItems,
                TextOnlyCapture = TextOnlyCapture,
                AutoCheckUpdates = AutoCheckUpdates,
                LastUpdateCheckUtc = LastUpdateCheckUtc?.ToUniversalTime().ToString("o"),
                PanelHotkeys = new PanelHotkeysData
                {
                    PasteSelected = HotkeyData.FromBinding(PasteSelectedHotkey),
                    PasteSelectedPlain = HotkeyData.FromBinding(PasteSelectedPlainHotkey),
                    CopySelected = HotkeyData.FromBinding(CopySelectedHotkey),
                    TogglePin = HotkeyData.FromBinding(TogglePinHotkey),
                    DeleteSelected = HotkeyData.FromBinding(DeleteSelectedHotkey),
                    HidePanel = HotkeyData.FromBinding(HidePanelHotkey),
                    MoveUp = HotkeyData.FromBinding(MoveUpHotkey),
                    MoveDown = HotkeyData.FromBinding(MoveDownHotkey)
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
    public bool? WindowAlwaysOnTop { get; set; }
    public string? Theme { get; set; }
    public string? DatabasePath { get; set; }
    public string? OcrEngine { get; set; }
    public string? OcrLocalPack { get; set; }
    public string? OcrCustomDir { get; set; }
    public string? VisionApiUrl { get; set; }
    public string? VisionApiModel { get; set; }
    public string? VisionApiKey { get; set; }
    /// <summary>旧字段，仅读取以迁移到 VisionApi*。</summary>
    public string? OllamaBaseUrl { get; set; }
    public string? OllamaModel { get; set; }
    public string? OpenAiBaseUrl { get; set; }
    public string? OpenAiModel { get; set; }
    public string? OpenAiApiKey { get; set; }
    public int? MaxHistoryItems { get; set; }
    public bool? TextOnlyCapture { get; set; }
    public bool? AutoCheckUpdates { get; set; }
    public string? LastUpdateCheckUtc { get; set; }
    public PanelHotkeysData? PanelHotkeys { get; set; }
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
