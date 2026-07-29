using PGW.Core;
using Xunit;

namespace PGW.Core.Tests;

public class CoreUnitTests
{
    [Fact]
    public void Linear_Scaling_Round_Trips()
    {
        var cfg = new ScalingConfig(ScalingMode.Linear, RawLo: 0, RawHi: 4095, EuLo: 0, EuHi: 100);
        var eu = Scaling.Apply(cfg, 2047.5);
        Assert.Equal(50.0, Convert.ToDouble(eu), 3);
        var raw = Scaling.Reverse(cfg, 50.0);
        Assert.Equal(2047.5, Convert.ToDouble(raw), 3);
    }

    [Fact]
    public void Scaling_Clamps_Out_Of_Range_Values()
    {
        var cfg = new ScalingConfig(ScalingMode.Linear, 0, 100, 0, 10, Clamp: true);
        Assert.Equal(10.0, Convert.ToDouble(Scaling.Apply(cfg, 500)));
        Assert.Equal(0.0, Convert.ToDouble(Scaling.Apply(cfg, -500)));
    }

    [Fact]
    public void New_Tag_Starts_Bad_Not_Initialized()
    {
        var ts = new TagSpace();
        var device = new DeviceHandle("ch", "dev");
        var def = new TagDefinition("ch.dev.t", TagDataType.Float32, TagAccess.RO, device, "addr");
        ts.Register(def, new FakeProtocolDriver(), new TagAddress(device, "addr"));

        var snap = ts.Get("ch.dev.t");
        Assert.NotNull(snap);
        Assert.Equal(TagQuality.Bad, snap!.Quality);
        Assert.Equal(QualitySubCode.NotInitialized, snap.SubCode);
    }

    [Fact]
    public void Deadband_Suppresses_Small_Changes_But_Not_Large_Ones()
    {
        var ts = new TagSpace();
        var device = new DeviceHandle("ch", "dev");
        var def = new TagDefinition("ch.dev.t", TagDataType.Float32, TagAccess.RO, device, "addr", Deadband: 1.0);
        ts.Register(def, new FakeProtocolDriver(), new TagAddress(device, "addr"));

        var changes = new List<TagSnapshot>();
        ts.Changed += changes.Add;

        ts.Publish("ch.dev.t", 10.0f, TagQuality.Good, DateTime.UtcNow);
        ts.Publish("ch.dev.t", 10.5f, TagQuality.Good, DateTime.UtcNow); // within deadband, suppressed
        ts.Publish("ch.dev.t", 12.0f, TagQuality.Good, DateTime.UtcNow); // outside deadband, published

        Assert.Equal(2, changes.Count);
        Assert.Equal(10.0f, Convert.ToSingle(changes[0].Value));
        Assert.Equal(12.0f, Convert.ToSingle(changes[1].Value));
    }

    [Fact]
    public async Task Write_To_ReadOnly_Tag_Is_Rejected()
    {
        var ts = new TagSpace();
        var device = new DeviceHandle("ch", "dev");
        var def = new TagDefinition("ch.dev.t", TagDataType.Float32, TagAccess.RO, device, "addr");
        var driver = new FakeProtocolDriver();
        ts.Register(def, driver, new TagAddress(device, "addr"));

        var result = await ts.WriteAsync("ch.dev.t", 5.0, TimeSpan.FromSeconds(1), default);

        Assert.False(result.Ok);
        Assert.Equal("read_only", result.ErrorCode);
        Assert.Null(driver.LastWritten);
    }

    [Fact]
    public async Task Write_To_RW_Tag_Reaches_Owning_Driver()
    {
        var ts = new TagSpace();
        var device = new DeviceHandle("ch", "dev");
        var def = new TagDefinition("ch.dev.t", TagDataType.Float32, TagAccess.RW, device, "addr");
        var driver = new FakeProtocolDriver();
        ts.Register(def, driver, new TagAddress(device, "addr"));

        var result = await ts.WriteAsync("ch.dev.t", 5.0, TimeSpan.FromSeconds(1), default);

        Assert.True(result.Ok);
        Assert.Equal(5.0f, Convert.ToSingle(driver.LastWritten));
    }
}
