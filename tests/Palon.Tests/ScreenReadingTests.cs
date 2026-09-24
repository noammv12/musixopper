using System.Text.Json;
using Palon.Vision;
using Xunit;

namespace Palon.Tests;

public class RegionMathTests
{
    [Fact]
    public void FromDrag_NormalizesAnyDirection()
    {
        Assert.Equal((10.0, 20.0, 30.0, 40.0), RegionMath.FromDrag(40, 60, 10, 20));
        Assert.Equal((10.0, 20.0, 30.0, 40.0), RegionMath.FromDrag(10, 20, 40, 60));
    }

    [Fact]
    public void FromPoints_IsInclusiveOfBothPixels()
    {
        Assert.Equal(new PxRect(100, 50, 11, 6), RegionMath.FromPoints(110, 55, 100, 50));
        Assert.Equal(new PxRect(-1920, 0, 1, 1), RegionMath.FromPoints(-1920, 0, -1920, 0));
    }

    [Theory]
    [InlineData(1.0, 10, 20, 100, 50)]
    [InlineData(1.5, 15, 30, 150, 75)]
    [InlineData(2.0, 20, 40, 200, 100)]
    public void DipToPhysical_ScalesByMonitorDpi(double scale, int x, int y, int w, int h)
    {
        Assert.Equal(new PxRect(x, y, w, h), RegionMath.DipToPhysical(10, 20, 100, 50, scale, 0, 0));
    }

    [Fact]
    public void DipToPhysical_RoundsOutwardAndAddsMonitorOrigin()
    {
        // 125%: 10.3 DIP → 12.875 px floors to 12; right edge 30.3 DIP → 37.875 ceils to 38.
        var r = RegionMath.DipToPhysical(10.3, 0, 20, 10, 1.25, -2560, 200);
        Assert.Equal(-2560 + 12, r.X);
        Assert.Equal(200, r.Y);
        Assert.Equal(38 - 12, r.Width);
        Assert.Equal(13, r.Height); // 12.5 → 13
    }

    [Fact]
    public void PhysicalToDip_InvertsDipToPhysical()
    {
        var (x, y) = RegionMath.PhysicalToDip(-2560 + 300, 150, 1.5, -2560, 0);
        Assert.Equal(200, x, 6);
        Assert.Equal(100, y, 6);
    }

    [Fact]
    public void ToBitmap_ShiftsByVirtualOriginAndClamps()
    {
        // Virtual screen starts at a left monitor (-1920, -200); bitmap 4480×1640.
        var inBitmap = RegionMath.ToBitmap(new PxRect(-1920, -200, 100, 100), -1920, -200, 4480, 1640);
        Assert.Equal(new PxRect(0, 0, 100, 100), inBitmap);
        var clamped = RegionMath.ToBitmap(new PxRect(2500, 1400, 400, 400), -1920, -200, 4480, 1640);
        Assert.Equal(new PxRect(4420, 1600, 60, 40), clamped);
        Assert.True(RegionMath.ToBitmap(new PxRect(9000, 0, 10, 10), 0, 0, 1920, 1080).IsEmpty);
    }

    [Fact]
    public void IsUsable_RejectsClicksAndSlivers()
    {
        Assert.False(RegionMath.IsUsable(new PxRect(0, 0, 3, 200)));
        Assert.True(RegionMath.IsUsable(new PxRect(0, 0, 8, 8)));
    }

    [Fact]
    public void HitTest_TopmostWindowWins()
    {
        var top = new WindowBox(1, "top", new PxRect(100, 100, 200, 200));
        var below = new WindowBox(2, "below", new PxRect(0, 0, 1000, 1000));
        var z = new[] { top, below };
        Assert.Equal("top", RegionMath.HitTest(z, 150, 150)?.Title);
        Assert.Equal("below", RegionMath.HitTest(z, 50, 50)?.Title);
        Assert.Null(RegionMath.HitTest(z, 5000, 5000));
    }

    [Fact]
    public void FitWithin_CapsEdgeAndPixelsKeepingAspect()
    {
        Assert.Equal((800, 600), RegionMath.FitWithin(800, 600));
        Assert.Equal((2000, 1000), RegionMath.FitWithin(4000, 2000));
        var (w, h) = RegionMath.FitWithin(2000, 2000);
        Assert.True((long)w * h <= 3_000_000);
        Assert.Equal(w, h);
        Assert.Equal((0, 0), RegionMath.FitWithin(0, 10));
    }
}

