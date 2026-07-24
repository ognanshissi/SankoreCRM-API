FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Bootstrapper/Sankore.Api/Sankore.Api.csproj", "src/Bootstrapper/Sankore.Api/"]
COPY ["src/Shared/Sankore.Shared.Kernel/Sankore.Shared.Kernel.csproj", "src/Shared/Sankore.Shared.Kernel/"]
COPY ["src/Shared/Sankore.Shared.Infrastructure/Sankore.Shared.Infrastructure.csproj", "src/Shared/Sankore.Shared.Infrastructure/"]
COPY ["src/Modules/Users/Sankore.Modules.Users/Sankore.Modules.Users.csproj", "src/Modules/Users/Sankore.Modules.Users/"]
COPY ["src/Modules/Users/Sankore.Modules.Users.PublicApi/Sankore.Modules.Users.PublicApi.csproj", "src/Modules/Users/Sankore.Modules.Users.PublicApi/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads/Sankore.Modules.Leads.csproj", "src/Modules/Leads/Sankore.Modules.Leads/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads.PublicApi/Sankore.Modules.Leads.PublicApi.csproj", "src/Modules/Leads/Sankore.Modules.Leads.PublicApi/"]
RUN dotnet restore "src/Bootstrapper/Sankore.Api/Sankore.Api.csproj"
COPY . .
WORKDIR "/src/src/Bootstrapper/Sankore.Api"
RUN dotnet build "./Sankore.Api.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sankore.Api.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Api.dll"]
