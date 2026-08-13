using System.Drawing;
using System.Windows.Forms;
using QuickClip.Native;

namespace QuickClip.Services;

/// <summary>系统托盘图标服务：打开、设置、数据目录、清除今日、自启动、更新、退出。</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _installUpdateItem;
    private readonly ToolStripMenuItem _exitItem;

    public event Action? ToggleRequested;
    public event Action? ExitRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? AutoStartToggleRequested;
    public event Action? CheckUpdateRequested;
    public event Action? InstallUpdateRequested;
    public event Action? OpenDataFolderRequested;
    public event Action? ClearTodayHistoryRequested;

    public TrayIconService()
    {
        _notifyIcon = new NotifyIcon
        {
            Text = "QuickClip",
            Visible = true
        };

        _notifyIcon.Icon = LoadAppIcon();

        var menu = new ContextMenuStrip();
        _toggleItem = new ToolStripMenuItem("打开 QuickClip");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();

        var dataFolderItem = new ToolStripMenuItem("打开数据目录");
        dataFolderItem.Click += (_, _) => OpenDataFolderRequested?.Invoke();

        var clearTodayItem = new ToolStripMenuItem("清除今日历史");
        clearTodayItem.Click += (_, _) => ClearTodayHistoryRequested?.Invoke();

        _autoStartItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true };
        _autoStartItem.Click += (_, _) => AutoStartToggleRequested?.Invoke(_autoStartItem.Checked);

        var updateItem = new ToolStripMenuItem("检查更新…");
        updateItem.Click += (_, _) => CheckUpdateRequested?.Invoke();

        _installUpdateItem = new ToolStripMenuItem(UpdateService.ApplyActionLabel + "…") { Visible = false };
        _installUpdateItem.Click += (_, _) => InstallUpdateRequested?.Invoke();

        _exitItem = new ToolStripMenuItem("退出");
        _exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(_toggleItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(dataFolderItem);
        menu.Items.Add(clearTodayItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(updateItem);
        menu.Items.Add(_installUpdateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_exitItem);
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ToggleRequested?.Invoke();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_installUpdateItem.Visible)
            {
                InstallUpdateRequested?.Invoke();
            }
        };
    }

    public void SetAutoStartChecked(bool enabled) => _autoStartItem.Checked = enabled;

    /// <summary>有已下载更新时显示托盘「安装更新」项。</summary>
    public void SetInstallUpdateVisible(bool visible, string? tagName)
    {
        if (_notifyIcon.ContextMenuStrip == null)
        {
            return;
        }

        void Apply()
        {
            _installUpdateItem.Visible = visible;
            string action = UpdateService.ApplyActionLabel;
            if (!visible || string.IsNullOrEmpty(tagName))
            {
                _installUpdateItem.Text = action + "…";
                return;
            }

            _installUpdateItem.Text = $"{action} {tagName}";
        }

        if (_notifyIcon.ContextMenuStrip.InvokeRequired)
        {
            _notifyIcon.ContextMenuStrip.BeginInvoke(Apply);
            return;
        }

        Apply();
    }

    public void ShowBalloonTip(string title, string message)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch
        {
            // 提示失败不影响主流程
        }
    }

    /// <summary>
    /// 加载托盘图标。系统托盘槽位尺寸固定，但默认常取 16px 且留白偏多会显得「小」：
    /// 取更大图源并在槽位内略放大绘制（约 1.22x），观感更大一圈且更清晰。
    /// </summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/QuickClip;component/Assets/quickclip.ico");
            using var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                // 先读多尺寸 ICO，再抽出接近 32/64 的清晰帧作源
                using var multi = new Icon(stream);
                int trayPx = GetTrayIconPixelSize();
                int sourcePx = trayPx <= 16 ? 32 : Math.Min(64, trayPx * 2);
                using var source = new Icon(multi, sourcePx, sourcePx);
                return RenderTrayIcon(source, trayPx, scale: 1.22f);
            }
        }
        catch (Exception ex)
        {
            DebugLog.LogException("加载托盘图标失败，使用回退图标", ex);
        }

        return GenerateFallbackIcon();
    }

    /// <summary>系统小图标边长（含 DPI），至少 16。</summary>
    private static int GetTrayIconPixelSize()
    {
        try
        {
            // SM_CXSMICON：托盘/小图标逻辑像素，高 DPI 下会变大
            int px = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
            if (px >= 16)
            {
                return px;
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            int px = SystemInformation.SmallIconSize.Width;
            if (px >= 16)
            {
                return px;
            }
        }
        catch
        {
            // ignore
        }

        return 16;
    }

    /// <summary>
    /// 将源图标画入 trayPx×trayPx，scale&gt;1 时略放大并裁切边缘留白，使托盘里主体更大。
    /// </summary>
    private static Icon RenderTrayIcon(Icon source, int trayPx, float scale)
    {
        trayPx = Math.Clamp(trayPx, 16, 64);
        scale = Math.Clamp(scale, 1f, 1.35f);

        using var bitmap = new Bitmap(trayPx, trayPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;

            float draw = trayPx * scale;
            float origin = (trayPx - draw) / 2f; // scale&gt;1 时为负，略裁边缘留白
            var dest = new RectangleF(origin, origin, draw, draw);
            g.DrawIcon(source, Rectangle.Round(dest));
        }

        return IconFromBitmap(bitmap);
    }

    private static Icon GenerateFallbackIcon()
    {
        int trayPx = GetTrayIconPixelSize();
        using var bitmap = new Bitmap(trayPx, trayPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // 几乎铺满槽位，少留白
            int pad = Math.Max(1, trayPx / 16);
            int size = trayPx - pad * 2;
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Rectangle(pad, pad, size, size),
                Color.FromArgb(99, 102, 241),
                Color.FromArgb(56, 189, 248),
                45f);
            g.FillRoundedRectangle(brush, pad, pad, size, size, Math.Max(3, trayPx / 5));
            using var pen = new Pen(Color.White, Math.Max(1.5f, trayPx / 12f));
            int inner = pad + trayPx / 5;
            int innerSize = trayPx - inner * 2;
            g.DrawRectangle(pen, inner, inner, innerSize, innerSize);
        }

        return IconFromBitmap(bitmap);
    }

    private static Icon IconFromBitmap(Bitmap bitmap)
    {
        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(iconHandle);
            return (Icon)icon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(iconHandle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _toggleItem.Dispose();
        _autoStartItem.Dispose();
        _exitItem.Dispose();
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, int x, int y, int width, int height, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
