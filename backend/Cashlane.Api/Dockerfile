FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY backend/Cashlane.Api/Cashlane.Api.csproj backend/Cashlane.Api/
RUN dotnet restore backend/Cashlane.Api/Cashlane.Api.csproj

COPY . .
RUN dotnet publish backend/Cashlane.Api/Cashlane.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://0.0.0.0:9090
EXPOSE 9090
ENTRYPOINT ["dotnet", "Cashlane.Api.dll"]
