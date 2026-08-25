FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["src/Bootstrapper/Sankore.Admin/Sankore.Admin.csproj", "src/Bootstrapper/Sankore.Admin/"]
RUN dotnet restore "src/Bootstrapper/Sankore.Admin/Sankore.Admin.csproj"
COPY . .
WORKDIR "/src/src/Bootstrapper/Sankore.Admin"
RUN dotnet build "./Sankore.Admin.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Sankore.Admin.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Admin.dll"]
