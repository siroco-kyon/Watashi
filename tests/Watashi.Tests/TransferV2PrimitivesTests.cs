using System.Text;
using FluentAssertions;
using Watashi.Shared.Cifs;

namespace Watashi.Tests;

public class TransferV2PrimitivesTests
{
    [Theory]
    [InlineData("/.watashi-upload-a.tmp")]
    [InlineData("/dept/reports/.watashi-upload-abc_123-XYZ.tmp")]
    public void Temp_path_validation_accepts_only_watashi_temp_names(string path)
    {
        TransferV2Validation.NormalizeAndValidateTempPath(path).Should().Be(path);
    }

    [Theory]
    [InlineData("/dept/report.csv")]
    [InlineData("/dept/.watashi-upload-.tmp")]
    [InlineData("/dept/.watashi-upload-bad.token.tmp")]
    [InlineData("/dept/.watashi-upload-a.partial")]
    public void Temp_path_validation_rejects_formal_or_malformed_names(string path)
    {
        var act = () => TransferV2Validation.NormalizeAndValidateTempPath(path);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Range_validation_enforces_non_negative_offset_and_chunk_limit()
    {
        TransferV2Validation.ValidateReadRange(0, 1);
        TransferV2Validation.ValidateReadRange(long.MaxValue - TransferV2Limits.MaxChunkBytes,
            TransferV2Limits.MaxChunkBytes);

        var negative = () => TransferV2Validation.ValidateReadRange(-1, 1);
        var empty = () => TransferV2Validation.ValidateReadRange(0, 0);
        var oversized = () => TransferV2Validation.ValidateReadRange(0, TransferV2Limits.MaxChunkBytes + 1);
        var overflow = () => TransferV2Validation.ValidateReadRange(long.MaxValue, 1);

        negative.Should().Throw<ArgumentOutOfRangeException>();
        empty.Should().Throw<ArgumentOutOfRangeException>();
        oversized.Should().Throw<ArgumentOutOfRangeException>();
        overflow.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Commit_paths_must_share_parent_and_target_cannot_use_reserved_name()
    {
        var paths = TransferV2Validation.ValidateCommitPaths(
            "/dept/.watashi-upload-abc.tmp", "/dept/report.csv");
        paths.Should().Be(("/dept/.watashi-upload-abc.tmp", "/dept/report.csv"));

        var crossFolder = () => TransferV2Validation.ValidateCommitPaths(
            "/dept/.watashi-upload-abc.tmp", "/other/report.csv");
        var reservedTarget = () => TransferV2Validation.NormalizeUploadTargetPath(
            "/dept/.watashi-upload-user.tmp");

        crossFolder.Should().Throw<ArgumentException>().WithMessage("*同じ親*");
        reservedTarget.Should().Throw<ArgumentException>().WithMessage("*予約済み*");
    }

    [Theory]
    [InlineData("/.watashi-upload-session.tmp")]
    [InlineData("/dept/.WATASHI-UPLOAD-session.tmp")]
    [InlineData("/dept/.watashi-upload-container/visible.txt")]
    public void User_operations_reject_reserved_temp_path_at_any_depth(string path)
    {
        TransferV2Validation.IsReservedTempPath(path).Should().BeTrue();

        var act = () => TransferV2Validation.NormalizeAndValidateUserPath(path);

        act.Should().Throw<UnauthorizedAccessException>().WithMessage("*予約済み一時ファイル*");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dept/report.csv")]
    [InlineData("/dept/prefix-.watashi-upload-report.csv")]
    public void User_operations_allow_non_reserved_paths(string path)
    {
        TransferV2Validation.NormalizeAndValidateUserPath(path)
            .Should().Be(path);
    }

    [Fact]
    public void Sha256_and_idempotency_key_normalization_are_canonical()
    {
        var uppercase = new string('A', 64);
        TransferV2Validation.NormalizeSha256(uppercase).Should().Be(new string('a', 64));
        TransferV2Validation.HashIdempotencyKey("client-operation-1")
            .Should().HaveLength(64).And.NotContain("client-operation-1");

        var badHash = () => TransferV2Validation.NormalizeSha256(new string('z', 64));
        var longKey = () => TransferV2Validation.HashIdempotencyKey(
            new string('あ', TransferV2Limits.MaxIdempotencyKeyBytes));
        badHash.Should().Throw<ArgumentException>();
        longKey.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Metadata_etag_changes_with_size_or_modified_time()
    {
        var at = new DateTime(2026, 8, 14, 1, 2, 3, DateTimeKind.Utc);
        var first = TransferV2Validation.BuildMetadataETag(new TransferFileMetadata(
            true, TransferFileTypes.File, 10, at, false));
        var same = TransferV2Validation.BuildMetadataETag(new TransferFileMetadata(
            true, TransferFileTypes.File, 10, at, false));
        var changedSize = TransferV2Validation.BuildMetadataETag(new TransferFileMetadata(
            true, TransferFileTypes.File, 11, at, false));
        var changedTime = TransferV2Validation.BuildMetadataETag(new TransferFileMetadata(
            true, TransferFileTypes.File, 10, at.AddTicks(1), false));

        first.Should().Be(same).And.StartWith("\"").And.EndWith("\"");
        changedSize.Should().NotBe(first);
        changedTime.Should().NotBe(first);
    }

    [Fact]
    public void Chunk_reconciliation_handles_append_partial_retry_and_complete_retry()
    {
        var chunk = Encoding.ASCII.GetBytes("abcdefgh");

        TransferV2Validation.ReconcileChunk(10, 10, [], chunk).Should().Be(0);
        TransferV2Validation.ReconcileChunk(13, 10, chunk[..3], chunk).Should().Be(3);
        TransferV2Validation.ReconcileChunk(18, 10, chunk, chunk).Should().Be(chunk.Length);
        // 後続 chunk まで進んだ後の古い retry も、対象範囲が同一なら安全な no-op。
        TransferV2Validation.ReconcileChunk(30, 10, chunk, chunk).Should().Be(chunk.Length);
    }

    [Fact]
    public void Chunk_reconciliation_rejects_gap_and_different_retry_content()
    {
        var chunk = Encoding.ASCII.GetBytes("abcdefgh");

        var gap = () => TransferV2Validation.ReconcileChunk(9, 10, [], chunk);
        var changed = () => TransferV2Validation.ReconcileChunk(
            13, 10, Encoding.ASCII.GetBytes("abX"), chunk);

        gap.Should().Throw<TransferOffsetMismatchException>()
            .Where(x => x.ExpectedOffset == 10 && x.ActualOffset == 9);
        changed.Should().Throw<TransferOffsetMismatchException>()
            .WithMessage("*内容が既存データと一致しません*");
    }

    [Fact]
    public void Regular_file_validation_rejects_reparse_points_and_directories()
    {
        var reparse = new TransferFileMetadata(
            true, TransferFileTypes.File, 10, DateTime.UtcNow, IsReparsePoint: true);
        var directory = new TransferFileMetadata(
            true, TransferFileTypes.Directory, null, DateTime.UtcNow, IsReparsePoint: false);

        var reparseAct = () => TransferV2Validation.EnsureRegularFile(reparse, "/link.bin");
        var directoryAct = () => TransferV2Validation.EnsureRegularFile(directory, "/folder");

        reparseAct.Should().Throw<TransferReparsePointException>().WithMessage("*リパースポイント*");
        directoryAct.Should().Throw<IOException>().WithMessage("*通常ファイル*");
    }

    [Fact]
    public void Sha256_returns_lowercase_digest_and_exact_size()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

        var result = TransferHashing.ComputeSha256(stream);

        result.Algorithm.Should().Be("SHA-256");
        result.Hash.Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        result.Size.Should().Be(3);
    }

    [Fact]
    public void Sha256_honors_pre_cancelled_token_before_reading()
    {
        using var stream = new MemoryStream(new byte[32]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TransferHashing.ComputeSha256(stream, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        stream.Position.Should().Be(0);
    }
}
