using System.Collections.Generic;
using System.Text.Json.Serialization;
using ElPaqueto.Protocol.Models;

namespace ElPaqueto.Core;

/// <summary>
/// ElPaqueto's extensions to a registration index response.
/// </summary>
/// <remarks>Extends <see cref="RegistrationIndexResponse"/>.</remarks>
public class ElPaquetoRegistrationIndexResponse : RegistrationIndexResponse
{
    /// <summary>
    /// The pages that contain all of the versions of the package, ordered by the package's version.
    /// </summary>
    /// <remarks>This was modified to use ElPaqueto's extended registration index page model.</remarks>
    [JsonPropertyName("items")]
    [JsonPropertyOrder(int.MaxValue)]
    public new IReadOnlyList<ElPaquetoRegistrationIndexPage> Pages { get; set; }

    /// <summary>
    /// The package's total downloads across all versions.
    /// </summary>
    /// <remarks>This is not part of the official NuGet protocol.</remarks>
    [JsonPropertyName("totalDownloads")]
    [JsonPropertyOrder(int.MaxValue)]
    public long TotalDownloads { get; set; }
}
