namespace QuickClip.Models;

/// <summary>
/// Toast 提示卡片尺寸预设：
/// 保持固定统一宽度与字体比例，避免动态自适应带来的跳动感。
/// </summary>
public enum ToastSize
{
    /// <summary>紧凑（小）：宽度 380px，标题字号 12.5，适合小屏幕场景。</summary>
    Small = 0,

    /// <summary>标准（中）：宽度 460px，与主列表完全同宽，标题字号 13.5，默认标准体验。</summary>
    Medium = 1,

    /// <summary>宽屏（大）：宽度 520px，标题字号 14.5，适合高分辨率大屏。</summary>
    Large = 2
}
