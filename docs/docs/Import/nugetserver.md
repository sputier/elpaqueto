# Import NuGet.Server packages

[NuGet.Server](https://github.com/NuGet/NuGet.Server) is a lightweight standalone NuGet server. It is strongly recommended that you upgrade to BaGetter if you use NuGet.Server. Feel free to open a [GitHub issue](https://github.com/bagetter/BaGetter/issues) if you need help migrating.

:::info

Please refer to the [BaGetter vs NuGet.Server](../vs/nugetserver.md) page for reasons to upgrade to BaGetter.

:::

## Steps

Make sure that you've installed [nuget.exe](https://www.nuget.org/downloads). In PowerShell, run:

```powershell
$source = "<NuGet.Server package source>"
$destination = "<BaGetter package source>"
```

ElPaqueto requires an API key with the `Publish` permission for every push (see [Publishing packages](https://github.com/sputier/ElPaqueto/blob/develop/docs/user/publishing.md)). Set it using [the `setapikey` command](https://docs.microsoft.com/en-us/nuget/reference/cli-reference/cli-ref-setapikey), and give the source credentials with `Read` as described in [client setup](https://github.com/sputier/ElPaqueto/blob/develop/docs/user/client-setup.md):

```powershell
& nuget.exe setapikey "MY-API-KEY" -Source $destination
```

Now run the following PowerShell script:

```powershell
if (!(Test-Path "Web.config")) {
  throw "Please run this script in the same directory as NuGet.Server's Web.config file"
}

(& nuget.exe list -AllVersions -Source $source).Split([Environment]::NewLine) | % {
  $id = $_.Split(" ")[0].Trim()
  $version = $_.Split(" ")[1].Trim()

  $path = [IO.Path]::Combine("Packages", $id, $version, "${id}.${version}.nupkg")

  Write-Host "nuget.exe push -Source $destination ""$path"""
  & nuget.exe push -Source $destination $path
}
```
