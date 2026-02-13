FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Bakabooru.sln ./
COPY Bakabooru.Core/Bakabooru.Core.csproj Bakabooru.Core/
COPY Bakabooru.Data/Bakabooru.Data.csproj Bakabooru.Data/
COPY Bakabooru.Processing/Bakabooru.Processing.csproj Bakabooru.Processing/
COPY Bakabooru.Server/Bakabooru.Server.csproj Bakabooru.Server/
RUN dotnet restore Bakabooru.Server/Bakabooru.Server.csproj

COPY . .
RUN dotnet publish Bakabooru.Server/Bakabooru.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/data/thumbnails /app/data/temp

ENV ASPNETCORE_URLS=http://+:6666
ENV Bakabooru__Storage__DatabasePath=/app/data/bakabooru.db
ENV Bakabooru__Storage__ThumbnailPath=/app/data/thumbnails
ENV Bakabooru__Storage__TempPath=/app/data/temp

EXPOSE 6666

ENTRYPOINT ["dotnet", "Bakabooru.Server.dll"]
