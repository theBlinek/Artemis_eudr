FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Eudr.BatchRunner/Eudr.BatchRunner.csproj Eudr.BatchRunner/
RUN dotnet restore Eudr.BatchRunner/Eudr.BatchRunner.csproj

COPY src/Eudr.BatchRunner/ Eudr.BatchRunner/
RUN dotnet publish Eudr.BatchRunner/Eudr.BatchRunner.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Eudr.BatchRunner.dll"]
