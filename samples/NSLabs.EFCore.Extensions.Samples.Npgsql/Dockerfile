# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy central props and solution metadata first for better caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY NSLabs.EFCore.Extensions.slnx ./

# Copy all source
COPY src/ ./src/
COPY samples/ ./samples/

# Publish Npgsql sample (self-contained publish not needed)
WORKDIR /src/samples/NSLabs.EFCore.Extensions.Samples.Npgsql
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

# Default connection for docker-compose network; override via env ConnectionStrings__DefaultConnection
ENV ConnectionStrings__DefaultConnection="Host=postgres;Port=5432;Database=nsamples;Username=postgres;Password=postgres"

ENTRYPOINT ["dotnet", "NSLabs.EFCore.Extensions.Samples.Npgsql.dll"]
