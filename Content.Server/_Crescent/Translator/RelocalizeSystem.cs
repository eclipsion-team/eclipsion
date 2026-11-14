using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Linguini.Syntax.Ast;
using Linguini.Syntax.Parser;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Translator;

// finds which en-US loc string made a text and writes it again in the player's language
public sealed class RelocalizeSystem : EntitySystem
{
    [Dependency] private readonly ILocalizationManager _loc = default!;
    [Dependency] private readonly IResourceManager _res = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;

    private const string Source = "en-US";
    private const int MaxLength = 400;

    // functions that give an entity name
    private static readonly HashSet<string> NameFuncs = new() { "THE", "INDEFINITE", "CAPITALIZE", "PROPER" };
    private static readonly HashSet<string> NumberFuncs = new() { "TOSTRING", "NATURALFIXED", "NUMBER" };
    // these cast their argument so a wrong type throws inside the engine
    private static readonly HashSet<string> NeedNumber = new()
        { "PRESSURE", "POWERWATTS", "POWERJOULES", "UNITS", "TOSTRING", "NATURALFIXED", "NATURALPERCENT", "NUMBER" };
    private static readonly HashSet<string> NeedString = new() { "MAKEPLURAL", "FORMATSIGNLANGUAGE" };

    private static readonly Regex NumberRx = new(@"^-?[\d,]*\.?\d+$");

    // 0 nothing, 1 building in the background, 2 ready, 3 broke
    private volatile int _state;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Dictionary<string, List<Entry>> _byPrefix = new();
    private readonly Dictionary<string, List<Entry>> _bySuffix = new();
    private readonly List<Entry> _middle = new();
    private readonly Dictionary<string, Pattern> _terms = new();

    // culture -> key -> what the message needs from its variables
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, VarUse>>> _targets = new();
    private readonly Dictionary<string, Dictionary<string, string>> _names = new();
    private readonly Dictionary<(string, string), string?> _cache = new();

    private enum VarUse { Any, Number, Text }

    private enum Hole { Raw, Name, Number, Percent, Skip }

    private sealed class Entry
    {
        public string Key = "";
        public List<object> Parts = new(); // string or (Hole, var)
        public int Letters;
        public string Longest = "";
        public Regex? Rx;
        public List<(Hole, string)> Groups = new();
    }

    public bool Ready => _state == 2;

    public bool TryRelocalize(string text, CultureInfo culture, out string result)
    {
        result = text;
        if (culture.Name == Source || string.IsNullOrWhiteSpace(text) || text.Length > MaxLength)
            return false;

        // takes a second or two so do it in the background, text stays english until then
        if (_state == 0)
        {
            _state = 1;
            Task.Run(() =>
            {
                try
                {
                    BuildAll();
                    _state = 2;
                }
                catch (Exception e)
                {
                    Log.Error($"relocalize index failed: {e}");
                    _state = 3;
                }
            });
        }

        if (_state != 2)
            return false;

        if (_cache.TryGetValue((culture.Name, text), out var cached))
        {
            if (cached == null)
                return false;
            result = cached;
            return true;
        }

        if (_cache.Count > 4000)
            _cache.Clear();

        string? done = null;
        try
        {
            done = Translate(text, culture, 0);
        }
        catch (RegexMatchTimeoutException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"relocalize failed for '{text}': {e}");
        }

        _cache[(culture.Name, text)] = done;
        if (done == null)
            return false;

