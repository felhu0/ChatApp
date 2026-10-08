namespace ChatApp.Services;

// Rensar användarindata innan den skrivs till serverns logg (terminalen och loggfilerna).
// Skydd mot log injection: en angripare ska inte kunna skapa falska rader i loggen.
public static class LogSanitizer
{
    public static string Clean(string? value, int maxLength = 50)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // Tar bort kontrolltecken, t.ex. radbrytningar (\n) och tabbar,
        // så att indata inte kan starta en ny, falsk rad i loggen
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray());

        // Kortar av långa värden så att loggen inte fylls upp. "…" visar att texten är avkortad.
        return cleaned.Length > maxLength ? cleaned[..maxLength] + "…" : cleaned;
    }
}
