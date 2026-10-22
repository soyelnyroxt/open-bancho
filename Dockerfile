# open!bancho for DockHosting (or any Docker host). The data lives in the S3 bucket
# given by the S3_* environment variables, so the container keeps nothing of its own.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish OpenBancho.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
# some hosts run containers as an unprivileged user: let it write data/status.txt
RUN mkdir -p /app/data && chmod -R 777 /app
# the server listens on $PORT; hosts that set their own PORT override this
ENV PORT=8080
EXPOSE 8080
CMD ["dotnet", "open-bancho.dll"]
