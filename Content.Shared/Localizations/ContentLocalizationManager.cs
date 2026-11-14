using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Robust.Shared;
using Robust.Shared.Configuration;
using Robust.Shared.Utility;

namespace Content.Shared.Localizations
{
    public sealed class ContentLocalizationManager
    {
        [Dependency] private readonly ILocalizationManager _loc = default!;
        [Dependency] private readonly IConfigurationManager _cfg = default!;

        // If you want to change your codebase's language, do it here.
        private const string Culture = "en-US";

        /// <summary>
        /// Custom format strings used for parsing and displaying minutes:seconds timespans.
        /// </summary>
        public static readonly string[] TimeSpanMinutesFormats = new[]
        {
            @"m\:ss",
            @"mm\:ss",
            @"%m",
            @"mm"
        };

        // Eclipsion - languages a player can pick, first one is the fallback for missing strings
        public static readonly string[] Languages = { "en-US", "ru-RU" };

        public void Initialize()
        {
            var fallback = new CultureInfo(Culture);
            LoadWithFunctions(fallback);

            var picked = PickedCulture();
            if (!picked.Equals(fallback))
                LoadWithFunctions(picked);

            _loc.DefaultCulture = picked;
            _loc.SetFallbackCluture(fallback);
        }

        // Eclipsion - server loads every language for RelocalizeSystem
        public void InitializeServer()
        {
            Initialize();
            foreach (var name in Languages)
            {
                var culture = new CultureInfo(name);
                if (!_loc.HasCulture(culture))
                    LoadWithFunctions(culture);
            }
        }

        private CultureInfo PickedCulture()
        {
            var name = _cfg.GetCVar(CVars.LocCultureName);
            return Languages.Contains(name) ? new CultureInfo(name) : new CultureInfo(Culture);
        }

        // windows already open keep their old text
        public bool SwitchLanguage(string name)
        {
            if (!Languages.Contains(name))
                return false;

            var culture = new CultureInfo(name);
            if (!_loc.HasCulture(culture))
                LoadWithFunctions(culture);

            _cfg.SetCVar(CVars.LocCultureName, name);
            if (_loc.DefaultCulture?.Name == name)
                return false;

            _loc.DefaultCulture = culture;
            // drops the cached entity names
            _loc.ReloadLocalizations();
            return true;
        }

        private void LoadWithFunctions(CultureInfo culture)
        {
            _loc.LoadCulture(culture);
            _loc.AddFunction(culture, "PRESSURE", FormatPressure);
            _loc.AddFunction(culture, "POWERWATTS", FormatPowerWatts);
            _loc.AddFunction(culture, "POWERJOULES", FormatPowerJoules);
            _loc.AddFunction(culture, "UNITS", FormatUnits);
            _loc.AddFunction(culture, "TOSTRING", args => FormatToString(culture, args));
            _loc.AddFunction(culture, "LOC", FormatLoc);
            _loc.AddFunction(culture, "NATURALFIXED", FormatNaturalFixed);
            _loc.AddFunction(culture, "NATURALPERCENT", FormatNaturalPercent);
            //HULLROT EDIT: needed for formatting sign language to only 1 word and 10 characters
            _loc.AddFunction(culture, "FORMATSIGNLANGUAGE", FormatSignLanguage);

            // Eclipsion - ru-RU files call these too
            _loc.AddFunction(culture, "MAKEPLURAL", FormatMakePlural);
            _loc.AddFunction(culture, "MANY", FormatMany);
        }

        private ILocValue FormatMany(LocArgs args)
        {
            var count = ((LocValueNumber) args.Args[1]).Value;

            if (Math.Abs(count - 1) < 0.0001f)
            {
                return (LocValueString) args.Args[0];
            }
            else
            {
                return (LocValueString) FormatMakePlural(args);
            }
        }

        private ILocValue FormatNaturalPercent(LocArgs args)
        {
            var number = ((LocValueNumber) args.Args[0]).Value * 100;
            var maxDecimals = (int)Math.Floor(((LocValueNumber) args.Args[1]).Value);
            var formatter = (NumberFormatInfo)NumberFormatInfo.GetInstance(CultureInfo.GetCultureInfo(Culture)).Clone();
            formatter.NumberDecimalDigits = maxDecimals;
            return new LocValueString(string.Format(formatter, "{0:N}", number).TrimEnd('0').TrimEnd(char.Parse(formatter.NumberDecimalSeparator)) + "%");
        }

        private ILocValue FormatNaturalFixed(LocArgs args)
        {
            var number = ((LocValueNumber) args.Args[0]).Value;
            var maxDecimals = (int)Math.Floor(((LocValueNumber) args.Args[1]).Value);
            var formatter = (NumberFormatInfo)NumberFormatInfo.GetInstance(CultureInfo.GetCultureInfo(Culture)).Clone();
            formatter.NumberDecimalDigits = maxDecimals;
            return new LocValueString(string.Format(formatter, "{0:N}", number).TrimEnd('0').TrimEnd(char.Parse(formatter.NumberDecimalSeparator)));
        }

        private static readonly Regex PluralEsRule = new("^.*(s|sh|ch|x|z)$");

        private ILocValue FormatMakePlural(LocArgs args)
        {
            var text = ((LocValueString) args.Args[0]).Value;
            var split = text.Split(" ", 1);
            var firstWord = split[0];
            if (PluralEsRule.IsMatch(firstWord))
            {
                if (split.Length == 1)
                    return new LocValueString($"{firstWord}es");
                else
                    return new LocValueString($"{firstWord}es {split[1]}");
            }
            else
            {
                if (split.Length == 1)
                    return new LocValueString($"{firstWord}s");
                else
                    return new LocValueString($"{firstWord}s {split[1]}");
            }
        }

