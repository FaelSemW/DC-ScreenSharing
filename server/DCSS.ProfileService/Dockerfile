FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["Directory.Build.props", "./"]
COPY ["server/DCSS.ProfileService/DCSS.ProfileService.csproj", "server/DCSS.ProfileService/"]
COPY ["src/DC-ScreenSharing.Shared/DC-ScreenSharing.Shared.csproj", "src/DC-ScreenSharing.Shared/"]
COPY ["src/DC-ScreenSharing.Core/DC-ScreenSharing.Core.csproj", "src/DC-ScreenSharing.Core/"]

RUN dotnet restore "server/DCSS.ProfileService/DCSS.ProfileService.csproj"

COPY . .
WORKDIR "/src/server/DCSS.ProfileService"
RUN dotnet build "DCSS.ProfileService.csproj" -c Release -o /app/build
RUN dotnet publish "DCSS.ProfileService.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=5000
ENV ProfileService__StoragePath=/app/storage

ENTRYPOINT ["dotnet", "DCSS.ProfileService.dll"]
