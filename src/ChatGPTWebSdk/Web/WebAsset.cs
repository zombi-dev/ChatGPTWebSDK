namespace ChatGPTWebSdk.Web;

public sealed record WebAsset(string FileId, string AssetPointer, string MimeType, int Width, int Height, long SizeBytes);
public sealed record WebUploadedFile(string Id, string Name, string MimeType, long SizeBytes, int Width = 0, int Height = 0)
{
    public string? Sha256 { get; init; }
    public string Purpose { get; init; } = "user_data";
    public long CreatedAt { get; init; }
    public WebInputMessage ToMessage(string text)
    {
        var pointer = new System.Text.Json.Nodes.JsonObject { ["content_type"] = "image_asset_pointer", ["asset_pointer"] = "sediment://" + Id, ["size_bytes"] = SizeBytes, ["width"] = Width, ["height"] = Height };
        return WebInputMessage.User(text) with
        {
            Content = new() { ["content_type"] = "multimodal_text", ["parts"] = new System.Text.Json.Nodes.JsonArray(pointer, System.Text.Json.Nodes.JsonValue.Create(text)) },
            Metadata = new() { ["attachments"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["id"] = Id, ["name"] = Name, ["mime_type"] = MimeType, ["size"] = SizeBytes, ["width"] = Width, ["height"] = Height }) }
        };
    }
}
