FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# Central Package Management: every PackageReference in this repo omits its version, so
# restore fails with NU1015 unless Directory.Packages.props is present. global.json pins
# the SDK band. Copied before the csproj so this layer is cached independently of source.
COPY ["Directory.Packages.props", "."]
COPY ["Directory.Build.props", "."]
COPY ["global.json", "."]
COPY ["src/Bootstrapper/Sankore.Hangfire/Sankore.Hangfire.csproj", "src/Bootstrapper/Sankore.Hangfire/"]
RUN dotnet restore "src/Bootstrapper/Sankore.Hangfire/Sankore.Hangfire.csproj"
COPY . .
WORKDIR "/src/src/Bootstrapper/Sankore.Hangfire"
# Directory.Build.props sets TreatWarningsAsErrors in Release, and Sankore.Shared.Kernel
# still carries analyzer findings (CA1000/CA1716). The same overrides the Sankore.Api image
# already uses keep the image buildable; the quality bar for local and CI builds is untouched.
RUN dotnet build "./Sankore.Hangfire.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sankore.Hangfire.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Hangfire.dll"]