        result = done;
        return true;
    }

    private string? Translate(string text, CultureInfo culture, int depth)
    {
        if (!_loc.HasCulture(culture) || !_targets.TryGetValue(culture.Name, out var targets))
            return null;

        Entry? best = null;
        Match? bestMatch = null;
        foreach (var entry in Candidates(text))
        {
            if (best != null && entry.Letters <= best.Letters)
                continue;
            if (!targets.ContainsKey(entry.Key))
                continue;

            entry.Rx ??= MakeRegex(entry);
            var m = entry.Rx.Match(text);
            if (!m.Success)
                continue;

            best = entry;
            bestMatch = m;
        }

        if (best == null || bestMatch == null)
            return null;

        var values = new Dictionary<string, string>();
        for (var i = 0; i < best.Groups.Count; i++)
        {
            var (hole, name) = best.Groups[i];
            var value = bestMatch.Groups["g" + i].Value;
            switch (hole)
            {
                case Hole.Name:
                    value = StripArticle(value);
                    break;
                case Hole.Number:
                    value = value.Trim();
                    break;
                case Hole.Percent:
                    value = value.Trim().TrimEnd('%');
                    if (double.TryParse(value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        value = (pct / 100).ToString(CultureInfo.InvariantCulture);
                    break;
            }

            // raw captures win over the ones read out of a function
            if (!values.ContainsKey(name) || hole == Hole.Raw)
                values[name] = value;
        }

        var args = new List<(string, object)>();
        foreach (var (name, use) in targets[best.Key])
        {
            if (!values.TryGetValue(name, out var value))
                return null;

            var isNumber = NumberRx.IsMatch(value);
            if (use == VarUse.Number)
            {
                if (!isNumber)
                    return null;
                args.Add((name, double.Parse(value.Replace(",", ""), CultureInfo.InvariantCulture)));
                continue;
            }

            if (use == VarUse.Any && isNumber)
            {
                args.Add((name, double.Parse(value.Replace(",", ""), CultureInfo.InvariantCulture)));
                continue;
            }

            args.Add((name, TranslateValue(value, culture, depth)));
        }

        var old = _loc.DefaultCulture;
        try
        {
            _loc.DefaultCulture = culture;
            return _loc.TryGetString(best.Key, out var res, args.ToArray()) ? res : null;
        }
        finally
        {
            if (old != null)
                _loc.DefaultCulture = old;
        }
    }

    private string TranslateValue(string value, CultureInfo culture, int depth)
    {
        if (_names.TryGetValue(culture.Name, out var names) && names.TryGetValue(value, out var name))
            return name;

        if (depth < 2 && Translate(value, culture, depth + 1) is { } inner)
            return inner;

        return value;
    }

    private static string StripArticle(string value)
    {
        foreach (var a in new[] { "the ", "a ", "an " })
        {
            if (value.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                return value.Substring(a.Length);
        }
        return value;
    }

    private IEnumerable<Entry> Candidates(string text)
    {
        for (var i = 1; i <= Math.Min(6, text.Length); i++)
        {
            if (_byPrefix.TryGetValue(text.Substring(0, i), out var list))
            {
                foreach (var e in list)
                    yield return e;
            }

            if (_bySuffix.TryGetValue(text.Substring(text.Length - i), out list))
            {
                foreach (var e in list)
                    yield return e;
            }
        }

        foreach (var e in _middle)
        {
            if (text.Contains(e.Longest, StringComparison.Ordinal))
                yield return e;
        }
    }

    private static Regex MakeRegex(Entry entry)
    {
        var sb = new StringBuilder("^");
        foreach (var part in entry.Parts)
        {
            if (part is string s)
            {
                sb.Append(Regex.Escape(s));
                continue;
            }

            var (hole, name) = ((Hole, string)) part;
            if (hole == Hole.Skip)
            {
                sb.Append("(?:.*?)");
                continue;
            }

            sb.Append($"(?<g{entry.Groups.Count}>.+?)");
            entry.Groups.Add((hole, name));
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
    }

    private void BuildAll()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Build();

        foreach (var lang in Content.Shared.Localizations.ContentLocalizationManager.Languages)
        {
            if (lang == Source)
                continue;

            var messages = new Dictionary<string, Pattern>();
            foreach (var resource in Parse(lang))
            {
                foreach (var item in resource.Entries)
                {
                    if (item is AstMessage { Value: { } value } msg)
                        messages.TryAdd(msg.Id.Name.ToString(), value);
                }
            }

            _targets[lang] = Targets(messages);
            _names[lang] = Names(messages);
        }

        Log.Info($"relocalize index: {_entries.Count} messages in {sw.ElapsedMilliseconds} ms");
    }

    private void Build()
    {
        var messages = new Dictionary<string, Pattern>();
        foreach (var resource in Parse(Source))
        {
            foreach (var item in resource.Entries)
            {
                if (item is AstMessage { Value: { } value } msg)
                    messages.TryAdd(msg.Id.Name.ToString(), value);
                else if (item is AstTerm term)
                    _terms.TryAdd(term.Id.Name.ToString(), term.Value);
            }
        }

        foreach (var (key, pattern) in messages)
        {
            // entity names are handled by the name table
            if (key.StartsWith("ent-") || key.StartsWith("zzzz-"))
                continue;

            var entry = new Entry { Key = key };
            Flatten(pattern, entry.Parts, 0);
            Merge(entry.Parts);

            foreach (var p in entry.Parts)
            {
                if (p is not string s)
                    continue;
                entry.Letters += s.Count(char.IsLetterOrDigit);
                if (s.Length > entry.Longest.Length)
                    entry.Longest = s;
            }

            if (entry.Letters < 4)
                continue;

            _entries[key] = entry;
            if (entry.Parts[0] is string first)
                Add(_byPrefix, first.Substring(0, Math.Min(6, first.Length)), entry);
            else if (entry.Parts[^1] is string last)
                Add(_bySuffix, last.Substring(Math.Max(0, last.Length - 6)), entry);
            else
                _middle.Add(entry);
        }
    }

    private static void Add(Dictionary<string, List<Entry>> dict, string key, Entry entry)
    {
        if (!dict.TryGetValue(key, out var list))
            dict[key] = list = new List<Entry>();
        list.Add(entry);
    }

    private static void Merge(List<object> parts)
    {
        for (var i = parts.Count - 1; i > 0; i--)
        {
            if (parts[i] is string a && parts[i - 1] is string b)
            {
                parts[i - 1] = b + a;
                parts.RemoveAt(i);
            }
            else if (parts[i] is (Hole.Skip, _) && parts[i - 1] is (Hole.Skip, _))
            {
                parts.RemoveAt(i);
            }
        }

        parts.RemoveAll(p => p is "");
    }

    private void Flatten(Pattern pattern, List<object> parts, int depth)
    {
        foreach (var element in pattern.Elements)
        {
            if (element is TextLiteral text)
            {
                parts.Add(text.Value.ToString());
                continue;
            }

            if (element is Placeable placeable)
                FlattenExpr(placeable.Expression, parts, depth);
        }
    }

    private void FlattenExpr(IExpression expr, List<object> parts, int depth)
    {
        switch (expr)
        {
            case TextLiteral lit:
                parts.Add(lit.Value.ToString());
                return;
            case NumberLiteral num:
                parts.Add(num.Value.ToString());
                return;
            case VariableReference v:
                parts.Add((Hole.Raw, v.Id.Name.ToString()));
                return;
            case TermReference t when depth < 3 && t.Attribute == null && _terms.TryGetValue(t.Id.Name.ToString(), out var term):
                Flatten(term, parts, depth + 1);
                return;
            case FunctionReference f:
                var func = f.Id.Name.ToString();
                if (f.Arguments.PositionalArgs.Count > 0 && FirstVar(f.Arguments.PositionalArgs[0]) is { } name)
                {
                    if (NameFuncs.Contains(func))
                    {
                        parts.Add((Hole.Name, name));
                        return;
                    }

                    if (NumberFuncs.Contains(func))
                    {
                        parts.Add((Hole.Number, name));
                        return;
                    }

                    if (func == "NATURALPERCENT")
                    {
                        parts.Add((Hole.Percent, name));
                        return;
                    }
                }

                parts.Add((Hole.Skip, ""));
                return;
            case Placeable inner:
                FlattenExpr(inner.Expression, parts, depth);
                return;
            default:
                parts.Add((Hole.Skip, ""));
                return;
        }
    }

    private static string? FirstVar(IInlineExpression expr)
    {
        return expr switch
        {
            VariableReference v => v.Id.Name.ToString(),
            FunctionReference f when NameFuncs.Contains(f.Id.Name.ToString()) && f.Arguments.PositionalArgs.Count > 0
                => FirstVar(f.Arguments.PositionalArgs[0]),
            _ => null,
        };
    }

    private Dictionary<string, Dictionary<string, VarUse>> Targets(Dictionary<string, Pattern> messages)
    {
        var result = new Dictionary<string, Dictionary<string, VarUse>>();
        foreach (var (key, value) in messages)
        {
            if (!_entries.ContainsKey(key))
                continue;

            var vars = new Dictionary<string, VarUse>();
            if (Uses(value, vars, null))
                result[key] = vars;
        }

        return result;
    }

    // false when the pattern uses something we can't fill in
    private bool Uses(Pattern pattern, Dictionary<string, VarUse> vars, string? func)
    {
        foreach (var element in pattern.Elements)
        {
            if (element is Placeable p && !UsesExpr(p.Expression, vars, func))
                return false;
        }
        return true;
    }

    private bool UsesExpr(IExpression expr, Dictionary<string, VarUse> vars, string? func)
    {
        switch (expr)
        {
            case VariableReference v:
                var use = func == null ? VarUse.Any
                    : NeedNumber.Contains(func) ? VarUse.Number
                    : NeedString.Contains(func) ? VarUse.Text
                    : VarUse.Any;
                var name = v.Id.Name.ToString();
                if (vars.TryGetValue(name, out var had) && had != VarUse.Any && use != VarUse.Any && had != use)
                    return false;
                if (!vars.ContainsKey(name) || use != VarUse.Any)
                    vars[name] = use;
                return true;
            case FunctionReference f:
                var fname = f.Id.Name.ToString();
                // MANY wants text first and a number second
                for (var i = 0; i < f.Arguments.PositionalArgs.Count; i++)
                {
                    var argFunc = fname == "MANY" ? (i == 0 ? "MAKEPLURAL" : "NUMBER") : fname;
                    if (!UsesExpr(f.Arguments.PositionalArgs[i], vars, argFunc))
                        return false;
                }
                return true;
            case SelectExpression s:
                if (!UsesExpr(s.Selector, vars, func))
                    return false;
                foreach (var variant in s.Variants)
                {
                    if (!Uses(variant.Value, vars, null))
                        return false;
                }
                return true;
            case Placeable inner:
                return UsesExpr(inner.Expression, vars, func);
            default:
                return true;
        }
    }

    // english name -> translated name
    private Dictionary<string, string> Names(Dictionary<string, Pattern> messages)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proto in _proto.EnumeratePrototypes<EntityPrototype>())
        {
            if (proto.Abstract)
                continue;

            var en = proto.Name;
            if (string.IsNullOrWhiteSpace(en) || result.ContainsKey(en))
                continue;

            foreach (var parent in _proto.EnumerateParents<EntityPrototype>(proto.ID, true))
            {
                var id = parent?.CustomLocalizationID ?? $"ent-{proto.ID}";
                if (!messages.TryGetValue(id, out var pattern))
                    continue;

                if (Plain(pattern, messages, 0) is { } text && !string.IsNullOrWhiteSpace(text))
                    result[en] = text;
                break;
            }
        }

        return result;
    }

    private static string? Plain(Pattern pattern, Dictionary<string, Pattern> messages, int depth)
    {
        var sb = new StringBuilder();
        foreach (var element in pattern.Elements)
        {
            switch (element)
            {
                case TextLiteral text:
                    sb.Append(text.Value.Span);
                    break;
                case Placeable { Expression: MessageReference { Attribute: null } m } when depth < 4
                    && messages.TryGetValue(m.Id.Name.ToString(), out var inner)
                    && Plain(inner, messages, depth + 1) is { } innerText:
                    sb.Append(innerText);
                    break;
                default:
                    return null;
            }
        }

        return sb.ToString().Trim();
    }

    private IEnumerable<Resource> Parse(string culture)
    {
        var root = new ResPath($"/Locale/{culture}/");
        foreach (var path in _res.ContentFindFiles(root))
        {
            if (!path.Filename.EndsWith(".ftl", StringComparison.OrdinalIgnoreCase))
                continue;

            string contents;
            using (var stream = _res.ContentFileRead(path))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                contents = reader.ReadToEnd();
            }

            yield return new LinguiniParser(contents).Parse();
        }
    }
}
