namespace Content.Shared._Crescent.Translator;

public static class ChatTranslatorLanguages
{
    // guess from the alphabet, only ru/en
    public static string? Guess(string text)
    {
        var cyrillic = 0;
        var latin = 0;
        foreach (var c in text)
        {
            if (c is >= 'Ѐ' and <= 'ӿ')
                cyrillic++;
            else if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
                latin++;
        }

        if (cyrillic + latin < 3)
            return null;

        return cyrillic >= latin ? "ru" : "en";
    }

    // ru-RU -> ru
    public static string Short(string culture)
    {
        var dash = culture.IndexOf('-');
        return (dash > 0 ? culture[..dash] : culture).ToLowerInvariant();
    }
}
