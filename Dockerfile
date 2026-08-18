# Build and run the API. The same image serves the application and applies
# migrations, selected by the --migrate argument, so the schema is never applied
# by a build that differs from the one that will serve it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Restore against the manifests first so a source-only change does not
# re-download packages. Central package management means the two Directory.*
# files are part of the restore graph.
COPY global.json Directory.Build.props Directory.Packages.props GiftCardPlatform.slnx ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore GiftCardPlatform.slnx

RUN dotnet publish src/GiftCardPlatform.Api/GiftCardPlatform.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Two things the runtime image does not ship.
#
# libgssapi-krb5-2: Npgsql loads the Kerberos GSSAPI library when it negotiates a
# connection. Without it every database call fails with
# "libgssapi_krb5.so.2: cannot open shared object file", which reads like a
# missing application dependency rather than a gap in the base image.
#
# curl: the container healthcheck needs something to call /health/ready with.
# The image has neither curl nor wget, so a healthcheck written against either
# fails silently and the container is reported unhealthy while the application
# is in fact serving normally.
RUN apt-get update \
    && apt-get install --no-install-recommends --yes libgssapi-krb5-2 curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app ./

# Runs as a non-root user. The database roles are separately non-superuser and
# NOBYPASSRLS; this is the container-level half of the same idea.
USER $APP_UID

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080

ENTRYPOINT ["dotnet", "GiftCardPlatform.Api.dll"]
