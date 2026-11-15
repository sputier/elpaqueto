using System.Text.Json.Serialization;
using ElPaqueto.Protocol.Models;

namespace ElPaqueto.Core;

/// <summary>
/// ElPaqueto's extensions to a registration index page.
/// </summary>
/// <remarks>Extends <see cref="RegistrationIndexPageItem"/>.</remarks>
public class ElPaquetoRegistrationIndexPageItem : RegistrationIndexPageItem
{
    /// <summary>
    /// The catalog entry containing the package metadata.
    /// </summary>
    /// <remarks>This was modified to use ElPaqueto's extended package metadata model.</remarks>
    [JsonPropertyName("catalogEntry")]
    [JsonPropertyOrder(int.MaxValue)]
    public new ElPaquetoPackageMetadata PackageMetadata { get; set; }
}
