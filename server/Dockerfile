FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Damebooru.sln ./
COPY Damebooru.Core/Damebooru.Core.csproj Damebooru.Core/
COPY Damebooru.Data/Damebooru.Data.csproj Damebooru.Data/
COPY Damebooru.Processing/Damebooru.Processing.csproj Damebooru.Processing/
COPY Damebooru.Server/Damebooru.Server.csproj Damebooru.Server/
RUN dotnet restore Damebooru.Server/Damebooru.Server.csproj

COPY . .
RUN dotnet publish Damebooru.Server/Damebooru.Server.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/data/thumbnails /app/data/temp

ENV ASPNETCORE_URLS=http://+:6666
ENV Damebooru__Storage__DatabasePath=/app/data/damebooru.db
ENV Damebooru__Storage__ThumbnailPath=/app/data/thumbnails
ENV Damebooru__Storage__TempPath=/app/data/temp

EXPOSE 6666

ENTRYPOINT ["dotnet", "Damebooru.Server.dll"]
