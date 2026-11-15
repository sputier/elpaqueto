using System.ComponentModel.DataAnnotations;
using ElPaqueto.Core;

namespace ElPaqueto.Gcp;

public class GoogleCloudStorageOptions : StorageOptions
{
    [Required]
    public string BucketName { get; set; }
}
