using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Client.Services;

namespace Watashi.Client.ViewModels;

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
}
