using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace QuickClip.ViewModels;

/// <summary>
/// 支持批量更新的 ObservableCollection，避免大量插入/清空时反复触发 CollectionChanged 导致 UI 布局抖动与闪烁。
/// </summary>
public class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public ObservableRangeCollection() : base() { }

    public ObservableRangeCollection(IEnumerable<T> collection) : base(collection) { }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotification)
        {
            base.OnPropertyChanged(e);
        }
    }

    /// <summary>
    /// 一次性将集合内容替换为目标项，仅触发一次 Reset 通知与 Count/Item[] 变更。
    /// </summary>
    public void ReplaceAll(IEnumerable<T> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        _suppressNotification = true;
        try
        {
            Items.Clear();
            foreach (var item in newItems)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotification = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
