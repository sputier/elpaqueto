using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using static ElPaqueto.EndToEnd.Tests.Harness.PackageSet;

namespace ElPaqueto.EndToEnd.Tests.Harness;

/// <summary>A package file produced by <c>dotnet pack</c>.</summary>
internal sealed record TestPackage(string Id, string Version, string Path)
{
    /// <summary>The SHA-512 of the file, in hexadecimal.</summary>
    public string Sha512 => Convert.ToHexString(SHA512.HashData(File.ReadAllBytes(Path)));

    public override string ToString() => Id + " " + Version;
}

/// <summary>The packages of the scenarios, generated once per run.</summary>
internal sealed class PackageSet
{
    public const string BaseId = "ElPaqueto.E2E.Base";
    public const string DependentId = "ElPaqueto.E2E.Dependent";
    public const string SymbolsId = "ElPaqueto.E2E.Symbols";

    public required TestPackage Base100 { get; init; }

    public required TestPackage Base110 { get; init; }

    public required TestPackage Base120Preview { get; init; }

    public required TestPackage Base200 { get; init; }

    /// <summary>Base 1.0.0 again, with different content: a push of it must never replace the original.</summary>
    public required TestPackage Base100Variant { get; init; }

    /// <summary>Depends on <c>ElPaqueto.E2E.Base</c> <c>[1.0.0, )</c>.</summary>
    public required TestPackage Dependent100 { get; init; }

    /// <summary>Packed with <c>-p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg</c>: the <c>.snupkg</c> is next to it.</summary>
    public required TestPackage Symbols100 { get; init; }
}

/// <summary>
/// Writes minimal SDK-style projects and packs them with the real <c>dotnet pack</c>, in an isolated client
/// whose only package source is a local folder: packing needs no network. No binary is committed: the
/// package icon is generated here too.
/// </summary>
internal static class TestPackages
{
    private static readonly Lazy<Task<PackageSet>> Built = new(BuildAsync);

    public static Task<PackageSet> GetAsync() => Built.Value;

