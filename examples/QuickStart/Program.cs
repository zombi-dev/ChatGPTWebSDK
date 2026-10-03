using System.Reflection;
using ChatGPTWebSdk.Browser;
using OpenAI;
using ChatGPTWebSdk.Mcp;

var bundle = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "ChatGPTWebBundleDirectory").Value!;
var browserDirectory = Path.Combine(bundle, "browsers");
if (args.Contains("--verify-bundle"))
{
    foreach (var acceleration in new[] { BrowserAcceleration.Automatic, BrowserAcceleration.Software })
    {
        var result = await BrowserDiagnostics.VerifyAsync(new() { BundledBrowserDirectory = browserDirectory, Headless = true, Acceleration = acceleration, Progress = Console.WriteLine, Timeout = TimeSpan.FromSeconds(45) });
        if (acceleration == BrowserAcceleration.Software && (!result.SoftwareRendering || result.HardwareAcceleration == true)) throw new InvalidOperationException("Software rendering was not applied.");
        Console.WriteLine($"Bundled Chromium {result.BrowserVersion}: invisible launch, {acceleration}, software={result.SoftwareRendering}, hardware={result.HardwareAcceleration}; JavaScript and canvas passed.");
    }
    return;
}
Console.Write("Paste the authentication string copied by the extension: ");
var authentication = Environment.GetEnvironmentVariable("CHATGPT_WEB_AUTH") ?? Console.ReadLine() ?? throw new ArgumentException("Authentication is required.");
var mcpUrl = Environment.GetEnvironmentVariable("CHATGPT_WEB_MCP_URL");
using var runtime = string.IsNullOrWhiteSpace(mcpUrl)
    ? ChatGPTWeb.Initialize(authentication, browser: new() { BundledBrowserDirectory = browserDirectory, Progress = Console.WriteLine })
    : ChatGPTWeb.Initialize(authentication, mcp: new McpConversationOptions { Servers = [new() { Label = "tools", Endpoint = new Uri(mcpUrl) }] },
        browser: new() { BundledBrowserDirectory = browserDirectory, Progress = Console.WriteLine });
var models = await runtime.CreateClient().GetOpenAIModelClient().GetModelsAsync();
var model = Environment.GetEnvironmentVariable("CHATGPT_WEB_MODEL") ?? models.Value.First().Id;
var chat = runtime.CreateClient(threadId: "quick-start").GetChatClient(model);
Console.WriteLine($"Using web model {model}. Type /exit to leave.");
while (true)
{
    Console.Write("You: ");
    var input = Console.ReadLine();
    if (input is null or "/exit") break;
    Console.Write("ChatGPT: ");
    await foreach (var update in chat.CompleteChatStreamingAsync(input))
        foreach (var part in update.ContentUpdate) Console.Write(part.Text);
    Console.WriteLine();
}
