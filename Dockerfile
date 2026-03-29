FROM mcr.microsoft.com/dotnet/sdk:10.0 AS client-build
WORKDIR /src

ARG PRIMARY_DOMAIN=localhost
ARG GRPC_PORT=5001
ARG API_KEY=

COPY ./Smena.Client ./Smena.Client
RUN dotnet restore Smena.Client/Smena.Client/Smena.Client.csproj
RUN dotnet publish Smena.Client/Smena.Client/Smena.Client.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=true -o /artifacts/published-client
RUN printf '{\n  "Grpc": {\n    "Address": "https://%s:%s",\n    "ApiKey": "%s"\n  }\n}\n' \
    "$PRIMARY_DOMAIN" \
    "$GRPC_PORT" \
    "$API_KEY" \
    > /artifacts/published-client/appsettings.json

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS updater-build
WORKDIR /src

COPY ./Smena.Updater ./Smena.Updater
RUN dotnet restore Smena.Updater/Smena.Updater/Smena.Updater.csproj
RUN dotnet publish Smena.Updater/Smena.Updater/Smena.Updater.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=true -p:PublishSingleFile=true -o /artifacts/published-updater

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src

COPY ./Smena.UpdateServer ./Smena.UpdateServer

RUN dotnet restore Smena.UpdateServer/Smena.UpdateServer.csproj
RUN dotnet publish Smena.UpdateServer/Smena.UpdateServer.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=server-build /app/publish .
COPY --from=client-build /artifacts/published-client ./updates/published-client
COPY --from=updater-build /artifacts/published-updater ./updates/published-updater

ENV ASPNETCORE_URLS=http://+:5100
EXPOSE 5100

ENTRYPOINT ["dotnet", "Smena.UpdateServer.dll"]