    private static async Task<PackageSet> BuildAsync()
    {
        // Not disposed: the packages are read by every fixture until the run ends with the temporary root.
        var client = new NuGetClient(output: null);
        var feed = Path.Combine(client.Root, "feed");
        var variantFeed = Path.Combine(client.Root, "feed-variant");
        Directory.CreateDirectory(feed);
        Directory.CreateDirectory(variantFeed);
        client.WriteConfig($"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="local" value="{feed}" />
              </packageSources>
            </configuration>
            """);

        await Task.WhenAll(
            PackBaseAsync(client, "1.0.0", feed, variant: false),
            PackBaseAsync(client, "1.1.0", feed, variant: false),
            PackBaseAsync(client, "1.2.0-preview.1", feed, variant: false),
            PackBaseAsync(client, "2.0.0", feed, variant: false),
            PackBaseAsync(client, "1.0.0", variantFeed, variant: true),
            PackSymbolsAsync(client, feed));

        // Restored from the local folder, which holds Base 1.0.0 by now.
        var dependent = WriteLibrary(client, "dependent", DependentId, "1.0.0", """
            namespace ElPaqueto.E2E.Dependent;

            public static class Greeter
            {
                public static string Greet(string name) => "Hello " + name + " from " + ElPaqueto.E2E.Base.BaseInfo.Describe();
            }
            """, extraProperties: string.Empty, extraItems: $"""<PackageReference Include="{BaseId}" Version="[1.0.0, )" />""");
        await PackAsync(client, dependent, feed);

        return new PackageSet
        {
            Base100 = Package(feed, BaseId, "1.0.0"),
            Base110 = Package(feed, BaseId, "1.1.0"),
            Base120Preview = Package(feed, BaseId, "1.2.0-preview.1"),
            Base200 = Package(feed, BaseId, "2.0.0"),
            Base100Variant = Package(variantFeed, BaseId, "1.0.0"),
            Dependent100 = Package(feed, DependentId, "1.0.0"),
            Symbols100 = Package(feed, SymbolsId, "1.0.0"),
        };
    }

    private static Task PackBaseAsync(NuGetClient client, string version, string output, bool variant)
    {
        var directory = WriteLibrary(client, "base-" + version + (variant ? "-variant" : string.Empty), BaseId, version, $$"""
            namespace ElPaqueto.E2E.Base;

            public static class BaseInfo
            {
                public const string Version = "{{version}}";

                public static string Describe() => "ElPaqueto.E2E.Base " + Version{{(variant ? " + \" (variant)\"" : string.Empty)}};
            }
            """,
            extraProperties: """
                <PackageReadmeFile>README.md</PackageReadmeFile>
                <PackageIcon>icon.png</PackageIcon>
            """,
            extraItems: """
                <None Include="README.md" Pack="true" PackagePath="\" />
                <None Include="icon.png" Pack="true" PackagePath="\" />
            """);
        File.WriteAllText(Path.Combine(directory, "README.md"), $"# ElPaqueto.E2E.Base {version}\n\nA package generated by the end-to-end tests.\n");
        File.WriteAllBytes(Path.Combine(directory, "icon.png"), CreatePng());
        return PackAsync(client, directory, output);
    }

    private static Task PackSymbolsAsync(NuGetClient client, string output)
    {
        var directory = WriteLibrary(client, "symbols", SymbolsId, "1.0.0", """
            namespace ElPaqueto.E2E.Symbols;

            public static class SymbolsInfo
            {
                public static int Answer() => 42;
            }
            """, extraProperties: string.Empty, extraItems: string.Empty);
        return PackAsync(client, directory, output, "-p:IncludeSymbols=true", "-p:SymbolPackageFormat=snupkg");
    }

    private static string WriteLibrary(NuGetClient client, string directoryName, string id, string version, string code, string extraProperties, string extraItems)
    {
        var directory = client.CreateWorkDirectory(directoryName);
        // No repository, project or license URL: every URL a server response contains must be one it generated.
        File.WriteAllText(Path.Combine(directory, id + ".csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <PackageId>{id}</PackageId>
                <AssemblyName>{id}</AssemblyName>
                <Version>{version}</Version>
                <Authors>ElPaqueto end-to-end tests</Authors>
                <Description>A package generated by the ElPaqueto end-to-end tests.</Description>
                <GenerateDocumentationFile>false</GenerateDocumentationFile>
            {extraProperties}
              </PropertyGroup>
              <ItemGroup>
            {extraItems}
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Code.cs"), code);
        return directory;
    }

    private static async Task PackAsync(NuGetClient client, string projectDirectory, string output, params string[] extraArguments)
    {
        var result = await client.DotnetAsync(
            projectDirectory,
            ["pack", "--configuration", "Release", "--output", output, .. extraArguments]);
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException("Prerequisite failed: a test package could not be packed." + Environment.NewLine + result.Describe());
        }
    }

    private static TestPackage Package(string feed, string id, string version)
    {
        var path = Path.Combine(feed, $"{id}.{version}.nupkg");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Prerequisite failed: dotnet pack did not produce {path}.");
        }

        return new TestPackage(id, version, path);
    }

    /// <summary>A valid 2x2 RGB PNG, built in code.</summary>
    private static byte[] CreatePng()
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), 2);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 2);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        WriteChunk(stream, "IHDR", header);

        // Two scanlines, each a filter byte (0) and two RGB pixels.
        byte[] pixels = [0, 0x2B, 0x6C, 0xB0, 0xF2, 0x9F, 0x05, 0, 0xF2, 0x9F, 0x05, 0x2B, 0x6C, 0xB0];
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(pixels);
            }

            WriteChunk(stream, "IDAT", compressed.ToArray());
        }

        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
        stream.Write(typeAndData);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typeAndData));
        stream.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
