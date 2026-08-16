using System.IO;
using Watashi.Shared.DTOs.Files;

namespace Watashi.Client.Services;

/// <summary>
/// 再開可能転送の通信境界。キューをHTTP実装から分離し、切断・競合・再送を決定的にテストできるようにする。
/// </summary>
public interface ITransferProtocol
{
    Task<UploadSessionDto> CreateUploadSessionAsync(CreateUploadSessionRequest request, CancellationToken ct = default);
    Task<UploadSessionDto> GetUploadSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<UploadSessionDto> UploadChunkAsync(
        Guid sessionId, long offset, byte[] buffer, int count, string chunkSha256, CancellationToken ct = default);
    Task<UploadSessionDto> CompleteUploadSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<UploadSessionDto> CancelUploadSessionAsync(Guid sessionId, CancellationToken ct = default);
    Task<TransferDownloadMetadataDto> GetDownloadMetadataV2Async(
        int hostId, int shareId, string path, CancellationToken ct = default);
    Task DownloadRangeV2Async(
        int hostId, int shareId, string path, long offset, int length, string? etag,
        Stream output, CancellationToken ct = default);
}
