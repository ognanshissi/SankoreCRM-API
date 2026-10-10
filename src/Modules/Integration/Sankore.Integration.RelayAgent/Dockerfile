# Relay agent image (INT-26, criterion 1).
#
# Built from the repository ROOT, like the other images here:
#   docker build -f src/Modules/Integration/Sankore.Integration.RelayAgent/Dockerfile \
#                -t sankore-relay-agent:1.0.0 .
#
# The runtime image, not aspnet: this process serves nothing. It has no listener, binds no port
# and exposes none — see Channel/RelayChannel.cs. An EXPOSE line here would be a lie about the
# component's trust model, so there is none.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Central Package Management: every PackageReference in this repo omits its version, so restore
# fails with NU1015 unless Directory.Packages.props is present. global.json pins the SDK band.
# Copied before the csproj so this layer is cached independently of source.
COPY ["Directory.Packages.props", "."]
COPY ["Directory.Build.props", "."]
COPY ["Directory.Build.targets", "."]
COPY ["global.json", "."]
COPY ["src/Modules/Integration/Sankore.Integration.RelayAgent/Sankore.Integration.RelayAgent.csproj", "src/Modules/Integration/Sankore.Integration.RelayAgent/"]
RUN dotnet restore "src/Modules/Integration/Sankore.Integration.RelayAgent/Sankore.Integration.RelayAgent.csproj"
COPY . .
WORKDIR "/src/src/Modules/Integration/Sankore.Integration.RelayAgent"
RUN dotnet build "./Sankore.Integration.RelayAgent.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sankore.Integration.RelayAgent.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# The configuration file and the client certificate are MOUNTED, never baked in. Two reasons, and
# the second is the one that matters: the certificate is this agent's identity, so an image
# carrying one could be run twice — or run by somebody else — as the same agent. Keeping both
# outside the image also means an upgrade is a tag change and touches neither.
#
#   docker run -d --name sankore-relay \
#     -v /etc/sankore:/etc/sankore:ro \
#     sankore-relay-agent:1.0.0
#
# /etc/sankore is where the agent looks by default; SANKORE_RELAY_CONFIG overrides it.
VOLUME ["/etc/sankore"]

ENTRYPOINT ["dotnet", "Sankore.Integration.RelayAgent.dll"]
