using System.Text.Json.Serialization;

namespace ElPaqueto.Protocol.Models;

public class AutocompleteContext
{
    public static readonly AutocompleteContext Default = new AutocompleteContext
    {
        Vocab = "http://schema.nuget.org/schema#"
    };

    [JsonPropertyName("@vocab")]
    public string Vocab { get; set; }
}
