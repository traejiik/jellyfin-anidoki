FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /app
COPY . /app

RUN dotnet publish jellyfin-anidoki/jellyfin-anidoki.csproj --configuration Release --output /app/publish

FROM alpine AS final
WORKDIR /app
COPY --from=build /app/publish /app/bin

CMD ["cp",  "/app/bin/jellyfin-anidoki.dll", "/out/jellyfin-anidoki.dll"]
