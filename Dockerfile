FROM mcr.microsoft.com/dotnet/sdk:11.0-alpine AS build
WORKDIR /src
COPY src/marvin.api/marvin.api.csproj ./marvin.api/
RUN dotnet restore marvin.api/marvin.api.csproj
COPY src/marvin.api/ ./marvin.api/
COPY skills/ /skills/
RUN dotnet publish marvin.api/marvin.api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS glibc
RUN mkdir -p /glibc && cp -a /lib/*-linux-gnu/. /glibc/ && cp -L /lib/*-linux-gnu/ld-linux*.so.* /glibc/ld.so

FROM mcr.microsoft.com/dotnet/aspnet:11.0-alpine
COPY --from=glibc /glibc/ /opt/glibc/
COPY --from=glibc /usr/share/dotnet/ /usr/share/dotnet/
RUN apk add --no-cache patchelf && \
    patchelf --set-interpreter /opt/glibc/ld.so --set-rpath /opt/glibc /usr/share/dotnet/dotnet && \
    apk del patchelf
ENV LD_LIBRARY_PATH=/opt/glibc
WORKDIR /app
COPY --from=build /app/ ./
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["/usr/share/dotnet/dotnet", "marvin.api.dll"]
