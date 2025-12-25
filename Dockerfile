# Build stage - using .NET 10 preview
FROM mcr.microsoft.com/dotnet/nightly/sdk:10.0-preview AS build
WORKDIR /src

# Copy project file and restore
COPY src/DiagManTestApp/DiagManTestApp.csproj ./DiagManTestApp/
RUN dotnet restore DiagManTestApp/DiagManTestApp.csproj

# Copy source and build
COPY src/DiagManTestApp/ ./DiagManTestApp/
RUN dotnet publish DiagManTestApp/DiagManTestApp.csproj -c Release -o /app/publish

# Runtime stage - using .NET 10 preview
FROM mcr.microsoft.com/dotnet/nightly/aspnet:10.0-preview AS runtime
WORKDIR /app

# Copy published app
COPY --from=build /app/publish .

# Expose port
EXPOSE 8080

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "DiagManTestApp.dll"]
