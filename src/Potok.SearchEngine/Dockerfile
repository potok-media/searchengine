# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files and restore as distinct layers
COPY ["src/Potok.SearchEngine/Potok.SearchEngine.csproj", "src/Potok.SearchEngine/"]
COPY ["src/Potok.SearchEngine.Core/Potok.SearchEngine.Core.csproj", "src/Potok.SearchEngine.Core/"]
COPY ["src/Potok.SearchEngine.Infrastructure/Potok.SearchEngine.Infrastructure.csproj", "src/Potok.SearchEngine.Infrastructure/"]

RUN dotnet restore "src/Potok.SearchEngine/Potok.SearchEngine.csproj"

# Copy the remaining source files and build/publish the application
COPY . .
WORKDIR "/src/src/Potok.SearchEngine"

ARG VERSION=1.0.0-dev
RUN dotnet publish "Potok.SearchEngine.csproj" -c Release -o /app/publish /p:UseAppHost=false /p:Version=${VERSION}

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Default SearchEngine port
ENV PORT=6000
EXPOSE 6000

ENTRYPOINT ["dotnet", "Potok.SearchEngine.dll"]
