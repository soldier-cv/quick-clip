using System.Windows.Media.Imaging;

namespace QuickClip.Services;

/// <summary>
/// 缩略图 LRU 缓存：限制内存中 Bitmap 数量，虚拟化滚出后可淘汰。
/// 键 = 路径 + 解码宽度。
/// </summary>
public static class ThumbnailCache
{
    /// <summary>同时保留的解码图上限（列表小图 + 偶发大图预览）。</summary>
    public const int MaxEntries = 64;

    private static readonly object Gate = new();
    private static readonly LinkedList<string> Order = new();
    private static readonly Dictionary<string, (LinkedListNode<string> Node, BitmapImage Image)> Map = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapImage GetOrCreate(string path, int decodePixelWidth)
    {
        string key = $"{path}|{decodePixelWidth}";
        lock (Gate)
        {
            if (Map.TryGetValue(key, out var hit))
            {
                Order.Remove(hit.Node);
                Order.AddFirst(hit.Node);
                return hit.Image;
            }
        }

        var bitmap = Decode(path, decodePixelWidth);

        lock (Gate)
        {
            if (Map.TryGetValue(key, out var race))
            {
                return race.Image;
            }

            var node = new LinkedListNode<string>(key);
            Order.AddFirst(node);
            Map[key] = (node, bitmap);

            while (Map.Count > MaxEntries && Order.Last != null)
            {
                string oldKey = Order.Last.Value;
                Order.RemoveLast();
                Map.Remove(oldKey);
            }
        }

        return bitmap;
    }

    /// <summary>路径失效时剔除缓存（删除条目后调用）。</summary>
    public static void RemoveByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        lock (Gate)
        {
            var keys = Map.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string key in keys)
            {
                if (Map.Remove(key, out var entry))
                {
                    Order.Remove(entry.Node);
                }
            }
        }
    }

    private static BitmapImage Decode(string path, int decodePixelWidth)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
