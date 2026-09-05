FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Directory.Packages.props", "."]
COPY ["Directory.Build.props", "."]
COPY ["global.json", "."]
COPY ["src/Bootstrapper/Sankore.Api/Sankore.Api.csproj", "src/Bootstrapper/Sankore.Api/"]
COPY ["src/Shared/Sankore.Shared.Kernel/Sankore.Shared.Kernel.csproj", "src/Shared/Sankore.Shared.Kernel/"]
COPY ["src/Shared/Sankore.Shared.Infrastructure/Sankore.Shared.Infrastructure.csproj", "src/Shared/Sankore.Shared.Infrastructure/"]
COPY ["src/Modules/Administration/Sankore.Modules.Administration/Sankore.Modules.Administration.csproj", "src/Modules/Administration/Sankore.Modules.Administration/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads/Sankore.Modules.Leads.csproj", "src/Modules/Leads/Sankore.Modules.Leads/"]
COPY ["src/Modules/Notifications/Sankore.Modules.Notifications/Sankore.Modules.Notifications.csproj", "src/Modules/Notifications/Sankore.Modules.Notifications/"]
COPY ["src/Modules/Workflow/Sankore.Modules.Workflow/Sankore.Modules.Workflow.csproj", "src/Modules/Workflow/Sankore.Modules.Workflow/"]
RUN dotnet restore "src/Bootstrapper/Sankore.Api/Sankore.Api.csproj"
COPY . .
WORKDIR "/src/src/Bootstrapper/Sankore.Api"
RUN dotnet build "./Sankore.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sankore.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Api.dll"]
