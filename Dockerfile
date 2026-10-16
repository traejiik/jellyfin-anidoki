FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY . /app
# The build context's owner differs from the container user; git describe needs to trust it.
RUN git config --global --add safe.directory /app

RUN python3 scripts/release.py local --output /app/anidoki

FROM alpine AS final
WORKDIR /app
COPY --from=build /app/anidoki /app/bin

CMD ["sh", "-c", "cp /app/bin/* /out/"]
