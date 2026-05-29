using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Watashi.Shared.Helpers;

namespace Watashi.Client.ViewModels;

public partial class TransferViewModel : ObservableObject
{
    // IsActive が true になった時点から計測し、速度と残り時間を算出する。
    private readonly Stopwatch _sw = new();

    [ObservableProperty] private string fileName = string.Empty;
    [ObservableProperty] private long bytesTransferred;
    [ObservableProperty] private long totalBytes;
    [ObservableProperty] private bool isActive;

    public int Percent => TotalBytes > 0 ? (int)(BytesTransferred * 100 / TotalBytes) : 0;

    public double BytesPerSecond => TransferStats.BytesPerSecond(BytesTransferred, _sw.Elapsed);
    public string SpeedText => TransferStats.FormatSpeed(BytesPerSecond);
    public string EtaText => TransferStats.FormatEta(TransferStats.Eta(BytesTransferred, TotalBytes, BytesPerSecond));

    partial void OnBytesTransferredChanged(long value)
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }

    partial void OnTotalBytesChanged(long value) => OnPropertyChanged(nameof(Percent));

    partial void OnIsActiveChanged(bool value)
    {
        if (value) _sw.Restart();
        else _sw.Stop();
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }
}
