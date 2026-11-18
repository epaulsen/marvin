FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS build
WORKDIR /src
COPY src/marvin.api/marvin.api.csproj ./marvin.api/
RUN dotnet restore marvin.api/marvin.api.csproj
COPY src/marvin.api/ ./marvin.api/
RUN dotnet publish marvin.api/marvin.api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine
WORKDIR /app
COPY --from=build /app/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "marvin.api.dll"]
