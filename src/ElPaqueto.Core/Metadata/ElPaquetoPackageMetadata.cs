using System.Collections.Generic;
using System.Text.Json.Serialization;
using ElPaqueto.Protocol.Models;

namespace ElPaqueto.Core;

/// <summary>
/// ElPaqueto's extensions to the package metadata model.
/// </summary>
/// <remarks>
/// Extends <see cref="PackageMetadata"/>.<br/>
/// These additions are not part of the official protocol.
/// </remarks>
public class ElPaquetoPackageMetadata : PackageMetadata
{
    [JsonPropertyName("downloads")]
    public long Downloads { get; set; }

    [JsonPropertyName("hasReadme")]
    public bool HasReadme { get; set; }

    [JsonPropertyName("packageTypes")]
    public IReadOnlyList<string> PackageTypes { get; set; }

    /// <summary>
    /// The package's release notes.
    /// </summary>
    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; }

    [JsonPropertyName("repositoryUrl")]
    public string RepositoryUrl { get; set; }

    [JsonPropertyName("repositoryType")]
    public string RepositoryType { get; set; }
}
