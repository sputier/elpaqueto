using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Azure
{
    public class AzureSearchOptions
    {
        [Required]
        public string AccountName { get; set; }

        [Required]
        public string ApiKey { get; set; }
    }
}
