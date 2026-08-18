using Drps.Shared.Helpers;

namespace Drps.Tests;

public class TickerSymbolHelperTests
{
    [Theory]
    [InlineData("AAPL")]
    [InlineData("MSFT")]
    [InlineData("A")]
    [InlineData("GOOGL")]
    public void IsValidSymbol_PlainEquityTicker_ReturnsTrue(string ticker)
    {
        Assert.True(TickerSymbolHelper.IsValidSymbol(ticker));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidSymbol_NullOrWhitespace_ReturnsFalse(string ticker)
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol(ticker));
    }

    [Fact]
    public void IsValidSymbol_LongerThanFiveCharacters_ReturnsFalse()
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol("ABCDEF"));
    }

    [Theory]
    [InlineData("AB1")]
    [InlineData("A2C")]
    public void IsValidSymbol_ContainsDigit_ReturnsFalse(string ticker)
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol(ticker));
    }

    [Fact]
    public void IsValidSymbol_AllDigits_ReturnsFalse()
    {
        // Can't occur via the real all-letters check above, but IsValidSymbol carries this as
        // a separate, explicit guard - tested directly against the guard itself.
        Assert.False(TickerSymbolHelper.IsValidSymbol("12345"));
    }

    // Warrant/rights/preferred/unit suffix filtering - the exact reason CapitalFill built this
    // helper in the first place (these are real NasdaqTrader rows, not synthetic edge cases).
    [Theory]
    [InlineData("ABCWS")]
    [InlineData("ABCWR")]
    [InlineData("ABCWT")]
    [InlineData("ABCRT")]
    [InlineData("ABCPR")]
    [InlineData("ABCW")]
    [InlineData("ABCR")]
    [InlineData("ABCP")]
    [InlineData("ABCU")]
    public void IsValidSymbol_WarrantRightsPreferredOrUnitSuffix_ReturnsFalse(string ticker)
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol(ticker));
    }

    [Fact]
    public void IsValidSymbol_ContainsNonLetterCharacter_ReturnsFalse()
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol("AB.C"));
    }

    // "ACT SYMBOL" (with a space, from otherlisted.txt's un-skipped header row) must fail here -
    // UniverseIngestionRunner's ParseTickers relies on this exact behavior to incidentally
    // filter that header row out, since its own "Symbol" prefix check only literally matches
    // nasdaqlisted.txt's header.
    [Fact]
    public void IsValidSymbol_ActSymbolHeaderText_ReturnsFalse()
    {
        Assert.False(TickerSymbolHelper.IsValidSymbol("ACT SYMBOL"));
    }
}