        // TODO: allow fluent to take in lists of strings so this can be a format function like it should be.
        /// <summary>
        /// Formats a list as per english grammar rules.
        /// </summary>
        public static string FormatList(List<string> list)
        {
            return list.Count switch
            {
                <= 0 => string.Empty,
                1 => list[0],
                2 => $"{list[0]} and {list[1]}",
                _ => $"{string.Join(", ", list.GetRange(0, list.Count - 1))}, and {list[^1]}"
            };
        }

        /// <summary>
        /// Formats a list as per english grammar rules, but uses or instead of and.
        /// </summary>
        public static string FormatListToOr(List<string> list)
        {
            return list.Count switch
            {
                <= 0 => string.Empty,
                1 => list[0],
                2 => $"{list[0]} or {list[1]}",
                _ => $"{string.Join(" or ", list)}"
            };
        }

        /// <summary>
        /// Formats a direction struct as a human-readable string.
        /// </summary>
        public static string FormatDirection(Direction dir)
        {
            return Loc.GetString($"zzzz-fmt-direction-{dir.ToString()}");
        }

        private static ILocValue FormatLoc(LocArgs args)
        {
            var id = ((LocValueString) args.Args[0]).Value;

            return new LocValueString(Loc.GetString(id, args.Options.Select(x => (x.Key, x.Value.Value!)).ToArray()));
        }

        private static ILocValue FormatToString(CultureInfo culture, LocArgs args)
        {
            var arg = args.Args[0];
            var fmt = ((LocValueString) args.Args[1]).Value;

            var obj = arg.Value;
            if (obj is IFormattable formattable)
                return new LocValueString(formattable.ToString(fmt, culture));

            return new LocValueString(obj?.ToString() ?? "");
        }

        private static ILocValue FormatUnitsGeneric(LocArgs args, string mode)
        {
            const int maxPlaces = 5; // Matches amount in _lib.ftl
            var pressure = ((LocValueNumber) args.Args[0]).Value;

            var places = 0;
            while (pressure > 1000 && places < maxPlaces)
            {
                pressure /= 1000;
                places += 1;
            }

            return new LocValueString(Loc.GetString(mode, ("divided", pressure), ("places", places)));
        }


        /// <summary>
        ///  //HULLROT EDIT: needed for formatting sign language to only 1 word and 10 characters
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private static ILocValue FormatSignLanguage(LocArgs args)
        {
            if (args.Args.Count < 1 || args.Args[0] is not LocValueString locStr)
                return new LocValueString(string.Empty);

            // Strip any markup first. Otherwise a message that already contains markup
            // (e.g. a font-amplified command role's "[font size=x]...[/font]") would get
            // split mid-tag, leaking a partial "[font" into the chat wrap and throwing in
            // the markup parser - which makes the whole sign message fail to render.
            var stripped = FormattedMessage.RemoveMarkupPermissive(locStr.Value);

            // Grab the first word only. RemoveEmptyEntries so a leading separator doesn't yield "".
            var separators = new[] { ' ', ',', '.', '-', '_' };
            var newMessage = stripped
                .Split(separators, System.StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;

            if (newMessage.Length > 10)
                newMessage = newMessage.Substring(0, 10);

            return new LocValueString(newMessage);
        }

        private static ILocValue FormatPressure(LocArgs args)
        {
            return FormatUnitsGeneric(args, "zzzz-fmt-pressure");
        }

        private static ILocValue FormatPowerWatts(LocArgs args)
        {
            return FormatUnitsGeneric(args, "zzzz-fmt-power-watts");
        }

        private static ILocValue FormatPowerJoules(LocArgs args)
        {
            return FormatUnitsGeneric(args, "zzzz-fmt-power-joules");
        }

        private static ILocValue FormatUnits(LocArgs args)
        {
            if (!Units.Types.TryGetValue(((LocValueString) args.Args[0]).Value, out var ut))
                throw new ArgumentException($"Unknown unit type {((LocValueString) args.Args[0]).Value}");

            var fmtstr = ((LocValueString) args.Args[1]).Value;

            double max = Double.NegativeInfinity;
            var iargs = new double[args.Args.Count - 1];
            for (var i = 2; i < args.Args.Count; i++)
            {
                var n = ((LocValueNumber) args.Args[i]).Value;
                if (n > max)
                    max = n;

                iargs[i - 2] = n;
            }

            if (!ut.TryGetUnit(max, out var mu))
                throw new ArgumentException("Unit out of range for type");

            var fargs = new object[iargs.Length];

            for (var i = 0; i < iargs.Length; i++)
                fargs[i] = iargs[i] * mu.Factor;

            fargs[^1] = Loc.GetString($"units-{mu.Unit.ToLower()}");

            // Before anyone complains about "{"+"${...}", at least it's better than MS's approach...
            // https://docs.microsoft.com/en-us/dotnet/standard/base-types/composite-formatting#escaping-braces
            //
            // Note that the closing brace isn't replaced so that format specifiers can be applied.
            var res = String.Format(
                fmtstr.Replace("{UNIT", "{" + $"{fargs.Length - 1}"),
                fargs
            );

            return new LocValueString(res);
        }
    }
}
