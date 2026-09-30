FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Crm.slnx ./
COPY src/Crm.Api/Crm.Api.csproj src/Crm.Api/
COPY tests/Crm.Api.Tests/Crm.Api.Tests.csproj tests/Crm.Api.Tests/
RUN dotnet restore src/Crm.Api/Crm.Api.csproj
COPY . .
RUN dotnet publish src/Crm.Api/Crm.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Crm.Api.dll"]
