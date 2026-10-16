# AutoFill Studio: API + UI + background worker in one container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore first so package downloads are cached while only code changes.
COPY Directory.Build.props AutoFill.sln ./
COPY src/AutoFill.Core/AutoFill.Core.csproj src/AutoFill.Core/
COPY src/AutoFill.Data/AutoFill.Data.csproj src/AutoFill.Data/
COPY src/AutoFill.Ai/AutoFill.Ai.csproj src/AutoFill.Ai/
COPY src/AutoFill.Documents/AutoFill.Documents.csproj src/AutoFill.Documents/
COPY src/AutoFill.Web/AutoFill.Web.csproj src/AutoFill.Web/
RUN dotnet restore src/AutoFill.Web/AutoFill.Web.csproj
COPY src/ src/
RUN dotnet publish src/AutoFill.Web/AutoFill.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# LibreOffice (no GUI) turns old .doc/.xls files into .docx/.xlsx; the fonts keep Vietnamese text intact.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libreoffice-writer-nogui libreoffice-calc-nogui fonts-liberation fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# HOME: LibreOffice writes a little state there even with its own profile folder.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DATA_DIR=/tmp/autofill \
    HOME=/tmp \
    SOFFICE_PATH=/usr/bin/soffice
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AutoFill.Web.dll"]
