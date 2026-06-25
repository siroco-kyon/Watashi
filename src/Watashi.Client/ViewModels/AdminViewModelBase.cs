using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

public sealed record AdminSortOption(string Label, string PropertyName, ListSortDirection Direction = ListSortDirection.Ascending);

/// <summary>
/// Admin VM 共通の StatusMessage + try/catch ラッパー + ObservableCollection 一括差し替え。
/// </summary>
public abstract partial class AdminViewModelBase : ObservableObject
{
    [ObservableProperty] private string statusMessage = string.Empty;

    /// <summary>
    /// try/catch ラッパー。成功時にメッセージを指定可能。
    /// action 内で StatusMessage を書き換えた場合 (バリデーション失敗の早期 return など) は
    /// successMessage で上書きしない。
    /// </summary>
    protected async Task SafeAsync(Func<Task> action, string? successMessage = null)
    {
        var before = StatusMessage;
        try
        {
            await action();
            if (successMessage is not null && string.Equals(StatusMessage, before, StringComparison.Ordinal))
                StatusMessage = successMessage;
        }
        catch (ApiException ex)
        {
            StatusMessage = ex.Message;
        }
        catch (Exception ex)
        {
            StatusMessage = "通信エラー: " + ex.Message;
        }
    }

    /// <summary>ObservableCollection を一括差し替えして CollectionChanged の連発を避ける。</summary>
    public static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    protected static bool MatchesSearch(string searchText, params object?[] values)
    {
        var search = searchText.Trim();
        if (string.IsNullOrEmpty(search)) return true;
        foreach (var value in values)
        {
            if (value is null) continue;
            if (value.ToString()?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        return false;
    }

    protected static void ApplySort(ICollectionView view, AdminSortOption? option)
    {
        if (option is null) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(option.PropertyName, option.Direction));
        var first = view.SourceCollection.Cast<object>().FirstOrDefault();
        if (first?.GetType().GetProperty("Id") is not null &&
            !string.Equals(option.PropertyName, "Id", StringComparison.Ordinal))
            view.SortDescriptions.Add(new SortDescription("Id", ListSortDirection.Ascending));
        view.Refresh();
    }
}
