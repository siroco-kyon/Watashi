using FluentAssertions;
using Watashi.Shared.Cifs;
using Xunit;

namespace Watashi.Tests;

public class CifsSessionPoolTests
{
    [Fact]
    public void Acquire_after_dispose_throws_ObjectDisposedException()
    {
        var pool = new CifsSessionPool(idleTtl: TimeSpan.FromSeconds(10));
        pool.Dispose();

        // SMB に実際に接続しに行く前に、Dispose 後ガードで弾かれる。
        Action act = () => pool.Acquire(new CifsConnectionInfo("127.0.0.1", 445, "u", "p", "s"));
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var pool = new CifsSessionPool(idleTtl: TimeSpan.FromSeconds(10));
        pool.Dispose();
        // 2 回目以降の Dispose は静かに何もしない。
        Action act = () => pool.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Acquire_unsupported_port_propagates_IOException()
    {
        // プールは Dispose 前なので Connect 経由でポート検証エラーが上がる。
        using var pool = new CifsSessionPool();
        Action act = () => pool.Acquire(new CifsConnectionInfo("127.0.0.1", 8080, "u", "p", "s"));
        act.Should().Throw<IOException>().WithMessage("*ポート*未対応*");
    }

    [Fact]
    public void Pool_key_changes_when_password_changes_and_does_not_expose_password()
    {
        var first = new CifsConnectionInfo("server", 445, "user", "secret-one", "share");
        var second = first with { Password = "secret-two" };

        var firstKey = CifsSessionPool.Key(first);
        var secondKey = CifsSessionPool.Key(second);

        firstKey.Should().NotBe(secondKey);
        firstKey.Should().NotContain("secret-one");
        secondKey.Should().NotContain("secret-two");
    }
}
