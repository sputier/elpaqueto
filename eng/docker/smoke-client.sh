#!/bin/sh
# NuGet client part of eng/docker/smoke-test.sh. Runs inside a .NET SDK container that shares the network
# namespace of the server container, so http://127.0.0.1:8080 is the server, reached over loopback.
#
# Usage: smoke-client.sh push-and-restore | restore
# Environment: READER_USER, READER_PASSWORD (restore); for push-and-restore also SOURCE_KEY (a Read API
# key, the source password while pushing) and PUBLISH_KEY (a Publish API key, for "dotnet nuget push --api-key").
# Works in a fresh folder with an empty package cache on every run.
set -eu
mode=${1:?usage: smoke-client.sh push-and-restore | restore}
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export NUGET_PACKAGES=/work/packages HOME=/work/home
source_url=http://127.0.0.1:8080/v3/index.json
mkdir -p /work/home && cd /work

# A NuGet.Config per user. Plain HTTP is acceptable here only because the connection never leaves the
# container's loopback interface.
write_config() { # <file> <user> <password>
  cat > "$1" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="smoke" value="$source_url" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceCredentials>
    <smoke>
      <add key="Username" value="$2" />
      <add key="ClearTextPassword" value="$3" />
    </smoke>
  </packageSourceCredentials>
</configuration>
EOF
}

if [ "$mode" = push-and-restore ]; then
  mkdir -p publish && cd publish
  # The service index is read with the source credentials: a Read key as the password, any user name.
  # The push itself needs the Publish key, sent in the X-NuGet-ApiKey header.
  write_config NuGet.Config apikey "$SOURCE_KEY"
  cat > Smoke.Package.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <PackageId>ElPaqueto.Smoke.Package</PackageId>
    <Version>1.0.0</Version>
    <Authors>ElPaqueto smoke test</Authors>
    <Description>Package pushed by the container smoke test.</Description>
  </PropertyGroup>
</Project>
EOF
  printf 'namespace Smoke;\npublic static class Marker { public const string Value = "smoke"; }\n' > Marker.cs
  dotnet pack -c Release -o out
  # Visible only inside this throwaway client container, as in a CI job that gets it from its secret store.
  dotnet nuget push out/ElPaqueto.Smoke.Package.1.0.0.nupkg --source smoke --api-key "$PUBLISH_KEY"
  cd /work
fi

mkdir -p consume && cd consume
write_config NuGet.Config "$READER_USER" "$READER_PASSWORD"
cat > Consumer.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ElPaqueto.Smoke.Package" Version="1.0.0" />
  </ItemGroup>
</Project>
EOF
dotnet nuget locals all --clear
dotnet restore
test -f "$NUGET_PACKAGES/elpaqueto.smoke.package/1.0.0/elpaqueto.smoke.package.1.0.0.nupkg"
echo "restored ElPaqueto.Smoke.Package 1.0.0 from the server"
