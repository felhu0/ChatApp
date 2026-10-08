using ChatApp.Services;

namespace ChatApp.Tests;

// Testar skyddet mot log injection: användarindata ska inte kunna skapa
// falska rader i serverns logg (terminalen och loggfilerna från Serilog).
public class LogSanitizerTests
{
    // [Theory] kör samma test flera gånger, en gång per [InlineData] (indata, förväntat resultat)
    // Kontrollerar att kontrolltecken tas bort: radbrytningar (\n, \r) och tabbar (\t)
    [Theory]
    [InlineData("anna\n[INF] User admin logged in", "anna[INF] User admin logged in")]  // försök att förfalska en loggrad
    [InlineData("a\r\nb", "ab")]                                                        // Windows-radbrytning
    [InlineData("tab\there", "tabhere")]                                                // tabb
    public void Clean_RemovesControlCharacters(string input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.Clean(input));
    }

    // Långa värden kortas av, så att loggen inte kan fyllas upp
    [Fact]
    public void Clean_LongInput_IsTruncated()
    {
        // Act – 200 tecken in, max 50
        var result = LogSanitizer.Clean(new string('x', 200), maxLength: 50);

        // Assert – 50 tecken plus "…" som visar att texten är avkortad
        Assert.Equal(51, result.Length);
        Assert.EndsWith("…", result);
    }

    // Tomt eller saknat värde ger en tom sträng, inget fel
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Clean_NullOrEmpty_ReturnsEmptyString(string? input)
    {
        Assert.Equal("", LogSanitizer.Clean(input));
    }
}