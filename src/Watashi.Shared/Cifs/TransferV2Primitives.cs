using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Watashi.Shared.Helpers;

namespace Watashi.Shared.Cifs;

public static class TransferV2Limits
{
    public const int MaxChunkBytes = 8 * 1024 * 1024;
    public const int MaxIdempotencyKeyBytes = 256;
    public const int MaxPathChars = 4096;
    public const string TempFilePrefix = ".watashi-upload-";
    public const string TempFileSuffix = ".tmp";
}

public static class TransferFileTypes
{
    public const string Missing = "missing";
    public const string File = "file";
    public const string Directory = "directory";
}

public sealed record TransferFileMetadata(
    bool Exists,
    string Type,
    long? Size,
    DateTime? ModifiedAtUtc,
    bool IsReparsePoint)
{
    public static TransferFileMetadata Missing { get; } =
        new(false, TransferFileTypes.Missing, null, null, false);
}

public sealed record TransferChunkWriteResult(
    long RequestedOffset,
    int ChunkLength,
    int BytesWritten,
    long NextOffset,
    bool AlreadyApplied);

/// <summary>offset range の実データと、SMBに最も近い実行nodeで計算したchecksum。</summary>
public sealed record TransferReadChunk(byte[] Data, string Sha256);

public sealed record TransferSha256Result(string Algorithm, string Hash, long Size);

public sealed class TransferOffsetMismatchException : IOException
{
    public long ExpectedOffset { get; }
    public long ActualOffset { get; }

    public TransferOffsetMismatchException(long expectedOffset, long actualOffset, string? reason = null)
        : base(reason is null
            ? $"一時ファイルの offset が一致しません (expected={expectedOffset}, actual={actualOffset})。"
            : $"一時ファイルの offset が一致しません (expected={expectedOffset}, actual={actualOffset}): {reason}")
    {
        ExpectedOffset = expectedOffset;
        ActualOffset = actualOffset;
    }
}

public sealed class TransferRangeNotSatisfiableException : IOException
{
    public long Offset { get; }
    public long Size { get; }

    public TransferRangeNotSatisfiableException(long offset, long size)
        : base($"読み取り offset {offset} はファイルサイズ {size} を超えています。")
    {
        Offset = offset;
        Size = size;
    }
}

public sealed class TransferReparsePointException : IOException
{
    public TransferReparsePointException(string path)
        : base($"リパースポイントは Transfer v2 の対象にできません: {path}") { }
}

public static class TransferV2Validation
{
    public static string NormalizeSha256(string value, string parameterName = "sha256")
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
            throw new ArgumentException("SHA-256 は64桁の16進文字列で指定してください。", parameterName);

