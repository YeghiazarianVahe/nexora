using FluentAssertions;
using Nexora.Contracts;

namespace Nexora.Application.Tests;
public sealed class SigningLayoutTests
{
    [Theory]
    [InlineData(0)] [InlineData(7)]
    public void Trust_key_before_a_certificate_table_is_read_without_mutating_the_file(int padding)
    {
        var key=new byte[]{1,2,3}; var stamp=TrustStamp.Tail(key);
        var offset=512+stamp.Length+padding;
        var bytes=new byte[offset+16]; bytes[0]=0x4D;bytes[1]=0x5A;
        BitConverter.GetBytes(128).CopyTo(bytes,60); BitConverter.GetBytes(0x4550).CopyTo(bytes,128);
        BitConverter.GetBytes((ushort)240).CopyTo(bytes,148); BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes,152);
        BitConverter.GetBytes((uint)offset).CopyTo(bytes,296); BitConverter.GetBytes(16u).CopyTo(bytes,300);
        stamp.CopyTo(bytes,512);
        using var stream=new MemoryStream(bytes);
        TrustStamp.Read(stream).Key.Should().Equal(key);
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".exe");
        try
        {
            File.WriteAllBytes(path,bytes);
            TrustStamp.HasCertificateTable(path).Should().BeTrue();
            TrustStamp.StampInPlace(path,new byte[]{4,5,6}).Should().BeFalse();
            File.ReadAllBytes(path).Should().Equal(bytes);
        }
        finally { File.Delete(path); }
    }
}
