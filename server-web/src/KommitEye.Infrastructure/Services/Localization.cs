namespace KommitEye.Services;

public static class Localization
{
    private static string _language = "en";
    private static readonly AsyncLocal<string?> ScopedLanguage = new();

    public static string Language => ScopedLanguage.Value ?? _language;
    public static bool IsRussian => Language == "ru";
    public static IDisposable UseLanguage(string language)
    {
        var previous = ScopedLanguage.Value;
        ScopedLanguage.Value = language == "ru" ? "ru" : "en";
        return new LanguageScope(previous);
    }
    private sealed class LanguageScope(string? previous) : IDisposable
    {
        public void Dispose() => ScopedLanguage.Value = previous;
    }

    public static void SetLanguage(string? language) =>
        _language = language?.Equals("ru", StringComparison.OrdinalIgnoreCase) == true ? "ru" : "en";

    public static string Text(string english, string russian) => IsRussian ? russian : english;

    public static string Format(string english, string russian, params object?[] arguments) =>
        string.Format(IsRussian ? russian : english, arguments);
}
