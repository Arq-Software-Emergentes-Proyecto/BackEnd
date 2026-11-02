namespace BuildingFex.Api.Iam.Infrastructure.Tokens.Jwt.Configuration;

public class FirebaseAuthSettings
{
    public const string SectionName = "Firebase";

    /// <summary>Firebase project id (e.g. emergentes-36306). Empty disables Firebase Bearer validation.</summary>
    public string ProjectId { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ProjectId);
}
