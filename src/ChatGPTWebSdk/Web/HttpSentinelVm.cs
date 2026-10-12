using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

// Decode the challenge as data. No downloaded JavaScript, native code or browser is executed.
internal sealed class HttpSentinelVm
{
    private sealed record Expression(string Kind, string? Text = null, params Expression[] Children)
    {
        internal int Depth { get; } = 1 + (Children.Length == 0 ? 0 : Children.Max(child => child.Depth));
        internal int Signals { get; } = Signal(Text) | Children.Aggregate(0, (value, child) => value | child.Signals);
        internal double? Numeric { get; } = NumericValue(Kind, Text, Children);
        public bool Contains(string text) => (Signals & Signal(text)) != 0;
    }
    private static int Signal(string? text) => text switch
    {
        "location" => 1, "cfIpLongitude" => 2, "maxTouchPoints" => 4, "history" => 8,
        "localStorage" => 16, "keys" => 32, "createElement" => 64, "random" => 128, "set" => 256, _ => 0
    };
    private static double? NumericValue(string kind, string? text, Expression[] children)
    {
        if (kind == "literal") return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : null;
        if (kind is "add" or "multiply" or "remove" && children.Length == 2 && children[0].Numeric is { } a && children[1].Numeric is { } b)
        {
            var value = kind == "add" ? a + b : kind == "multiply" ? a * b : a - b;
            return double.IsFinite(value) ? value : null;
        }
        return null;
    }

    private readonly Dictionary<string, string> _operations = new()
    {
        ["1"] = "xor", ["2"] = "set", ["3"] = "output", ["4"] = "output", ["5"] = "add",
        ["6"] = "access", ["7"] = "call", ["8"] = "copy", ["10"] = "window", ["11"] = "script",
        ["12"] = "map", ["13"] = "try", ["14"] = "parse", ["15"] = "stringify", ["17"] = "callset",
        ["18"] = "atob", ["19"] = "btoa", ["20"] = "ifequal", ["21"] = "ifdiff", ["22"] = "temp",
        ["23"] = "ifdefined", ["24"] = "bind", ["25"] = "undefined", ["26"] = "undefined",
        ["27"] = "remove", ["28"] = "undefined", ["29"] = "less", ["31"] = "increment",
        ["32"] = "decrement", ["33"] = "multiply", ["34"] = "move"
    };
    private readonly Dictionary<string, Expression> _registers = new();
    private readonly Dictionary<Expression, Dictionary<string, Expression>> _objects = new(ReferenceEqualityComparer.Instance);
    private readonly List<string> _strings = [];
    private Expression? _payload;
    private string? _stageKey;
    private string? _envelopeKey;
    private int _instructions;
    private int _nodes;
    private readonly CancellationToken _ct;
    private HttpSentinelVm(CancellationToken ct) => _ct = ct;

