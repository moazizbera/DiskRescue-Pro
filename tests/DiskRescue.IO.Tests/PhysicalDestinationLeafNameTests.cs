using DiskRescue.IO;
using Xunit;

namespace DiskRescue.IO.Tests;

public sealed class PhysicalDestinationLeafNameTests
{
    [Theory]
    [InlineData("disk.img")]
    [InlineData(".disk.img.0123456789abcdef.partial")]
    [InlineData("disk image — 01.img")]
    [InlineData("acquisition.diskrescue.json")]
    public void ValidateAcceptsOrdinaryLeafNames(string name)
    {
        Assert.Equal(name, PhysicalDestinationLeafName.Validate(name, "name"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../disk.img")]
    [InlineData(@"..\disk.img")]
    [InlineData(@"C:\disk.img")]
    [InlineData("disk.img.")]
    [InlineData("disk.img ")]
    [InlineData("disk<bad>.img")]
    [InlineData("disk|bad.img")]
    [InlineData("disk?bad.img")]
    [InlineData("bad\0name.img")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("NUL.json")]
    [InlineData("COM1.img")]
    [InlineData("LPT9.log")]
    public void ValidateRejectsPathsAndReservedNames(string name)
    {
        Assert.Throws<ArgumentException>(() =>
            PhysicalDestinationLeafName.Validate(name, "name"));
    }
}
