using System.ComponentModel.DataAnnotations;

namespace ElPaqueto.Azure
{
    public class AzureTableOptions
    {
        [Required]
        public string ConnectionString { get; set; }
        public string TableName { get; set; } = "Packages";
    }
}
