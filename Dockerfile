# syntax=docker/dockerfile:1.7-labs

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS client-build
WORKDIR /src

COPY ./Smena.Client ./Smena.Client
RUN dotnet restore Smena.Client/Smena.Client/Smena.Client.csproj
RUN dotnet publish Smena.Client/Smena.Client/Smena.Client.csproj -c Release -r win-x64 --self-contained false -p:UseAppHost=true -o /artifacts/published-client

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS server-build
WORKDIR /src

COPY ./Smena.UpdateServer ./Smena.UpdateServer

RUN dotnet restore Smena.UpdateServer/Smena.UpdateServer.csproj
RUN dotnet publish Smena.UpdateServer/Smena.UpdateServer.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=server-build /app/publish .
COPY --from=client-build /artifacts/published-client ./updates/published-client

ENV ASPNETCORE_URLS=http://+:5100
EXPOSE 5100

ENTRYPOINT ["dotnet", "Smena.UpdateServer.dll"]
