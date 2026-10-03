using System.Net;
using ChatGPTWebSdk.Protocol;

namespace ChatGPTWebSdk.Web;

/// <summary>Web-only settings shared by native and OpenAI-compatible clients.</summary>
public sealed record WebChatContext
{
    public string? ProjectId { get; init; }
    public string? GizmoId { get; init; }
    public bool TemporaryChat { get; init; }

    public string? ResolveGizmoId()
    {
        if (ProjectId is not null) ValidateProjectId(ProjectId);
        if (GizmoId is not null) ValidateGizmoId(GizmoId);
        if (ProjectId is not null && GizmoId is not null && ProjectId != GizmoId)
            throw new ArgumentException("ProjectId and GizmoId must identify the same project when both are supplied.");
        var id = ProjectId ?? GizmoId;
        if (TemporaryChat && id?.StartsWith("g-p-", StringComparison.Ordinal) == true)
            throw new SdkException("ChatGPT does not support temporary chats inside projects. Use separate thread IDs for project and temporary chats.", "temporary_project_conflict", HttpStatusCode.BadRequest);
        return id;
    }

    public static void ValidateProjectId(string id)
    {
        ValidateGizmoId(id);
        if (!id.StartsWith("g-p-", StringComparison.Ordinal) || id.Length <= 4)
            throw new ArgumentException("ProjectId must be a ChatGPT project identifier beginning with g-p-.");
    }

    private static void ValidateGizmoId(string id)
    {
        if (!id.StartsWith("g-", StringComparison.Ordinal) || id.Length is < 3 or > 256 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Invalid ChatGPT gizmo identifier.");
    }
}