public class ReceiptExtractionTests
{
    [Theory]
    [InlineData("3,000.00", 3000)]
    [InlineData("3.000,00", 3000)]
    [InlineData("3,000", 3000)]
    [InlineData("$1,250.50", 1250.5)]
    [InlineData("1,234,567", 1234567)]
    [InlineData("250", 250)]
    [InlineData("99.9", 99.9)]
    public void ParseAmount_HandlesSeparators(string text, double expected) =>
        Assert.Equal((decimal)expected, ReceiptExtraction.ParseAmount(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0.00")]
    public void ParseAmount_RejectsNonAmounts(string text) => Assert.Null(ReceiptExtraction.ParseAmount(text));

    [Theory]
    [InlineData("סכום ההעברה: 3,000.00 $", 3000, true)]
    [InlineData("Amount 3 000 USD", 3000, true)]
    [InlineData("Date 12.09.2026\nTotal 3000", 3000, true)]
    [InlineData("Total 30000", 3000, false)]
    [InlineData("Total 300", 3000, false)]
    [InlineData("", 3000, false)]
    public void AmountAppears_ComparesWholeNumberTokens(string transcript, double amount, bool expected) =>
        Assert.Equal(expected, ReceiptExtraction.AmountAppears((decimal)amount, transcript));

    [Fact]
    public void Parse_ValidReceipt_IsVerified()
    {
        var raw = """
            ```json
            {"transcript":"בנק הפועלים\nהעברה מאת: דני לוי\nסכום: 5,000.00 USD\nאסמכתא 99812",
             "payer_name":"דני לוי","amount":"5,000.00","currency":"USD","date":"2026-09-21",
             "method":"בנק הפועלים","reference":"99812"}
            ```
            """;
        var r = ReceiptExtraction.Parse(raw)!;
        Assert.Equal("דני לוי", r.PayerName);
        Assert.Equal(5000m, r.Amount);
        Assert.Equal("USD", r.Currency);
        Assert.Equal(new DateTime(2026, 9, 21), r.Date);
        Assert.True(r.AmountVerified);
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Parse_AmountMissingFromTranscript_IsFlagged()
    {
        var r = ReceiptExtraction.Parse("""{"transcript":"Total 4,000","amount":"5000","currency":"$"}""")!;
        Assert.Equal(5000m, r.Amount);
        Assert.False(r.AmountVerified);
        Assert.Contains(r.Warnings, w => w.Contains("לא נמצא בטקסט"));
    }

    [Fact]
    public void Parse_ForeignCurrencyAndNulls()
    {
        var r = ReceiptExtraction.Parse("""{"transcript":"₪ 12,000","payer_name":null,"amount":"12,000","currency":"ש\"ח","date":"21/09/2026"}""")!;
        Assert.Null(r.PayerName);
        Assert.Equal("ILS", r.Currency);
        Assert.True(r.AmountVerified);
        Assert.Null(r.Date);
        Assert.Contains(r.Warnings, w => w.Contains("ILS"));
        Assert.Contains(r.Warnings, w => w.Contains("התאריך"));
    }

    [Fact]
    public void Parse_NoJson_ReturnsNull()
    {
        Assert.Null(ReceiptExtraction.Parse("I cannot read this image."));
        Assert.Null(ReceiptExtraction.Parse("{not json}"));
    }

    [Fact]
    public void DealPrefill_UsesRecentReceiptMonthAndBuildsNote()
    {
        var r = ReceiptExtraction.Parse("""{"transcript":"1,500","payer_name":"Ann","amount":"1,500","currency":"USD","date":"2026-08-30","method":"Wire","reference":"X1"}""")!;
        var p = DealPrefill.FromReceipt(r, new DateTime(2026, 9, 2));
        Assert.Equal((2026, 8), (p.Year, p.Month));
        Assert.Equal(new DateTime(2026, 8, 30), p.Date);
        Assert.Equal(1500m, p.AmountUsd);
        Assert.Equal("מקבלה: Wire · אסמכתא X1", p.Note);
    }

    [Fact]
    public void DealPrefill_FutureOrOldDate_FallsBackToThisMonth()
    {
        var r = ReceiptExtraction.Parse("""{"transcript":"100","amount":"100","date":"2027-01-01"}""")!;
        var p = DealPrefill.FromReceipt(r, new DateTime(2026, 9, 24));
        Assert.Equal((2026, 9), (p.Year, p.Month));
        Assert.Null(p.Date);
        Assert.Contains(p.Warnings, w => w.Contains("לא בחודשים האחרונים"));
    }
}

public class VisionPayloadTests
{
    [Fact]
    public void UserMessage_IsOpenAiMultimodalShape()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        var json = JsonSerializer.Serialize(VisionPayload.UserMessage("מה כתוב פה?", jpeg));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("user", root.GetProperty("role").GetString());
        var parts = root.GetProperty("content");
        Assert.Equal(2, parts.GetArrayLength());
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("מה כתוב פה?", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
        Assert.Equal("data:image/jpeg;base64,/9j/2Q==", parts[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("Gemini", "gemini-3.8-flash", true)]
    [InlineData("DeepSeek", "deepseek-flash", true)]
    [InlineData("DeepSeek", "deepseek-v4-pro", false)]
    public void SupportsImages_SkipsTextOnlyModels(string provider, string model, bool expected) =>
        Assert.Equal(expected, VisionPayload.SupportsImages(provider, model));

    [Fact]
    public void ParseRead_JsonAndProseFallback()
    {
        var r = VisionPayload.ParseRead("""{"transcript":"Invoice 42","answer":"זו חשבונית מספר 42."}""");
        Assert.Equal("Invoice 42", r.Transcript);
        Assert.Equal("זו חשבונית מספר 42.", r.Answer);
        var prose = VisionPayload.ParseRead("כתוב שם 'שלום'.");
        Assert.Equal("כתוב שם 'שלום'.", prose.Answer);
    }
}
