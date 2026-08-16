namespace Watashi.Shared.DTOs.Files;

/// <summary>リモート上のファイル／フォルダーをコピーする。移動は意図的に提供しない。</summary>
public sealed record RemoteCopyRequest
{
    public int SourceHostId { get; init; }
    public int SourceShareId { get; init; }
    public string SourcePath { get; init; } = "/";
    public int TargetHostId { get; init; }
    public int TargetShareId { get; init; }
    public string TargetPath { get; init; } = "/";
    /// <summary>fail（既定）/ rename / overwrite。フォルダーのoverwriteは拒否する。</summary>
    public string CollisionPolicy { get; init; } = "fail";
}

public sealed record RemoteCopyResponse
{
    public string SourcePath { get; init; } = "/";
    public string TargetPath { get; init; } = "/";
    public string ItemType { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public long BytesCopied { get; init; }
    public bool RenamedForCollision { get; init; }
}
