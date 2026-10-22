using System.Collections.Specialized;
using System.IO;
using QuickClip.Models;

namespace QuickClip.Services;

/// <summary>
/// 把一条历史做成拖出数据。只带原文，不带备注。
/// 对方窗口不接受对应格式时，这次拖放不会写入任何东西。
/// </summary>
public static class DragPayload
{
    public static DataObject? Create(ClipboardItem item)
    {
        var data = new DataObject();
        bool hasPayload = false;

        if (item.ContentType == ClipboardContentType.File)
        {
            var existing = new StringCollection();
            foreach (string path in ClipboardItem.SplitFilePaths(item.TextContent))
            {
                if (File.Exists(path) || Directory.Exists(path))
                {
                    existing.Add(path);
                }
            }

            if (existing.Count > 0)
            {
                data.SetFileDropList(existing);
                hasPayload = true;
            }
        }
        else if (item.ContentType == ClipboardContentType.Image)
        {
            if (!string.IsNullOrEmpty(item.PreviewPath) && File.Exists(item.PreviewPath))
            {
                var files = new StringCollection { item.PreviewPath };
                data.SetFileDropList(files);
                hasPayload = true;
            }
        }

        if (!string.IsNullOrEmpty(item.TextContent) &&
            item.ContentType != ClipboardContentType.Image)
        {
            data.SetText(item.TextContent);
            hasPayload = true;
        }

        return hasPayload ? data : null;
    }
}
