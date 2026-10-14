FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY GreatLoveImmortalVenerable.csproj ./
RUN dotnet restore
COPY *.cs ./
RUN dotnet publish -c Release -o /out --no-restore
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out ./
USER 1654:1654
ENTRYPOINT ["dotnet", "GreatLoveImmortalVenerable.dll"]
