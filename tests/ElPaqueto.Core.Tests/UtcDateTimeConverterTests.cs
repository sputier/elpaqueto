using System;
using ElPaqueto.Core;
using Xunit;

namespace ElPaqueto.Core.Tests;

public class UtcDateTimeConverterTests
{
    private static readonly DateTime Moment = new(2020, 1, 1, 12, 0, 0);

    [Fact]
    public void ToProvider_KeepsUtc()
    {
        var utc = DateTime.SpecifyKind(Moment, DateTimeKind.Utc);

        var written = (DateTime)UtcDateTimeConverter.Instance.ConvertToProvider(utc);

        Assert.Equal(utc, written);
        Assert.Equal(DateTimeKind.Utc, written.Kind);
    }

    [Fact]
    public void ToProvider_ConvertsLocalToUtc()
    {
        var local = DateTime.SpecifyKind(Moment, DateTimeKind.Local);

        var written = (DateTime)UtcDateTimeConverter.Instance.ConvertToProvider(local);

        Assert.Equal(DateTimeKind.Utc, written.Kind);
        Assert.Equal(local.ToUniversalTime(), written);
    }

    [Fact]
    public void ToProvider_TakesUnspecifiedForUtc()
    {
        var written = (DateTime)UtcDateTimeConverter.Instance.ConvertToProvider(Moment);

        Assert.Equal(DateTimeKind.Utc, written.Kind);
        Assert.Equal(Moment.Ticks, written.Ticks);
    }

    [Fact]
    public void ToProvider_AcceptsMinValue()
    {
        var written = (DateTime)UtcDateTimeConverter.Instance.ConvertToProvider(DateTime.MinValue);

        Assert.Equal(DateTimeKind.Utc, written.Kind);
    }

    [Fact]
    public void FromProvider_ReadsAsUtc()
    {
        var read = (DateTime)UtcDateTimeConverter.Instance.ConvertFromProvider(Moment);

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(Moment.Ticks, read.Ticks);
    }
}
