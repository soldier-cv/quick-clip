namespace QuickClip.Models;

/// <summary>面板唤起位置。</summary>
public enum WindowOpenPosition
{
    /// <summary>记住上次位置，下次在同处弹出（默认）。</summary>
    RememberLast = 0,

    /// <summary>在鼠标指针处弹出。</summary>
    AtCursor = 1,

    /// <summary>停靠当前显示器右侧垂直居中（仿系统 Win+V）。</summary>
    RightSide = 2
}
