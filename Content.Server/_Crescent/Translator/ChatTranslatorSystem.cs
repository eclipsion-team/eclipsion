using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Shared._Crescent.Translator;
using Content.Shared.CCVar;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._Crescent.Translator;

// sends chat and ahelp text to each receiver in their own language
public sealed class ChatTranslatorSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly INetConfigurationManager _netCfg = default!;
    [Dependency] private readonly ITaskManager _task = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ISharedPlayerManager _player = default!;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private const int CacheSize = 4000;
    private const int BatchSize = 50;
    private const int MaxInFlight = 4;
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan QuotaPause = TimeSpan.FromHours(1);

    private readonly Dictionary<(string Target, string Text), string?> _cache = new();
    // asked for but not answered yet, queued in _outbox or in flight
    private readonly HashSet<(string Target, string Text)> _waiting = new();
    private readonly Dictionary<string, List<string>> _outbox = new();
    private readonly Dictionary<NetUserId, Queue<TimeSpan>> _asked = new();
    private readonly Dictionary<INetChannel, Queue<Pending>> _queues = new();

    private int _inFlight;
    private TimeSpan _primaryOffUntil;
    private int _untranslated;

    private ISawmill _sawmill = default!;

    private sealed class Pending
    {
        public (string Target, string Text)? Key;
        public TimeSpan Deadline;
        public Action<string?> Send = default!;
    }

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("translator");
    }

    public bool Active => _cfg.GetCVar(CCVars.TranslatorEnabled)
                          && (_cfg.GetCVar(CCVars.TranslatorApiKey) != string.Empty
                              || _cfg.GetCVar(CCVars.TranslatorUrl) != string.Empty
                              || _cfg.GetCVar(CCVars.TranslatorFallbackUrl) != string.Empty);

    public string LanguageOf(INetChannel channel)
    {
        return ChatTranslatorLanguages.Short(_netCfg.GetClientCVar(channel, CCVars.PlayerLanguage));
    }

    // text sent while this is open is not translated (garbled speech)
    public UntranslatedScope Untranslated()
    {
        _untranslated++;
        return new UntranslatedScope(this);
    }

    public readonly struct UntranslatedScope : IDisposable
    {
        private readonly ChatTranslatorSystem _owner;

        public UntranslatedScope(ChatTranslatorSystem owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            _owner._untranslated--;
        }
    }

    // send gets the translation or null for the original, maybe a bit later
    // the author always gets their own text back
    public void Deliver(string text, NetUserId? author, EntityUid? sender, IEnumerable<INetChannel> receivers,
        Action<INetChannel, string?> send, Action<List<INetChannel>>? sendMany = null)
    {
        // whispers come in with no author, so they never hit the per minute limit
        if (author == null && sender is { Valid: true } && TryComp<ActorComponent>(sender, out var speaker))
            author = speaker.PlayerSession.UserId;

        string? plain = null;
        string? source = null;
        if (_untranslated == 0 && Active)
        {
            plain = FormattedMessage.RemoveMarkupPermissive(text).Trim();
            if (plain.Length > 0 && plain.Length <= _cfg.GetCVar(CCVars.TranslatorMaxLength))
                source = ChatTranslatorLanguages.Guess(plain);
        }

        List<INetChannel>? direct = null;
        var asked = false;
        var deadline = _timing.RealTime + GiveUpAfter;

        foreach (var channel in receivers)
        {
            (string, string)? key = null;
            if (source != null
                && channel.UserId != author
                && LanguageOf(channel) is var target and ("en" or "ru")
                && target != source
                && _netCfg.GetClientCVar(channel, CCVars.TranslatorAuto)
                && !IsSender(channel, sender))
            {
                var k = (target, plain!);
                if (_cache.ContainsKey(k) || _waiting.Contains(k))
                {
                    key = k;
                }
                else if (author == null || asked || CanAsk(author.Value))
                {
                    asked = true;
                    key = k;
                    Ask(k);
                }
            }

            if (key == null && !_queues.ContainsKey(channel))
            {
                direct ??= new List<INetChannel>();
                direct.Add(channel);
                continue;
            }

            if (!_queues.TryGetValue(channel, out var queue))
                _queues[channel] = queue = new Queue<Pending>();

            var c = channel;
            queue.Enqueue(new Pending { Key = key, Deadline = deadline, Send = translated => send(c, translated) });
        }

        if (direct != null)
        {
            if (sendMany != null)
                sendMany(direct);
            else
                foreach (var channel in direct)
                    send(channel, null);
        }

        if (_queues.Count > 0)
            Flush();
    }

    private bool IsSender(INetChannel channel, EntityUid? sender)
    {
        return sender is { Valid: true }
               && _player.TryGetSessionByChannel(channel, out var session)
               && session.AttachedEntity == sender;
    }

    private bool CanAsk(NetUserId user)
    {
        if (!_asked.TryGetValue(user, out var times))
            _asked[user] = times = new Queue<TimeSpan>();

        var now = _timing.RealTime;
        while (times.Count > 0 && now - times.Peek() > TimeSpan.FromMinutes(1))
            times.Dequeue();

        if (times.Count >= _cfg.GetCVar(CCVars.TranslatorPerMinute))
            return false;

        times.Enqueue(now);
        return true;
    }

    private void Ask((string Target, string Text) key)
    {
        _waiting.Add(key);
        if (!_outbox.TryGetValue(key.Target, out var list))
            _outbox[key.Target] = list = new List<string>();
        list.Add(key.Text);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_outbox.Count > 0)
            SendBatches();

        if (_queues.Count > 0)
            Flush();
    }

    private void Flush()
    {
        var now = _timing.RealTime;
        List<INetChannel>? done = null;
        foreach (var (channel, queue) in _queues)
        {
            if (!channel.IsConnected)
            {
                queue.Clear();
            }

            while (queue.TryPeek(out var next))
            {
                string? translated = null;
                if (next.Key is { } key)
                {
                    if (_cache.TryGetValue(key, out var result))
                        translated = result;
                    else if (now < next.Deadline)
                        break;
                }

                queue.Dequeue();
                try
                {
                    next.Send(translated);
                }
                catch (Exception e)
                {
                    _sawmill.Error($"sending translated text failed: {e}");
                }
            }

            if (queue.Count == 0)
                (done ??= new List<INetChannel>()).Add(channel);
        }

        if (done == null)
            return;

        foreach (var channel in done)
            _queues.Remove(channel);
    }

    private void SendBatches()
    {
        var settings = new Settings(
            _cfg.GetCVar(CCVars.TranslatorProvider).ToLowerInvariant(),
            _cfg.GetCVar(CCVars.TranslatorUrl),
            _cfg.GetCVar(CCVars.TranslatorApiKey),
            _cfg.GetCVar(CCVars.TranslatorFallbackUrl),
            _cfg.GetCVar(CCVars.TranslatorFallbackApiKey),
            _timing.RealTime < _primaryOffUntil);

        foreach (var target in _outbox.Keys.ToArray())
        {
            var list = _outbox[target];
            while (list.Count > 0 && _inFlight < MaxInFlight)
            {
                var take = Math.Min(BatchSize, list.Count);
                var texts = list.GetRange(0, take).ToArray();
                list.RemoveRange(0, take);
                _inFlight++;
                _ = RunBatch(target, texts, settings);
            }

            if (list.Count == 0)
                _outbox.Remove(target);
        }
    }

    private sealed record Settings(string Provider, string Url, string ApiKey, string FallbackUrl, string FallbackKey, bool PrimaryOff);

    private async Task RunBatch(string target, string[] texts, Settings settings)
    {
        string?[]? results = null;
        var quotaHit = false;

        if (!settings.PrimaryOff)
        {
            try
            {
                if (settings.Provider == "deepl")
                    (results, quotaHit) = await DeepL(settings.Url, settings.ApiKey, texts, target).ConfigureAwait(false);
                else
                    results = await Libre(settings.Url, settings.ApiKey, texts, target).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _sawmill.Warning($"{settings.Provider} failed: {e.Message}");
            }
        }

        if (results == null && settings.FallbackUrl != string.Empty)
        {
            try
            {
                results = await Libre(settings.FallbackUrl, settings.FallbackKey, texts, target).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _sawmill.Warning($"fallback failed: {e.Message}");
            }
        }

        _task.RunOnMainThread(() => FinishBatch(target, texts, results, quotaHit));
    }

    private void FinishBatch(string target, string[] texts, string?[]? results, bool quotaHit)
    {
        _inFlight--;

        if (quotaHit && _timing.RealTime >= _primaryOffUntil)
        {
            _primaryOffUntil = _timing.RealTime + QuotaPause;
            _sawmill.Warning("DeepL quota is used up, using only the fallback for an hour");
        }

        if (_cache.Count + texts.Length > CacheSize)
            _cache.Clear();

        for (var i = 0; i < texts.Length; i++)
        {
            var key = (target, texts[i]);
            _waiting.Remove(key);

            var result = results != null && i < results.Length ? results[i]?.Trim() : null;
            if (string.IsNullOrEmpty(result) || string.Equals(result, texts[i], StringComparison.OrdinalIgnoreCase))
                result = null;

            _cache[key] = result;
        }

        Flush();
    }

    private static async Task<(string?[]? Results, bool QuotaHit)> DeepL(string url, string apiKey, string[] texts, string target)
    {
        if (apiKey == string.Empty)
            return (null, false);

        var root = url != string.Empty
            ? url
            : apiKey.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";

        using var request = new HttpRequestMessage(HttpMethod.Post, root.TrimEnd('/') + "/v2/translate");
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");
        request.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["text"] = texts,
            ["target_lang"] = target == "en" ? "EN-US" : "RU",
        });

        using var response = await Http.SendAsync(request).ConfigureAwait(false);
        // 456: monthly character quota used up
        if ((int) response.StatusCode == 456)
            return (null, true);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"answered {(int) response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        var results = new string?[texts.Length];
        var i = 0;
        foreach (var item in doc.RootElement.GetProperty("translations").EnumerateArray())
        {
            if (i >= results.Length)
                break;

            var detected = item.TryGetProperty("detected_source_language", out var d) ? d.GetString()?.ToLowerInvariant() : null;
            results[i++] = detected != null && detected.StartsWith(target) ? null : item.GetProperty("text").GetString();
        }

        return (results, false);
    }

    private static async Task<string?[]?> Libre(string url, string apiKey, string[] texts, string target)
    {
        if (url == string.Empty)
            return null;

        var payload = new Dictionary<string, object>
        {
            ["q"] = texts,
            ["source"] = "auto",
            ["target"] = target,
            ["format"] = "text",
        };
        if (apiKey != string.Empty)
            payload["api_key"] = apiKey;

        using var response = await Http.PostAsJsonAsync(url.TrimEnd('/') + "/translate", payload).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"answered {(int) response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

        var translated = doc.RootElement.GetProperty("translatedText");
        if (translated.ValueKind == JsonValueKind.String)
            return new[] { translated.GetString() };

        return translated.EnumerateArray().Select(x => x.GetString()).ToArray();
    }

    // swaps the original text inside a wrapped line
    public static string SwapInWrapped(string wrapped, string original, string translated)
    {
        var escapedTranslation = FormattedMessage.EscapeText(translated);
        var escapedOriginal = FormattedMessage.EscapeText(original);

        if (escapedOriginal.Length > 0 && wrapped.Contains(escapedOriginal))
            return wrapped.Replace(escapedOriginal, escapedTranslation);

        if (original.Length > 0 && wrapped.Contains(original))
            return wrapped.Replace(original, escapedTranslation);

        // couldn't find it, add the translation at the end
        return $"{wrapped} [italic]({escapedTranslation})[/italic]";
    }
}