        Span<char> normalized = stackalloc char[64];
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (!char.IsAsciiHexDigit(ch))
                throw new ArgumentException("SHA-256 は64桁の16進文字列で指定してください。", parameterName);
            normalized[i] = char.ToLowerInvariant(ch);
        }
        return new string(normalized);
    }

    public static string HashIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("idempotencyKey は必須です。", nameof(value));
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > TransferV2Limits.MaxIdempotencyKeyBytes)
            throw new ArgumentException(
                $"idempotencyKey は UTF-8 で {TransferV2Limits.MaxIdempotencyKeyBytes} バイト以内にしてください。",
                nameof(value));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string NormalizeUploadTargetPath(string path)
    {
        var normalized = PathHelper.NormalizePath(path);
        if (normalized == "/")
            throw new ArgumentException("アップロード先にはファイルパスを指定してください。", nameof(path));
        if (normalized.Length > TransferV2Limits.MaxPathChars)
            throw new ArgumentException(
                $"アップロード先パスは {TransferV2Limits.MaxPathChars} 文字以内にしてください。",
                nameof(path));
        if (RemoteTrashPathPolicy.IsReservedPath(normalized))
            throw new ArgumentException("Watashi の管理用ごみ箱領域はアップロード先に指定できません。", nameof(path));
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (name.StartsWith(TransferV2Limits.TempFilePrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Watashi の予約済み一時ファイル名は指定できません。", nameof(path));
        return normalized;
    }

    public static string BuildTempPath(string targetPath, Guid sessionId)
    {
        var target = NormalizeUploadTargetPath(targetPath);
        var parent = PathHelper.GetParent(target);
        return PathHelper.NormalizePath(
            $"{parent}/{TransferV2Limits.TempFilePrefix}{sessionId:N}{TransferV2Limits.TempFileSuffix}");
    }

    public static (string TempPath, string TargetPath) ValidateCommitPaths(string tempPath, string targetPath)
    {
        var temp = NormalizeAndValidateTempPath(tempPath);
        var target = NormalizeUploadTargetPath(targetPath);
        if (!string.Equals(PathHelper.GetParent(temp), PathHelper.GetParent(target),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("一時ファイルとcommit先は同じ親ディレクトリである必要があります。");
        return (temp, target);
    }

    /// <summary>
    /// range間の通常の変更を安価に検出するmetadata世代ETag。contentの完全性は
    /// metadataで別途返す全体SHA-256をClientが完了時に照合して保証する。
    /// </summary>
    public static string BuildMetadataETag(TransferFileMetadata metadata)
    {
        EnsureRegularFile(metadata, "metadata");
        var modified = metadata.ModifiedAtUtc?.ToUniversalTime().Ticks ?? 0;
        var canonical = Encoding.UTF8.GetBytes($"{metadata.Size ?? 0}:{modified}");
        return $"\"{Convert.ToHexString(SHA256.HashData(canonical)).ToLowerInvariant()}\"";
    }

    public static void ValidateReadRange(long offset, int length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "offset は 0 以上で指定してください。");
        if (length <= 0 || length > TransferV2Limits.MaxChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(length),
                $"length は 1〜{TransferV2Limits.MaxChunkBytes} バイトで指定してください。");
        if (offset > long.MaxValue - length)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset + length が Int64 の範囲を超えています。");
    }

    public static void ValidateChunk(long offset, int length)
    {
        ValidateReadRange(offset, length);
    }

    public static string NormalizeAndValidateTempPath(string path)
    {
        var normalized = PathHelper.NormalizePath(path);
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (!name.StartsWith(TransferV2Limits.TempFilePrefix, StringComparison.OrdinalIgnoreCase) ||
            !name.EndsWith(TransferV2Limits.TempFileSuffix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfer v2 の書き込み先は Watashi の一時ファイル名である必要があります。", nameof(path));

        var tokenLength = name.Length - TransferV2Limits.TempFilePrefix.Length - TransferV2Limits.TempFileSuffix.Length;
        if (tokenLength is < 1 or > 128)
            throw new ArgumentException("Transfer v2 の一時ファイル識別子が不正です。", nameof(path));
        var token = name.AsSpan(TransferV2Limits.TempFilePrefix.Length, tokenLength);
        foreach (var ch in token)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
                throw new ArgumentException("Transfer v2 の一時ファイル識別子が不正です。", nameof(path));
        }
        return normalized;
    }

    /// <summary>
    /// 既存長と、要求 offset から読み戻した既存 byte を検査し、chunk の何 byte 目から
    /// 書き足すべきか返す。同一再送は chunk.Length、途中再送は既存 prefix 長になる。
    /// </summary>
    internal static int ReconcileChunk(
        long currentLength,
        long requestedOffset,
        ReadOnlySpan<byte> existingAtOffset,
        ReadOnlySpan<byte> chunk)
    {
        ValidateChunk(requestedOffset, chunk.Length);
        if (currentLength < requestedOffset)
            throw new TransferOffsetMismatchException(requestedOffset, currentLength, "未書き込みの空白があります。");

        var overlap = (int)Math.Min(currentLength - requestedOffset, chunk.Length);
        if (existingAtOffset.Length != overlap)
            throw new IOException($"既存 chunk の読み戻し長が一致しません (expected={overlap}, actual={existingAtOffset.Length})。");
        if (!existingAtOffset.SequenceEqual(chunk[..overlap]))
            throw new TransferOffsetMismatchException(
                requestedOffset,
                currentLength,
                "再送された chunk の内容が既存データと一致しません。");
        return overlap;
    }

    internal static void EnsureRegularFile(TransferFileMetadata metadata, string path)
    {
        if (metadata.IsReparsePoint) throw new TransferReparsePointException(path);
        if (!metadata.Exists || metadata.Type != TransferFileTypes.File)
            throw new IOException($"Transfer v2 の対象は通常ファイルである必要があります: {path}");
    }
}

public static class TransferHashing
{
    public static string ComputeSha256Hex(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static TransferSha256Result ComputeSha256(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                size = checked(size + read);
            }
            ct.ThrowIfCancellationRequested();
            return new TransferSha256Result(
                "SHA-256",
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                size);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
