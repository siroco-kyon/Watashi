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

    /// <summary>try/catch とローディング状態をまとめる。成功時にメッセージを指定可能。</summary>
    protected async Task SafeAsync(Func<Task> action, string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage is not null) StatusMessage = successMessage;
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
