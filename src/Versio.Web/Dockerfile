# Versio Web: the read-only viewer (docs/web/PLAN.md Step 6).
#
# Build from the repository root:
#   docker build -f src/Versio.Web/Dockerfile -t versio-web .
#
# The build stage publishes in Release, which builds the page with the Node pinned in
# web/node.props (fetched and hash-checked; tar is in the SDK image). The runtime stage is the
# ASP.NET image, run as its built-in non-root user, with nothing in it but the published server.
#
# Run (the secrets come from the host's secret store, never from this file or the image):
#   docker run --read-only -p 8080:8080 \
#     -v versio-logs:/var/log/versio -v versio-keys:/var/lib/versio/keys \
#     -e Web__GitHub__ClientId=... -e Web__GitHub__ClientSecret=... \
#     -e Web__GitHub__AppId=... -e Web__GitHub__PrivateKeyPem="$(cat app.pem)" \
#     -e Web__GitHub__PublicBaseUrl=https://versio.example.com \
#     versio-web
#
# Not built in the session that wrote it: there was no Docker on that machine. The publish it
# wraps was run there; this file is the part still to prove on a host that has Docker.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Versio.Web/Versio.Web.csproj -c Release -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Logs and the Data Protection key ring live on volumes: the root file system can be read-only
# (security rule 16), and every instance must read the same key ring or people are signed out at
# random. In production the ring itself is protected by the host's key vault (security rule 7).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Web__LogDirectory=/var/log/versio \
    Web__KeysDirectory=/var/lib/versio/keys \
    DOTNET_gcServer=0
RUN mkdir -p /var/log/versio /var/lib/versio/keys && chown -R $APP_UID /var/log/versio /var/lib/versio
VOLUME ["/var/log/versio", "/var/lib/versio/keys"]

USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Versio.Web.dll"]
