using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // also needs translator.api_key (deepl) or a libretranslate url
    public static readonly CVarDef<bool> TranslatorEnabled =
        CVarDef.Create("translator.enabled", false, CVar.SERVER | CVar.REPLICATED);

    // deepl or libretranslate
    public static readonly CVarDef<string> TranslatorProvider =
        CVarDef.Create("translator.provider", "deepl", CVar.SERVERONLY);

    // deepl: leave empty. libretranslate: server root like http://localhost:5000
    public static readonly CVarDef<string> TranslatorUrl =
        CVarDef.Create("translator.url", "", CVar.SERVERONLY);

    public static readonly CVarDef<string> TranslatorApiKey =
        CVarDef.Create("translator.api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    // libretranslate url used when the main one fails, empty = none
    public static readonly CVarDef<string> TranslatorFallbackUrl =
        CVarDef.Create("translator.fallback_url", "", CVar.SERVERONLY);

    public static readonly CVarDef<string> TranslatorFallbackApiKey =
        CVarDef.Create("translator.fallback_api_key", "", CVar.SERVERONLY | CVar.CONFIDENTIAL);

    // new translations per player per minute
    public static readonly CVarDef<int> TranslatorPerMinute =
        CVarDef.Create("translator.per_minute", 30, CVar.SERVERONLY);

    // longer lines are not translated
    public static readonly CVarDef<int> TranslatorMaxLength =
        CVarDef.Create("translator.max_length", 300, CVar.SERVER | CVar.REPLICATED);

    // client: translate other players' lines
    public static readonly CVarDef<bool> TranslatorAuto =
        CVarDef.Create("translator.auto", true, CVar.CLIENT | CVar.REPLICATED | CVar.ARCHIVE);

    // client: picked language, same as loc.culture_name
    public static readonly CVarDef<string> PlayerLanguage =
        CVarDef.Create("loc.player_language", "en-US", CVar.CLIENT | CVar.REPLICATED | CVar.ARCHIVE);

    // client: language picker was answered
    public static readonly CVarDef<bool> LanguagePicked =
        CVarDef.Create("loc.language_picked", false, CVar.CLIENTONLY | CVar.ARCHIVE);
}