    internal static string Decode(string bytecode, string token, string ipInfo, string userAgent, Uri origin, CancellationToken ct = default)
    {
        try
        {
            var vm = new HttpSentinelVm(ct);
            vm.Run(Program(bytecode, token));
            var seen = new HashSet<string>(StringComparer.Ordinal) { bytecode };
            for (var stage = 0; stage < 3 && vm._stageKey is not null; stage++)
            {
                JsonArray? next = null;
                foreach (var candidate in vm._strings.OrderByDescending(s => s.Length))
                {
                    if (candidate.Length < 60 || !seen.Add(candidate)) continue;
                    try { next = Program(candidate, vm._stageKey); break; }
                    catch (Exception ex) when (ex is FormatException or JsonException) { }
                }
                if (next is null) break;
                vm.Run(next);
            }
            if (vm._payload is null || vm._envelopeKey is not { Length: > 0 } key || !vm._objects.TryGetValue(vm._payload, out var fields) || fields.Count is < 1 or > 128)
                throw Unsupported();
            var payload = new Dictionary<string, object>();
            foreach (var (name, expression) in fields)
            {
                ct.ThrowIfCancellationRequested();
                if (Supported(expression)) payload.Add(name, Value(expression, key, ipInfo, userAgent, origin));
            }
            if (payload.Count == 0) throw Unsupported();
            return Encode(Xor(JsonSerializer.Serialize(payload, JsonOptions), key));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException)
        { throw Unsupported(); }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static SdkException Unsupported() => new("The HTTP Sentinel challenge format has changed. Use Hybrid or an ExternalApi provider until it is supported.", "unsupported_http_sentinel_challenge");
    private static string Scalar(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node?.ToJsonString() ?? "null";
    internal static string Xor(string text, string key)
    {
        if (key.Length == 0 || key.Length > 512 * 1024 || text.Length > 512 * 1024) throw Unsupported();
        return string.Create(text.Length, (text, key), static (chars, state) =>
        { for (var i = 0; i < chars.Length; i++) chars[i] = (char)(state.text[i] ^ state.key[i % state.key.Length]); });
    }
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static JsonArray Program(string encoded, string key)
    {
        if (encoded.Length > 512 * 1024 || key.Length == 0) throw new FormatException();
        var decoded = Xor(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)), key);
        var result = JsonNode.Parse(decoded, documentOptions: new() { MaxDepth = 32 }) as JsonArray ?? throw new JsonException();
        if (result.Count is < 1 or > 4096 || result.Any(row => row is not JsonArray { Count: >= 1 and <= 32 })) throw new JsonException();
        return result;
    }
    private Expression Node(string kind, string? text = null, params Expression[] children)
    {
        if (++_nodes > 16384 || children.Any(child => child.Depth > 48)) throw Unsupported();
        return new(kind, text, children);
    }
    private Expression Read(string register) => _registers.TryGetValue(register, out var value) ? value : Node(_operations.GetValueOrDefault(register) ?? "unknown", register);
    private void Run(JsonArray program)
    {
        foreach (var node in program)
        {
            _ct.ThrowIfCancellationRequested();
            if (++_instructions > 8192) throw Unsupported();
            var row = (JsonArray)node!;
            var arguments = row.Skip(1).Select(Scalar).ToArray();
            var operation = _operations.GetValueOrDefault(Scalar(row[0])) ?? throw Unsupported();
            Apply(operation, arguments);
        }
    }
    private void Apply(string operation, string[] args)
    {
        if (args.Length == 0) throw Unsupported();
        var target = args[0];
        Expression Get(int index) => index < args.Length ? Read(args[index]) : throw Unsupported();
        switch (operation)
        {
            case "copy":
                if (args.Length < 2) throw Unsupported();
                if (_operations.TryGetValue(args[1], out var alias)) _operations[target] = alias;
                _registers[target] = Get(1);
                break;
            case "set":
                if (args.Length < 2 || args[1].Length > 512 * 1024) throw Unsupported();
                _registers[target] = Node("literal", args[1]);
                if (args[1].Length >= 60) _strings.Add(args[1]);
                break;
            case "access": case "bind":
                if (args.Length < 3) throw Unsupported();
                _registers[target] = Node("access", null, Get(1), Get(2));
                break;
            case "xor":
                if (args.Length < 2) throw Unsupported();
                var left = Get(0); var right = Get(1);
                if (left.Kind == "stringify") { _payload = left.Children[0]; _envelopeKey = Literal(right); }
                _registers[target] = Node("xor", null, left, right);
                break;
            case "btoa": case "atob": case "stringify":
                if (args.Length < 1) throw Unsupported();
                _registers[target] = Node(operation, null, Get(0));
                if (operation == "stringify" && _objects.ContainsKey(Get(0).Children[0])) _payload = Get(0).Children[0];
                break;
            case "add": case "multiply": case "remove":
                if (args.Length < 2) throw Unsupported();
                _registers[target] = Node(operation, null, Get(0), Get(1));
                break;
            case "callset":
                if (args.Length < 2) throw Unsupported();
                _registers[target] = Node("call", null, args.Skip(1).Select(Read).ToArray());
                break;
            case "call":
                if (Literal(Get(0)) == "set" || Get(0).Contains("set"))
                {
                    if (args.Length < 4) throw Unsupported();
                    var obj = Get(1); var name = Literal(Get(2)) ?? throw Unsupported();
                    if (!_objects.TryGetValue(obj, out var fields)) _objects.Add(obj, fields = new());
                    fields[name] = Get(3);
                }
                break;
            case "ifdefined":
                if (args.Length < 3) throw Unsupported();
                if (_operations.GetValueOrDefault(args[1]) == "set" && args.Length == 4)
                { _stageKey ??= args[3]; Apply("set", args[2..]); }
                break;
            case "try":
                if (args.Length < 2) throw Unsupported();
                _registers[target] = Node("try", null, args.Skip(1).Select(Read).ToArray());
                break;
            // These instructions describe optional browser probes; their values are never executed.
            case "ifequal": case "ifdiff": case "temp": case "parse": case "map": case "script":
            case "less": case "increment": case "decrement": case "move": case "output":
                break;
            default: throw Unsupported();
        }
    }
    private static string? Literal(Expression expression) => expression.Kind == "literal" ? expression.Text : null;
    private static readonly string[] BrowserValues = ["location", "cfIpLongitude", "maxTouchPoints", "history", "localStorage", "keys", "createElement", "random"];
    private static bool Supported(Expression expression) => BrowserValues.Any(expression.Contains) || Constant(expression, "0", out _);
    private static bool Number(Expression expression, out double number)
    {
        if (expression.Numeric is { } value) { number = value; return true; }
        number = 0; return false;
    }
    private static object Value(Expression expression, string key, string ipInfo, string userAgent, Uri origin)
    {
        string Protect(string value) => Encode(Xor(value, key));
        if (expression.Contains("location")) return Protect(origin.AbsoluteUri);
        if (expression.Contains("cfIpLongitude")) return !string.IsNullOrWhiteSpace(ipInfo) ? Protect(ipInfo) : throw Unsupported();
        if (expression.Contains("maxTouchPoints"))
        {
            var platform = userAgent.Contains("Windows", StringComparison.Ordinal) ? "Win32" : userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "MacIntel" : "Linux x86_64";
            return Protect(JsonSerializer.Serialize(new object[] { userAgent.Contains("Firefox/", StringComparison.Ordinal) ? "" : "Google Inc.", platform, 8, 0 }, JsonOptions));
        }
        if (expression.Contains("history")) return Protect(Random.Shared.Next(1, 6).ToString(CultureInfo.InvariantCulture));
        if (expression.Contains("localStorage") || expression.Contains("keys")) return Protect("oai/apps/hasDismissedTeamsNoAuthUpsell,oai/apps/lastSeenNoAuthTrialsBannerAt,oai-did,oai/apps/noAuthGoUpsellModalDismissed,oai/apps/hasDismissedBusinessFreeTrialUpsellModal,oai/apps/capExpiresAt,statsig.session_id.1792610830,oai/apps/hasSeenNoAuthImagegenNux,oai/apps/lastPageLoadDate,client-correlated-secret,statsig.stable_id.1792610830,oai/apps/debugSettings,oai/apps/hasDismissedPlusFreeTrialUpsellModal,oai/apps/tatertotInContextUpsellBannerV2,search.attributions-settings");
        if (expression.Contains("createElement")) return Protect("{\"x\":0,\"y\":1219,\"width\":37.8125,\"height\":30,\"top\":1219,\"right\":37.8125,\"bottom\":1249,\"left\":0}");
        if (Number(expression, out var number)) return Protect(number.ToString(number % 1 == 0 ? "0.0" : "R", CultureInfo.InvariantCulture));
        if (expression.Kind == "call" && expression.Contains("random")) return Random.Shared.NextDouble();
        if (Constant(expression, key, out var constant)) return constant;
        return Evaluate(expression);
    }
    private static bool Constant(Expression expression, string key, out string value)
    {
        var current = expression; var selfXor = false; var otherXor = false; var base64 = false;
        while (current.Kind is "btoa" or "xor")
        {
            if (current.Kind == "btoa") base64 = true;
            else if (ReferenceEquals(current.Children[0], current.Children[1])) selfXor = true;
            else otherXor = true;
            current = current.Children[0];
        }
        value = "";
        if (!Number(current, out var number)) return false;
        var text = current.Kind == "literal" ? current.Text! : number.ToString("R", CultureInfo.InvariantCulture);
        if (selfXor)
        { value = Encode(Encode(new string('\0', Encode(new string('\0', text.Length)).Length))); return true; }
        if (base64 && !otherXor) { value = Encode(text); return true; }
        if (current.Kind != "literal" || text.All(char.IsAsciiDigit))
        { value = Encode(Xor(number.ToString(number % 1 == 0 ? "0.0" : "R", CultureInfo.InvariantCulture), key)); return true; }
        return false;
    }
    private static string Evaluate(Expression expression)
    {
        if (expression.Kind == "literal") return expression.Text!;
        if (Number(expression, out var number)) return number.ToString("R", CultureInfo.InvariantCulture);
        if (expression.Kind == "call" && expression.Contains("random")) return Random.Shared.NextDouble().ToString("R", CultureInfo.InvariantCulture);
        if (expression.Kind == "btoa") return Encode(Evaluate(expression.Children[0]));
        if (expression.Kind == "xor")
        {
            var left = Evaluate(expression.Children[0]);
            return Xor(left, ReferenceEquals(expression.Children[0], expression.Children[1]) ? left : Evaluate(expression.Children[1]));
        }
        throw Unsupported();
    }
}
