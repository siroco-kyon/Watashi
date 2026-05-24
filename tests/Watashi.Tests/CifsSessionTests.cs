using System.IO;
using FluentAssertions;
using Watashi.Shared.Cifs;
using Xunit;

namespace Watashi.Tests;

public class CifsSessionTests
{
    [Theory]
    [InlineData(443)]
    [InlineData(8080)]
    [InlineData(65535)]
    public void Connect_rejects_unsupported_ports_with_clear_message(int port)
    {
        var info = new CifsConnectionInfo("127.0.0.1", port, "u", "p", "share");
        // 実 SMB 接続まで行かず port バリデーションだけ走るので、ネットワーク無し環境でも動く。
        Action act = () => CifsSession.Connect(info);
        act.Should().Throw<IOException>()
            .WithMessage("*ポート*未対応*");
    }
}
