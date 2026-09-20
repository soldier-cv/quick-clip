using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace QuickClip.Models;

/// <summary>
/// AI 模型配置项：封装接口地址、访问密钥与已缓存模型列表。
/// 供 OCR 与 翻译等不同功能独立关联使用。
/// 
/// @author xudong.hua,gemini
/// @since 2026-09-20 15:30 星期日
/// </summary>
public sealed class AiProfile : INotifyPropertyChanged
{
    private string _name = "默认配置";
    private string _apiUrl = "https://api.openai.com/v1/chat/completions";
    private string? _apiKey;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>唯一标识符（GUID 字符串）。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>配置名词 / 别名（如 "OpenAI", "DeepSeek", "Ollama", "硅基流动"）。</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>完整接口地址（OpenAI 兼容 /chat/completions 或 Ollama /api/generate）。</summary>
    public string ApiUrl
    {
        get => _apiUrl;
        set
        {
            if (_apiUrl != value)
            {
                _apiUrl = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>访问密钥 API Key（本地持久化，禁止打入日志）。</summary>
    public string? ApiKey
    {
        get => _apiKey;
        set
        {
            if (_apiKey != value)
            {
                _apiKey = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>已成功拉取并缓存的模型列表。</summary>
    public List<string> CachedModels { get; set; } = new();

    public AiProfile Clone()
    {
        return new AiProfile
        {
            Id = Id,
            Name = Name,
            ApiUrl = ApiUrl,
            ApiKey = ApiKey,
            CachedModels = new List<string>(CachedModels)
        };
    }

    public override string ToString() => Name;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

