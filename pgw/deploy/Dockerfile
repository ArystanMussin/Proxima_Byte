FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/PGW.Host/PGW.Host.csproj -c Release -r linux-x64 --self-contained true -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
RUN useradd -r -s /usr/sbin/nologin pgw \
    && mkdir -p /etc/pgw /var/lib/pgw /var/log/pgw \
    && chown -R pgw:pgw /var/lib/pgw /var/log/pgw
WORKDIR /app
COPY --from=build /app .
USER pgw
ENV PGW_CONFIG=/etc/pgw/project.yaml
# Bind 0.0.0.0 inside the container: the container's own network namespace + `-p` publishing is the
# access boundary here, not the app (unlike a bare-metal/systemd install, where 127.0.0.1 is correct — §10).
ENV PGW_API_URL=http://0.0.0.0:8420
EXPOSE 502 8420
ENTRYPOINT ["./PGW.Host"]
