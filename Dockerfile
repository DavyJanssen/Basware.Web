FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Basware.Core/Basware.Core.csproj src/Basware.Core/
COPY src/Basware.Web/Basware.Web.csproj src/Basware.Web/
RUN dotnet restore src/Basware.Web/Basware.Web.csproj
COPY src/ src/
RUN dotnet publish src/Basware.Web/Basware.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER root
RUN mkdir -p /app/keys && chown app:app /app/keys
COPY --from=build /app/publish .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Basware.Web.dll"]
