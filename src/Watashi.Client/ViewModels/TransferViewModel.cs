using CommunityToolkit.Mvvm.ComponentModel;

namespace Watashi.Client.ViewModels;

public partial class TransferViewModel : ObservableObject
{
    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private long bytesTransferred;
    [ObservableProperty] private long totalBytes;
    [ObservableProperty] private bool isActive;

    public int Percent => TotalBytes > 0 ? (int)(BytesTransferred * 100 / TotalBytes) : 0;

    partial void OnBytesTransferredChanged(long value) => OnPropertyChanged(nameof(Percent));
    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(Percent));
}
