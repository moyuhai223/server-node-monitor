# syntax=docker/dockerfile:1
# Build context = repository root:  docker build -f deploy/docker/Dockerfile -t snm-master .

# ---- web assets ----
FROM node:22-alpine AS web
RUN apk add --no-cache bash
WORKDIR /src
COPY web/admin/package.json web/admin/package-lock.json* web/admin/
COPY web/public/package.json web/public/package-lock.json* web/public/
RUN cd web/admin && (npm ci --no-audit --no-fund || npm install --no-audit --no-fund) \
 && cd ../public && (npm ci --no-audit --no-fund || npm install --no-audit --no-fund)
COPY web/ web/
COPY scripts/build-web.sh scripts/
RUN bash scripts/build-web.sh --out /out/wwwroot --skip-install

# ---- master build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/SNM.Contracts/SNM.Contracts.csproj src/SNM.Contracts/
COPY src/SNM.Master/SNM.Master.csproj src/SNM.Master/
RUN dotnet restore src/SNM.Master/SNM.Master.csproj
COPY src/ src/
COPY --from=web /out/wwwroot src/SNM.Master/wwwroot
ARG VERSION=1.0.0
RUN dotnet publish src/SNM.Master/SNM.Master.csproj -c Release -o /app --no-restore -p:Version="${VERSION#v}"

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV SNM_LISTEN=http://0.0.0.0:5080 \
    SNM_DATA_DIR=/data \
    SNM_KNOWN_NETWORKS=172.16.0.0/12,10.0.0.0/8,192.168.0.0/16 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
RUN mkdir -p /data && chown $APP_UID:$APP_UID /data
VOLUME /data
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["dotnet", "snm-master.dll"]
