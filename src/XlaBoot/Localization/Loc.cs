using System;
using System.Globalization;
using System.Linq;

namespace XlaBoot.Localization;

/// <summary>
/// Picks the launcher's UI language. The text itself lives in Strings.resx (English, the fallback for any
/// missing key) and its Strings.{fr,de,ja}.resx translations; Strings is generated from it at build time.
///
/// Only the UI culture changes. Number and date formatting keep the device's own culture, and everything
/// written to logs or files stays English so bug reports read the same whatever the player's language.
/// </summary>
public static class Loc
{
    /// <summary>The setting values, in dropdown order. "system" follows the phone.</summary>
    public static readonly string[] Options = { "system", "en", "fr", "de", "ja" };

    private static readonly string[] Translated = { "fr", "de", "ja" };

    /// <summary>The phone's language as a two-letter code, set by the platform before the UI starts.</summary>
    public static string? SystemLanguage { get; set; }

    /// <summary>Raised after the culture changes, so the open screens can rebuild their text.</summary>
    public static event Action? Changed;

    /// <summary>The language the phone asks for, if the launcher has it; English otherwise.</summary>
    public static string SystemChoice
    {
        get
        {
            var lang = (SystemLanguage ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName).ToLowerInvariant();
            return Translated.Contains(lang) ? lang : "en";
        }
    }

    /// <summary>The language in use now: the setting, with "system" resolved.</summary>
    public static string Current
    {
        get
        {
            var setting = AppHost.Get("launcher_language", "system");
            return setting is "en" or "fr" or "de" or "ja" ? setting : SystemChoice;
        }
    }

    /// <summary>Sets the UI culture from the setting. Call before the first view is built and after it changes.</summary>
    public static void Apply()
    {
        var culture = CultureInfo.GetCultureInfo(Current);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Strings.Culture = culture;
    }

    /// <summary>Applies a new setting value and lets the open screens redraw.</summary>
    public static void Select(string option)
    {
        AppHost.Set("launcher_language", option);
        Apply();
        Changed?.Invoke();
    }

    /// <summary>A translated format string filled in, e.g. F(Strings.Status_Removed, label).</summary>
    public static string F(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    /// <summary>The game language that matches the phone, for a first install. English when it is none of the four.</summary>
    public static string GameLanguageForSystem() => SystemChoice switch
    {
        "fr" => "French",
        "de" => "German",
        "ja" => "Japanese",
        _ => "English",
    };
}
