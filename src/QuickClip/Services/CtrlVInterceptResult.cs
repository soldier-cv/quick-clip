namespace QuickClip.Services;

/// <summary>
/// 低级键盘钩子拦截物理 Ctrl+V 后的处理结论。
/// 收集栈需要区分「吞掉按键并自行注入」和「回填剪贴板后放行真实按键」两种情况：
/// 后者让目标窗口收到的是用户自己按下的完整 Ctrl+V，行为与普通粘贴完全一致。
/// </summary>
public enum CtrlVInterceptResult
{
    /// <summary>不接管：按键照常下发给目标窗口。</summary>
    NotHandled,

    /// <summary>已接管并自行模拟粘贴：吞掉这次物理按键。</summary>
    Swallow,

    /// <summary>内容已回填剪贴板：放行这次物理按键，由用户自己的 Ctrl+V 完成粘贴。</summary>
    PassThrough
}
