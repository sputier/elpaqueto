using System.IO;
using System.IO.Compression;
using System.Text;

namespace ElPaqueto.Tests;

/// <summary>Builds small, valid packages with any id and version, in memory.</summary>
internal static class TestPackageBuilder
{
    /// <param name="id">The package id, in the case it should be stored.</param>
    /// <param name="version">The version as written in the nuspec, for example <c>1.0.0-Beta.1</c>.</param>
    /// <param name="dependencyId">An optional dependency of the <c>net8.0</c> group, any version.</param>
    public static byte[] Create(string id, string version, string dependencyId = null)
    {
        var dependencies = dependencyId is null
            ? "<group targetFramework=\"net8.0\" />"
            : $"<group targetFramework=\"net8.0\"><dependency id=\"{dependencyId}\" version=\"1.0.0\" /></group>";

        var nuspec = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>Test author</authors>
                <description>Test description of {id}</description>
                <dependencies>{dependencies}</dependencies>
              </metadata>
            </package>
            """;

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, id + ".nuspec", Encoding.UTF8.GetBytes(nuspec));
            WriteEntry(zip, "lib/net8.0/_._", []);
        }

        return buffer.ToArray();
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
